import { useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from "react";
import { host } from "./host";
import { renderMarkdown } from "./markdown";
import {
  initialState,
  reduce,
  type Attachment,
  type Config,
  type ConfirmPreview,
  type Message,
  type ResultTable,
  type State,
  type ToolCall,
} from "./protocol";
import { readAttachment } from "./files";
import { useVoice } from "./voice";
import { IconArrowUp, IconCheck, IconClose, IconMic, IconNewChat, IconPaperclip, IconSheet, IconSparkle } from "./icons";

declare global {
  interface Window {
    /** 自测用: 读状态、走与发送按钮相同的路径发问题、点确认卡片 */
    __proai?: {
      state: () => State;
      send: (text: string) => void;
      confirm: (id: string, ok: boolean) => void;
      open: (id: string) => void;
    };
  }
}

export function App() {
  const [state, dispatch] = useReducer(reduce, initialState);
  const stateRef = useRef(state);
  stateRef.current = state;
  const [dragging, setDragging] = useState(false);

  useEffect(() => {
    const off = host.subscribe(dispatch);
    host.post({ type: "ready" });
    window.__proai = {
      state: () => stateRef.current,
      send: (text) => host.post({ type: "send", text }),
      confirm: (id, ok) => host.post({ type: "confirm", id, ok }),
      open: (id) => host.post({ type: "open", id }),
    };
    return off;
  }, []);

  useEffect(() => {
    document.title = state.config.title || "AI 助手";
  }, [state.config.title]);

  // 提示条几秒后自动消失
  useEffect(() => {
    if (state.notices.length === 0) return;
    const n = state.notices[0];
    const t = window.setTimeout(() => dispatch({ type: "_dismiss", id: n.id }), 5000);
    return () => clearTimeout(t);
  }, [state.notices]);

  const theme = state.config.theme || "#2563eb";
  const canAttach = !!(state.config.images || state.config.files);
  const send = (text: string) => {
    if ((!text.trim() && state.draft.length === 0) || state.busy) return;
    host.post({ type: "send", text });
  };
  const addFiles = async (files: FileList | File[]) => {
    for (const f of Array.from(files)) {
      try {
        host.post(await readAttachment(f, state.config));
      } catch (e) {
        dispatch({ type: "_notice", text: `「${f.name}」没有附上: ${(e as Error).message}` });
      }
    }
  };

  return (
    <div
      className="app"
      style={{ ["--theme" as string]: theme }}
      onDragOver={(e) => {
        if (!canAttach || !e.dataTransfer.types.includes("Files")) return;
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target || !e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false);
      }}
      onDrop={(e) => {
        if (!canAttach) return;
        e.preventDefault();
        setDragging(false);
        if (e.dataTransfer.files.length) void addFiles(e.dataTransfer.files);
      }}
    >
      <header className="bar">
        <span className="logo">
          <IconSparkle size={14} />
        </span>
        <span className="title">{state.config.title}</span>
        {host.mock && <span className="badge">模拟宿主</span>}
        <button
          className="icon-btn"
          title="新对话"
          disabled={state.messages.length === 0 && !state.busy}
          onClick={() => host.post({ type: "newChat" })}
        >
          <IconNewChat />
        </button>
        {state.config.closable && (
          <button className="icon-btn" title="关闭" onClick={() => host.post({ type: "close" })}>
            <IconClose />
          </button>
        )}
      </header>
      <MessageList state={state} onSend={send} />
      {state.notices.length > 0 && (
        <div className="notices">
          {state.notices.map((n) => (
            <div key={n.id} className={"notice " + n.level} onClick={() => dispatch({ type: "_dismiss", id: n.id })}>
              {n.text}
            </div>
          ))}
        </div>
      )}
      <Composer
        busy={state.busy}
        disabled={!state.connected}
        config={state.config}
        draft={state.draft}
        onSend={send}
        onFiles={addFiles}
        onNotice={(text, level) => dispatch({ type: "_notice", text, level })}
      />
      {dragging && <div className="drop">松开即可附上{state.config.images ? "图片" : ""}{state.config.images && state.config.files ? "或" : ""}{state.config.files ? "表格" : ""}</div>}
    </div>
  );
}

function MessageList({ state, onSend }: { state: State; onSend: (text: string) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const stick = useRef(true);

  // 停在底部时跟着新内容滚动; 用户往上翻了就不打扰
  const onScroll = () => {
    const el = ref.current!;
    stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
  };
  useLayoutEffect(() => {
    const el = ref.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  });
  const lastLen = state.messages.length;
  useEffect(() => {
    stick.current = true;
  }, [lastLen]);

  if (!state.connected)
    return (
      <div className="list empty">
        <span className="spin" /> 正在连接…
      </div>
    );

  return (
    <div className="list" ref={ref} onScroll={onScroll}>
      <div className="column">
      {state.messages.length === 0 ? (
        <Welcome title={state.config.title} text={state.config.welcome} quick={state.config.quick} onSend={onSend} />
      ) : (
        state.messages.map((m, i) =>
          m.role === "user" ? (
            <div key={m.id} className="msg user">
              {m.attachments && m.attachments.length > 0 && <AttachmentList items={m.attachments} />}
              <div className="bubble">{m.text}</div>
            </div>
          ) : (
            <AssistantMessage
              key={m.id}
              m={m}
              onRetry={() => {
                const q = state.messages[i - 1];
                if (q?.role === "user") onSend(q.text);
              }}
              canRetry={!state.busy}
            />
          ),
        )
      )}
      </div>
    </div>
  );
}

function AttachmentList({ items, onRemove }: { items: Attachment[]; onRemove?: (id: string) => void }) {
  return (
    <div className="attachments">
      {items.map((a) =>
        a.kind === "image" && a.thumb ? (
          <div key={a.id} className="att image" title={a.name}>
            <img src={a.thumb} alt={a.name} />
            {onRemove && (
              <button className="att-x" title="移除" onClick={() => onRemove(a.id)}>
                <IconClose />
              </button>
            )}
          </div>
        ) : (
          <div key={a.id} className="att file" title={a.summary}>
            <span className="att-icon">
              <IconSheet />
            </span>
            <span className="att-name">{a.name}</span>
            {a.summary && <span className="att-sum">{a.summary}</span>}
            {onRemove && (
              <button className="att-x" title="移除" onClick={() => onRemove(a.id)}>
                <IconClose />
              </button>
            )}
          </div>
        ),
      )}
    </div>
  );
}

function Welcome({ title, text, quick, onSend }: { title: string; text: string; quick: string[]; onSend: (t: string) => void }) {
  return (
    <div className="welcome">
      <div className="hero">
        <IconSparkle size={26} />
      </div>
      <h1>{title || "AI 助手"}</h1>
      {text && <p>{text}</p>}
      {quick.length > 0 && (
        <div className="quick">
          {quick.map((q) => (
            <button key={q} onClick={() => onSend(q)}>
              {q}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function AssistantMessage({ m, onRetry, canRetry }: { m: Message; onRetry: () => void; canRetry: boolean }) {
  const streaming = m.status === "streaming";
  const html = useMemo(() => (m.text ? renderMarkdown(m.text) : ""), [m.text]);
  const waiting = streaming && !m.text && !m.think && !(m.tools?.length);

  return (
    <div className="msg assistant">
      {m.think ? <Think text={m.think} active={streaming && !m.text} /> : null}
      {m.tools?.map((t) => <ToolLine key={t.id} t={t} />)}
      {html && <div className={"md" + (streaming ? " streaming" : "")} dangerouslySetInnerHTML={{ __html: html }} />}
      {waiting && (
        <div className="pending">
          <span className="dots">
            <i />
            <i />
            <i />
          </span>
          {m.retry ? `连接不稳定，正在第 ${m.retry} 次重试…` : "正在思考…"}
        </div>
      )}
      {m.status === "error" && (
        <div className="error">
          <div>{m.error || "回答失败。"}</div>
          {canRetry && (
            <button className="ghost small" onClick={onRetry}>
              重试
            </button>
          )}
        </div>
      )}
      {m.status === "cancelled" && <div className="note">已停止</div>}
    </div>
  );
}

function Think({ text, active }: { text: string; active: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <div className={"think" + (open ? " open" : "")}>
      <button className="think-head" onClick={() => setOpen(!open)}>
        <span className={"chev" + (open ? " down" : "")}>›</span>
        {active ? "思考中…" : "已思考"}
      </button>
      {open && <div className="think-body">{text}</div>}
    </div>
  );
}

function ToolLine({ t }: { t: ToolCall }) {
  const [open, setOpen] = useState(false);
  if (t.confirm) return <ConfirmCard t={t} c={t.confirm} />;
  const icon = t.status === "running" ? <span className="spin" /> : t.status === "done" ? <IconCheck size={13} /> : t.status === "error" ? "!" : "–";
  const hasTable = !!t.table && t.table.rows.length > 0;
  return (
    <div className={"tool " + t.status}>
      <div className="tool-row">
        <button className="tool-head" disabled={!hasTable} onClick={() => setOpen(!open)} title={hasTable ? "查看结果" : undefined}>
          <span className="tool-icon">{icon}</span>
          <span className="tool-label">{t.status === "running" ? "正在" + t.label + "…" : t.label}</span>
          {t.summary && <span className="tool-summary">{t.summary}</span>}
          {hasTable && <span className={"chev" + (open ? " down" : "")}>›</span>}
        </button>
        {t.open && t.status === "done" && <OpenButton id={t.id} />}
      </div>
      {open && t.table && <TableView table={t.table} />}
    </div>
  );
}

function OpenButton({ id }: { id: string }) {
  return (
    <button className="open-btn" title="在狐表主界面里筛出这些行" onClick={() => host.post({ type: "open", id })}>
      在狐表中打开
    </button>
  );
}

/** 写入前的确认卡片: 等确认时可以点; 确认、取消之后折叠成一行, 点开还能看 */
function ConfirmCard({ t, c }: { t: ToolCall; c: ConfirmPreview }) {
  const waiting = t.status === "confirm";
  const [expanded, setExpanded] = useState(false);
  const [sent, setSent] = useState(false);
  const show = waiting || expanded;
  const decided =
    t.decision === "approved" ? "已确认" : t.decision === "rejected" ? "已取消" : t.decision === "timeout" ? "超时未确认" : t.status === "cancelled" ? "已停止" : "";
  const icon =
    t.status === "running" ? <span className="spin" />
    : waiting ? "?"
    : t.decision === "rejected" || t.decision === "timeout" ? "×"
    : t.status === "done" ? <IconCheck size={13} />
    : t.status === "error" ? "!"
    : "–";
  const decide = (ok: boolean) => {
    if (sent) return;
    setSent(true);
    host.post({ type: "confirm", id: t.id, ok });
  };
  return (
    <div className={"ccard " + t.status + (t.decision ? " " + t.decision : "")}>
      <div className="tool-row">
        <button className="confirm-head" disabled={waiting} onClick={() => setExpanded(!expanded)}>
          <span className="tool-icon">{icon}</span>
          <span className="confirm-title">{c.title}</span>
          {!waiting && <span className="tool-summary">{t.status === "running" ? "正在写入…" : t.summary || decided}</span>}
          {!waiting && <span className={"chev" + (expanded ? " down" : "")}>›</span>}
        </button>
        {t.open && t.status === "done" && <OpenButton id={t.id} />}
      </div>
      {show && (
        <div className="confirm-body">
          {c.text && <div className="confirm-text">{c.text}</div>}
          {c.tables.map((tbl, i) => (
            <div key={i} className="confirm-table">
              {c.tables.length > 1 && <div className="confirm-sub">{tbl.title}</div>}
              <TableView table={tbl} />
            </div>
          ))}
          {c.warnings && c.warnings.length > 0 && (
            <ul className="warnings">
              {c.warnings.map((w, i) => (
                <li key={i}>{w}</li>
              ))}
            </ul>
          )}
          {waiting && (
            <div className="confirm-actions">
              <span className="confirm-hint">核对无误后确认，确认后才会{c.kind === "action" ? "执行" : "写入"}</span>
              <button className="ghost" disabled={sent} onClick={() => decide(false)}>
                取消
              </button>
              <button className="primary" disabled={sent} onClick={() => decide(true)}>
                {c.confirmText || "确认"}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

const fmt = (v: string | number | boolean | null) =>
  v === null ? "" : typeof v === "number" ? v.toLocaleString("zh-CN", { maximumFractionDigits: 4 }) : typeof v === "boolean" ? (v ? "是" : "否") : v;

function TableView({ table }: { table: ResultTable }) {
  return (
    <div className="result">
      <div className="md-table">
        <table>
          <thead>
            <tr>
              {table.columns.map((c) => (
                <th key={c}>{c}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((r, i) => (
              <tr key={i}>
                {r.map((v, j) => (
                  <td key={j} className={typeof v === "number" ? "num" : undefined}>
                    {fmt(v)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {table.total > table.rows.length && (
        <div className="note">
          共 {table.total} 条，显示前 {table.rows.length} 条
        </div>
      )}
    </div>
  );
}

function Composer(props: {
  busy: boolean;
  disabled: boolean;
  config: Config;
  draft: Attachment[];
  onSend: (t: string) => void;
  onFiles: (files: File[]) => void;
  onNotice: (text: string, level?: "error" | "info") => void;
}) {
  const [text, setText] = useState("");
  const ref = useRef<HTMLTextAreaElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  // 开始录音时的光标位置, 识别出的文字插在这里
  const caret = useRef<[number, number] | null>(null);
  const { images, files, voice } = props.config;
  const accept = [images ? "image/*" : "", files ? ".xlsx,.xls,.csv" : ""].filter(Boolean).join(",");
  const canSend = (!!text.trim() || props.draft.length > 0) && !props.disabled;

  const mic = useVoice((said) => {
    setText((t) => {
      const [a, b] = caret.current ?? [t.length, t.length];
      const next = t.slice(0, a) + said + t.slice(b);
      const pos = a + said.length;
      requestAnimationFrame(() => {
        const el = ref.current;
        if (!el) return;
        el.focus();
        el.setSelectionRange(pos, pos);
      });
      return next;
    });
  }, props.onNotice);
  const listening = mic.phase !== "idle";

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = Math.min(el.scrollHeight, 180) + "px";
  }, [text, listening]);

  // 录音、识别时 Esc 取消, 录音时 Enter 结束
  useEffect(() => {
    if (!listening) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") mic.cancel();
      else if (e.key === "Enter" && mic.phase === "recording") {
        e.preventDefault();
        void mic.finish();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const submit = () => {
    if (props.busy || !canSend) return;
    props.onSend(text.trim());
    setText("");
  };

  const startVoice = () => {
    const el = ref.current;
    caret.current = el ? [el.selectionStart, el.selectionEnd] : null;
    void mic.start();
  };

  return (
    <div className="composer-wrap">
      {props.draft.length > 0 && <AttachmentList items={props.draft} onRemove={(id) => host.post({ type: "detach", id })} />}
      <div className={"composer" + (listening ? " listening" : "")}>
        {listening ? (
          <div className="voice-bar">
            {mic.phase === "recording" ? (
              <>
                <span className="rec-dot" />
                <span className="wave">
                  {mic.levels.map((v, i) => (
                    <i key={i} style={{ transform: `scaleY(${0.12 + v * 0.88})` }} />
                  ))}
                </span>
                <span className="rec-time">
                  {Math.floor(mic.seconds / 60)}:{String(Math.floor(mic.seconds % 60)).padStart(2, "0")}
                </span>
              </>
            ) : (
              <span className="voice-wait">
                <span className="spin" /> 正在识别…
              </span>
            )}
          </div>
        ) : (
          <textarea
            ref={ref}
            rows={1}
            value={text}
            disabled={props.disabled}
            placeholder={props.config.placeholder || "输入问题"}
            onChange={(e) => setText(e.target.value)}
            onPaste={(e) => {
              if (!images) return;
              const pics = Array.from(e.clipboardData.files).filter((f) => f.type.startsWith("image/"));
              if (pics.length) {
                e.preventDefault();
                props.onFiles(pics);
              }
            }}
            onKeyDown={(e) => {
              // 输入法选字时的回车不算发送
              if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing && e.keyCode !== 229) {
                e.preventDefault();
                submit();
              }
            }}
          />
        )}
        <div className="toolbar">
          {listening ? (
            <>
              <button className="tool-btn" title="取消 (Esc)" onClick={mic.cancel}>
                <IconClose />
              </button>
              <span className="toolbar-hint">{mic.phase === "recording" ? "说完点 ✓ 或按 Enter" : ""}</span>
              <button className="send" title="说完了 (Enter)" disabled={mic.phase !== "recording"} onClick={() => void mic.finish()}>
                <IconCheck />
              </button>
            </>
          ) : (
            <>
              {(images || files) && (
                <>
                  <button
                    className="tool-btn"
                    title={images && files ? "添加图片或表格" : images ? "添加图片" : "添加表格"}
                    disabled={props.disabled}
                    onClick={() => fileRef.current?.click()}
                  >
                    <IconPaperclip />
                  </button>
                  <input
                    ref={fileRef}
                    type="file"
                    multiple
                    accept={accept}
                    hidden
                    onChange={(e) => {
                      if (e.target.files) props.onFiles(Array.from(e.target.files));
                      e.target.value = "";
                    }}
                  />
                </>
              )}
              {voice && (
                <button className="tool-btn" title="语音输入" disabled={props.disabled} onClick={startVoice}>
                  <IconMic />
                </button>
              )}
              <span className="toolbar-hint" />
              {props.busy ? (
                <button className="send stop" title="停止" onClick={() => host.post({ type: "cancel" })}>
                  <span className="square" />
                </button>
              ) : (
                <button className="send" title="发送 (Enter)" disabled={!canSend} onClick={submit}>
                  <IconArrowUp />
                </button>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
