// 语音输入: 网页录音, 转成 16kHz 单声道 WAV 交给 DLL, DLL 调开发者配置的语音识别接口, 文字回填到输入框。
import { useEffect, useRef, useState } from "react";
import { host } from "./host";
import type { HostEvent } from "./protocol";

const MAX_SECONDS = 60;
const MIN_SECONDS = 0.6;
const RATE = 16000;
const BARS = 32;

class Recorder {
  private chunks: Blob[] = [];
  private buf: Uint8Array<ArrayBuffer>;
  readonly started = performance.now();

  private constructor(
    private stream: MediaStream,
    private ctx: AudioContext,
    private analyser: AnalyserNode,
    private rec: MediaRecorder,
  ) {
    this.buf = new Uint8Array(analyser.fftSize);
    rec.ondataavailable = (e) => e.data.size && this.chunks.push(e.data);
    rec.start();
  }

  static async open(): Promise<Recorder> {
    if (!navigator.mediaDevices?.getUserMedia) throw new Error("当前环境不支持录音");
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    } catch (e) {
      throw new Error(micError(e as DOMException));
    }
    const ctx = new AudioContext();
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 1024;
    ctx.createMediaStreamSource(stream).connect(analyser);
    return new Recorder(stream, ctx, analyser, new MediaRecorder(stream));
  }

  get seconds() {
    return (performance.now() - this.started) / 1000;
  }

  /** 当前音量 0~1 */
  level(): number {
    this.analyser.getByteTimeDomainData(this.buf);
    let sum = 0;
    for (const v of this.buf) sum += ((v - 128) / 128) ** 2;
    return Math.min(1, Math.sqrt(Math.sqrt(sum / this.buf.length)) * 1.6);
  }

  /** 停止并返回 WAV 的 base64 */
  async stop(): Promise<string> {
    const done = new Promise<void>((r) => (this.rec.onstop = () => r()));
    this.rec.stop();
    await done;
    try {
      const raw = await new Blob(this.chunks, { type: this.rec.mimeType }).arrayBuffer();
      const decoded = await this.ctx.decodeAudioData(raw);
      const off = new OfflineAudioContext(1, Math.max(1, Math.ceil(decoded.duration * RATE)), RATE);
      const src = off.createBufferSource();
      src.buffer = decoded;
      src.connect(off.destination);
      src.start();
      const pcm = (await off.startRendering()).getChannelData(0);
      return toBase64(wav(pcm));
    } finally {
      this.release();
    }
  }

  cancel() {
    if (this.rec.state !== "inactive") this.rec.stop();
    this.release();
  }

  private release() {
    this.stream.getTracks().forEach((t) => t.stop());
    void this.ctx.close().catch(() => {});
  }
}

function micError(e: DOMException): string {
  switch (e?.name) {
    case "NotAllowedError":
    case "SecurityError":
      return "用不了麦克风：请在 Windows 设置 → 隐私和安全性 → 麦克风 中打开「让桌面应用访问麦克风」";
    case "NotFoundError":
      return "没有找到麦克风";
    case "NotReadableError":
      return "麦克风被其他程序占用了";
    default:
      return "打不开麦克风：" + (e?.message || e?.name || "未知原因");
  }
}

function wav(pcm: Float32Array): Uint8Array {
  const out = new DataView(new ArrayBuffer(44 + pcm.length * 2));
  const str = (o: number, s: string) => [...s].forEach((c, i) => out.setUint8(o + i, c.charCodeAt(0)));
  str(0, "RIFF");
  out.setUint32(4, 36 + pcm.length * 2, true);
  str(8, "WAVE");
  str(12, "fmt ");
  out.setUint32(16, 16, true);
  out.setUint16(20, 1, true); // PCM
  out.setUint16(22, 1, true); // 单声道
  out.setUint32(24, RATE, true);
  out.setUint32(28, RATE * 2, true);
  out.setUint16(32, 2, true);
  out.setUint16(34, 16, true);
  str(36, "data");
  out.setUint32(40, pcm.length * 2, true);
  for (let i = 0; i < pcm.length; i++) {
    const s = Math.max(-1, Math.min(1, pcm[i]));
    out.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Uint8Array(out.buffer);
}

function toBase64(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

export type VoicePhase = "idle" | "recording" | "transcribing";

/** 录音 → 识别 → onText。onNotice 显示提示。 */
export function useVoice(onText: (text: string) => void, onNotice: (text: string, level?: "error" | "info") => void) {
  const [phase, setPhase] = useState<VoicePhase>("idle");
  const [seconds, setSeconds] = useState(0);
  const [levels, setLevels] = useState<number[]>(() => Array(BARS).fill(0));
  const rec = useRef<Recorder | null>(null);
  const pending = useRef<string | null>(null);
  const cb = useRef({ onText, onNotice });
  cb.current = { onText, onNotice };

  // 识别结果
  useEffect(() => {
    const handle = (evt: HostEvent) => {
      if (evt.type === "batch") return evt.events.forEach(handle);
      if (evt.type !== "transcript" || evt.id !== pending.current) return;
      pending.current = null;
      setPhase("idle");
      if (evt.error) cb.current.onNotice(evt.error);
      else if (!evt.text?.trim()) cb.current.onNotice("没听清，请再说一次", "info");
      else cb.current.onText(evt.text.trim());
    };
    return host.subscribe(handle);
  }, []);

  // 录音时刷新音量和时长, 到时自动结束
  useEffect(() => {
    if (phase !== "recording") return;
    let raf = 0;
    let last = 0;
    const tick = (t: number) => {
      const r = rec.current;
      if (!r) return;
      if (t - last > 70) {
        last = t;
        const lv = r.level();
        setLevels((a) => [...a.slice(1), lv]);
        setSeconds(r.seconds);
        if (r.seconds >= MAX_SECONDS) {
          void finish();
          return;
        }
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  useEffect(() => () => rec.current?.cancel(), []);

  async function start() {
    if (phase !== "idle") return;
    try {
      rec.current = await Recorder.open();
    } catch (e) {
      cb.current.onNotice((e as Error).message);
      return;
    }
    setSeconds(0);
    setLevels(Array(BARS).fill(0));
    setPhase("recording");
  }

  async function finish() {
    const r = rec.current;
    if (!r) return;
    rec.current = null;
    if (r.seconds < MIN_SECONDS) {
      r.cancel();
      setPhase("idle");
      cb.current.onNotice("说话时间太短", "info");
      return;
    }
    setPhase("transcribing");
    try {
      const data = await r.stop();
      const id = "v" + Date.now();
      pending.current = id;
      host.post({ type: "voice", id, data });
    } catch (e) {
      setPhase("idle");
      cb.current.onNotice("录音处理失败：" + (e as Error).message);
    }
  }

  function cancel() {
    rec.current?.cancel();
    rec.current = null;
    pending.current = null;
    setPhase("idle");
  }

  return { phase, seconds, levels, start, finish, cancel };
}
