// 模拟宿主: 在浏览器里回放 DLL 会发的各种消息, 前端脱离狐表开发。
// 状态存 sessionStorage, 刷新页面后照样要快照还原, 与真实 DLL 的行为一致。
// 问题里带"错误"回放失败, 带"慢"回放慢速输出 (测停止), 带"明细"回放查询明细, 带"新增"回放写入确认卡片,
// 带"填"或附了图片回放填窗口, 其余回放统计。
import type { Attachment, Config, HostEvent, Message, ResultTable, ToolCall, WebMessage } from "./protocol";
import type { Host } from "./host";

const KEY = "proai-mock-state";

interface MockState {
  messages: Message[];
  busy: boolean;
  seq: number;
  draft?: Attachment[];
}

const config: Config = {
  title: "销售助手 (模拟)",
  welcome: "你好，我可以帮你查询和统计系统里的数据。",
  theme: "#2563eb",
  placeholder: "输入问题，Enter 发送，Shift+Enter 换行",
  quick: ["本月销售额是多少？", "各产品的销售额（明细）", "新增一张订单", "把这张单填进订单录入", "制造一个错误"],
  images: true,
  files: true,
  voice: true,
  closable: true,
};

const salesTable: ResultTable = {
  title: "订单",
  columns: ["产品", "金额 合计", "行数"],
  rows: [
    ["笔记本", 199960, 3],
    ["显示器", 48063, 2],
    ["键盘", 6567, 4],
    ["鼠标", 5346, 4],
  ],
  total: 4,
};

const detailTable: ResultTable = {
  title: "订单",
  columns: ["订单号", "日期", "客户编号", "产品", "数量", "金额"],
  rows: Array.from({ length: 8 }, (_, i) => [
    "D00" + (i + 1),
    "2026-10-" + String(i * 3 + 1).padStart(2, "0"),
    "C0" + ((i % 5) + 1),
    ["笔记本", "显示器", "键盘", "鼠标"][i % 4],
    i + 2,
    (i + 2) * 199,
  ]),
  total: 23,
};

const answer = `**2026 年 10 月销售额合计 259,936.00 元**，各产品如下：

| 产品 | 销售额（元） | 订单数 |
|---|---:|---:|
| 笔记本 | 199,960.00 | 3 |
| 显示器 | 48,063.00 | 2 |
| 键盘 | 6,567.00 | 4 |
| 鼠标 | 5,346.00 | 4 |

- 笔记本占比最高，约 **76.9%**
- 统计口径：订单日期在 10 月内的全部订单

<script>alert('不应执行')</script>`;

export function createMockHost(): Host {
  const subs = new Set<(evt: HostEvent) => void>();
  let st: MockState = load();
  let timers: number[] = [];
  let current: string | null = null;

  function load(): MockState {
    try {
      const s = sessionStorage.getItem(KEY);
      if (s) return JSON.parse(s) as MockState;
    } catch {
      /* 忽略 */
    }
    return { messages: [], busy: false, seq: 0 };
  }

  function save() {
    sessionStorage.setItem(KEY, JSON.stringify(st));
  }

  function emit(evt: HostEvent) {
    apply(evt);
    save();
    // 模拟 WebView2 的异步投递
    setTimeout(() => subs.forEach((cb) => cb(evt)), 0);
  }

  // 模拟宿主自己也维护状态 (就像 DLL), 供快照用
  function apply(evt: HostEvent) {
    const find = (id: string) => st.messages.find((m) => m.id === id);
    switch (evt.type) {
      case "message":
        st.messages.push(structuredClone(evt.message));
        break;
      case "busy":
        st.busy = evt.busy;
        break;
      case "delta": {
        const m = find(evt.id);
        if (m) evt.kind === "answer" ? (m.text += evt.text) : (m.think = (m.think ?? "") + evt.text);
        break;
      }
      case "tool": {
        const m = find(evt.id);
        if (!m) break;
        m.tools = m.tools ?? [];
        const i = m.tools.findIndex((t) => t.id === evt.tool.id);
        if (i < 0) m.tools.push(structuredClone(evt.tool));
        else m.tools[i] = structuredClone(evt.tool);
        break;
      }
      case "update": {
        const m = find(evt.id);
        if (m) Object.assign(m, structuredClone(evt.patch));
        break;
      }
    }
  }

  function later(ms: number, f: () => void) {
    timers.push(window.setTimeout(f, ms));
  }

  function stream(id: string, kind: "think" | "answer", text: string, start: number, step: number, size = 3): number {
    let t = start;
    for (let i = 0; i < text.length; i += size) {
      const piece = text.slice(i, i + size);
      later(t, () => emit({ type: "delta", id, kind, text: piece }));
      t += step;
    }
    return t;
  }

  function finish(id: string, patch: Partial<Message>) {
    current = null;
    emit({ type: "update", id, patch });
    emit({ type: "busy", busy: false });
  }

  let confirmWait: ((ok: boolean) => void) | null = null;

  function send(text: string) {
    const draft = st.draft ?? [];
    if (st.busy || (!text.trim() && draft.length === 0)) return;
    const uid = "u" + ++st.seq;
    const aid = "a" + ++st.seq;
    current = aid;
    st.draft = [];
    emitDraft();
    emit({ type: "message", message: { id: uid, role: "user", text: text || "请看图片。", attachments: draft.length ? draft : undefined } });
    emit({ type: "message", message: { id: aid, role: "assistant", text: "", think: "", tools: [], status: "streaming" } });
    emit({ type: "busy", busy: true });

    if (text.includes("错误")) {
      later(800, () =>
        finish(aid, { status: "error", error: "模型服务拒绝访问, 请检查 API Key 是否正确、是否有权限。\n(状态码: 401)" }),
      );
      return;
    }
    if (text.includes("慢")) {
      const long = Array.from({ length: 60 }, (_, i) => `第 ${i + 1} 句，慢慢输出的内容。`).join("");
      const t = stream(aid, "answer", long, 300, 120, 4);
      later(t + 100, () => finish(aid, { status: "done" }));
      return;
    }
    if (text.includes("新增")) return replayWrite(aid);
    if (text.includes("出库")) return replayAction(aid);
    if (text.includes("填") || draft.some((a) => a.kind === "image")) return replayFill(aid);
    const detail = text.includes("明细");
    const tool: ToolCall = {
      id: aid + "-t1",
      name: detail ? "query_rows" : "aggregate",
      label: detail ? "查询 订单" : "统计 订单",
      status: "running",
    };
    let t = stream(aid, "think", "用户想看10月各产品销售额，用 aggregate 按产品分组求金额合计。", 200, 40);
    later(t, () => emit({ type: "tool", id: aid, tool }));
    t += 900;
    later(t, () =>
      emit({
        type: "tool",
        id: aid,
        tool: detail
          ? { ...tool, status: "done", summary: "共 23 行, 显示前 8 行", table: detailTable }
          : { ...tool, status: "done", summary: "13 行, 4 组", table: salesTable },
      }),
    );
    t = stream(aid, "answer", answer, t + 300, 30, 4);
    later(t + 100, () => finish(aid, { status: "done", usage: { prompt_tokens: 2142, completion_tokens: 263 } }));
  }

  function replayWrite(aid: string) {
    const tool: ToolCall = { id: aid + "-t1", name: "add_rows", label: "新增 订单", status: "running" };
    let t = stream(aid, "think", "用户要新增一张订单，信息齐全，直接调用 add_rows，系统会请用户确认。", 200, 30);
    later(t, () =>
      emit({
        type: "tool",
        id: aid,
        tool: {
          ...tool,
          status: "confirm",
          confirm: {
            kind: "add",
            title: "新增 1 行到 订单 (含明细 2 行)",
            confirmText: "确认新增",
            tables: [
              { title: "订单", columns: ["订单号", "日期", "客户编号"], rows: [["D0121", "2026-11-21", "C02"]], total: 1 },
              { title: "第 1 行的明细 · 订单明细", columns: ["产品", "数量", "单价"], rows: [["笔记本", 2, 4999], ["鼠标", 5, 99]], total: 2 },
            ],
            warnings: ["第 1 行 的 产品 \"平板\" 不在可选值里"],
          },
        },
      }),
    );
    confirmWait = (ok) => {
      confirmWait = null;
      const cur = st.messages.find((m) => m.id === aid)?.tools?.[0] ?? tool;
      emit({ type: "tool", id: aid, tool: { ...cur, status: "running", decision: ok ? "approved" : "rejected" } });
      later(600, () => {
        emit({ type: "tool", id: aid, tool: { ...cur, status: "done", decision: ok ? "approved" : "rejected", summary: ok ? "已新增 1 行" : "用户未确认", open: ok } });
        const t2 = stream(aid, "answer", ok ? "已新增订单 **D0121**（华北商行，2 条明细），已保存。" : "好的，没有新增。需要改哪里？", 300, 30, 4);
        later(t2 + 100, () => finish(aid, { status: "done" }));
      });
    };
  }

  function replayAction(aid: string) {
    const tool: ToolCall = { id: aid + "-t1", name: "fn_8c1e2f3a", label: "出库 笔记本", status: "running" };
    let t = stream(aid, "think", "用户要出库 3 台笔记本，用业务工具 出库，系统会先给用户看预览。", 200, 30);
    later(t, () => emit({ type: "tool", id: aid, tool }));
    t += 700;
    later(t, () =>
      emit({
        type: "tool",
        id: aid,
        tool: {
          ...tool,
          status: "confirm",
          confirm: {
            kind: "action",
            title: "出库",
            text: "从 总仓 出库 笔记本 3 台\n出库后库存: 12 → 9",
            confirmText: "确认出库",
            tables: [],
          },
        },
      }),
    );
    confirmWait = (ok) => {
      confirmWait = null;
      const cur = st.messages.find((m) => m.id === aid)?.tools?.[0] ?? tool;
      emit({ type: "tool", id: aid, tool: { ...cur, status: "running", decision: ok ? "approved" : "rejected" } });
      later(600, () => {
        emit({ type: "tool", id: aid, tool: { ...cur, status: "done", decision: ok ? "approved" : "rejected", summary: ok ? "已出库, 单号 CK0001" : "用户未确认" } });
        const t2 = stream(aid, "answer", ok ? "已出库笔记本 3 台，出库单号 **CK0001**，剩余库存 9 台。" : "好的，没有出库。", 300, 30, 4);
        later(t2 + 100, () => finish(aid, { status: "done" }));
      });
    };
  }

  function replayFill(aid: string) {
    const tool: ToolCall = { id: aid + "-t1", name: "fill_form", label: "填写窗口 订单录入", status: "running" };
    let t = stream(aid, "think", "图片是一张销售单：客户北方商行，两行明细。用 fill_form 填进订单录入窗口。", 200, 30);
    later(t, () => emit({ type: "tool", id: aid, tool }));
    t += 900;
    later(t, () => emit({ type: "tool", id: aid, tool: { ...tool, status: "done", summary: "已填 3 项, 明细 2 行" } }));
    t = stream(aid, "answer", "已把单据填进 **订单录入** 窗口（还没保存）：\n\n- 日期 2026-11-21\n- 客户 C02 北方商行\n- 明细 2 行\n\n请核对后点窗口里的 **保存**。", t + 300, 30, 4);
    later(t + 100, () => finish(aid, { status: "done" }));
  }

  function emitDraft() {
    save();
    const draft = structuredClone(st.draft ?? []);
    setTimeout(() => subs.forEach((cb) => cb({ type: "draft", draft })), 0);
  }

  function notice(text: string) {
    setTimeout(() => subs.forEach((cb) => cb({ type: "notice", text, level: "info" })), 0);
  }

  function cancel() {
    confirmWait = null;
    if (!current) return;
    timers.forEach(clearTimeout);
    timers = [];
    const m = st.messages.find((x) => x.id === current);
    const tools = (m?.tools ?? []).map((t) => (t.status === "running" || t.status === "confirm" ? { ...t, status: "cancelled" as const } : t));
    finish(current, { status: "cancelled", tools });
  }

  return {
    mock: true,
    post(msg: WebMessage) {
      switch (msg.type) {
        case "ready":
          if (st.busy && !current) {
            // 刷新时正在回答: 模拟宿主没法接着回放, 当作已停止
            const last = st.messages[st.messages.length - 1];
            if (last) Object.assign(last, { status: "cancelled" });
            st.busy = false;
            save();
          }
          setTimeout(() => {
            subs.forEach((cb) =>
              cb({ type: "snapshot", config, busy: st.busy, messages: structuredClone(st.messages), draft: structuredClone(st.draft ?? []) }),
            );
            // ?ask=问题: 打开就提问, 方便调界面和截图
            const ask = new URLSearchParams(location.search).get("ask");
            if (ask && st.messages.length === 0) send(ask);
          }, 50);
          break;
        case "send":
          send(msg.text);
          break;
        case "cancel":
          cancel();
          break;
        case "newChat":
          cancel();
          st = { messages: [], busy: false, seq: st.seq };
          save();
          subs.forEach((cb) => cb({ type: "snapshot", config, busy: false, messages: [], draft: [] }));
          break;
        case "confirm":
          confirmWait?.(msg.ok);
          break;
        case "open":
          notice("（模拟）已在狐表主界面筛出这些行");
          break;
        case "attach": {
          const a: Attachment =
            msg.kind === "image"
              ? { id: "f" + ++st.seq, name: msg.name, kind: "image", summary: "图片", thumb: msg.data }
              : { id: "f" + ++st.seq, name: msg.name, kind: "sheet", summary: "Sheet1 120 行" };
          st.draft = [...(st.draft ?? []), a];
          emitDraft();
          break;
        }
        case "detach":
          st.draft = (st.draft ?? []).filter((a) => a.id !== msg.id);
          emitDraft();
          break;
        case "voice": {
          // 模拟识别: 先确认是能解码的 16kHz WAV, 录音越长回的字越多
          const id = msg.id;
          const reply = (evt: { text?: string; error?: string }) => subs.forEach((cb) => cb({ type: "transcript", id, ...evt }));
          const bytes = Uint8Array.from(atob(msg.data), (c) => c.charCodeAt(0));
          const v = new DataView(bytes.buffer);
          const tag = (o: number) => String.fromCharCode(...bytes.subarray(o, o + 4));
          const ok =
            bytes.length > 44 && tag(0) === "RIFF" && tag(8) === "WAVE" && tag(36) === "data" &&
            v.getUint16(20, true) === 1 && v.getUint16(22, true) === 1 && v.getUint32(24, true) === 16000 &&
            v.getUint32(40, true) === bytes.length - 44;
          if (!ok) {
            reply({ error: "（模拟）录音不是 16kHz 单声道 WAV" });
            break;
          }
          const seconds = (bytes.length - 44) / 32000;
          console.info(`（模拟）收到录音 ${seconds.toFixed(2)} 秒`);
          setTimeout(() => reply({ text: seconds > 4 ? "帮我统计一下本月各产品的销售额，按金额从高到低排" : "本月销售额是多少" }), 900);
          break;
        }
        case "openUrl":
          window.open(msg.url, "_blank");
          break;
        case "close":
          notice("（模拟）已关闭侧栏");
          break;
      }
    },
    subscribe(cb) {
      subs.add(cb);
      return () => subs.delete(cb);
    },
  };
}
