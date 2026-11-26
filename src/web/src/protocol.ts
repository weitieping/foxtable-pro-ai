// 网页与 DLL 之间的消息。对话状态由 DLL 保管, 网页只显示; 刷新后发 ready 要一份快照。
// 与 FoxTablePro.AI/Chat/ChatSession.vb 保持一致。

export interface Config {
  title: string;
  welcome: string;
  theme: string;
  placeholder: string;
  quick: string[];
  /** 能不能发图片 / 表格文件 */
  images?: boolean;
  files?: boolean;
  /** 配了语音识别接口, 显示麦克风按钮 */
  voice?: boolean;
  /** 能出图 (ai.AllowCharts); false 时不显示任何图表 */
  charts?: boolean;
  /** 停靠在主窗口侧栏里, 顶栏显示关闭按钮 */
  closable?: boolean;
}

export interface Attachment {
  id: string;
  name: string;
  kind: "image" | "sheet";
  summary?: string;
  /** 图片: data URL */
  thumb?: string;
}

/** 写入前的确认卡片 */
export interface ConfirmPreview {
  /** action: 第 4 档的业务工具 (内部函数) */
  kind: "add" | "update" | "import" | "action";
  title: string;
  /** 函数返回的文字预览 */
  text?: string;
  tables: ResultTable[];
  warnings?: string[];
  confirmText?: string;
}

export interface ResultTable {
  title: string;
  columns: string[];
  rows: (string | number | boolean | null)[][];
  total: number;
}

/** 图表类型。与 FoxTablePro.AI/Data/ChartBuilder.vb 一致 */
export type ChartType = "bar" | "bar_horizontal" | "bar_stacked" | "line" | "area" | "pie" | "donut" | "combo";

export interface ChartSeries {
  name: string;
  /** 与 categories 一一对应, 没有数据为 null */
  data: (number | null)[];
  /** 1: 组合图里画在右轴的折线 */
  axis?: 1;
}

/** 统计结果画成的图。数字都由 DLL 从统计结果里取, 模型只决定画法 */
export interface ChartData {
  type: ChartType;
  /** 数据形状允许切换到的类型 */
  types: ChartType[];
  title: string;
  /** 横轴 (饼图为分块) 的名字 */
  x: string;
  categories: string[];
  series: ChartSeries[];
  /** 拆成多条系列时, 数值是哪个统计项 */
  y?: string;
  note?: string;
}

export type ToolStatus = "running" | "confirm" | "done" | "error" | "cancelled";

export interface ToolCall {
  id: string;
  name: string;
  label: string;
  status: ToolStatus;
  summary?: string;
  table?: ResultTable;
  /** 统计时附带的图表 */
  chart?: ChartData;
  confirm?: ConfirmPreview;
  decision?: "approved" | "rejected" | "timeout";
  /** 可以在狐表中打开 */
  open?: boolean;
}

export type MessageStatus = "streaming" | "done" | "error" | "cancelled";

export interface Message {
  id: string;
  role: "user" | "assistant";
  text: string;
  think?: string;
  tools?: ToolCall[];
  status?: MessageStatus;
  error?: string;
  retry?: number;
  usage?: Record<string, unknown>;
  attachments?: Attachment[];
}

/** DLL → 网页 */
export type HostEvent =
  | { type: "snapshot"; config: Config; busy: boolean; messages: Message[]; draft?: Attachment[] }
  | { type: "draft"; draft: Attachment[] }
  | { type: "notice"; text: string; level?: "error" | "info" }
  | { type: "config"; config: Config }
  | { type: "busy"; busy: boolean }
  | { type: "message"; message: Message }
  | { type: "delta"; id: string; kind: "think" | "answer"; text: string }
  | { type: "tool"; id: string; tool: ToolCall }
  | { type: "update"; id: string; patch: Partial<Message> }
  /** 语音识别结果, id 对应网页发的 voice */
  | { type: "transcript"; id: string; text?: string; error?: string }
  | { type: "batch"; events: HostEvent[] };

/** 网页 → DLL */
export type WebMessage =
  | { type: "ready" }
  | { type: "send"; text: string }
  | { type: "cancel" }
  | { type: "newChat" }
  | { type: "confirm"; id: string; ok: boolean }
  | { type: "open"; id: string }
  | { type: "attach"; name: string; kind: "image" | "file"; data: string }
  | { type: "detach"; id: string }
  /** 录音 (16kHz 单声道 WAV 的 base64), 识别完回 transcript */
  | { type: "voice"; id: string; data: string }
  | { type: "openUrl"; url: string }
  | { type: "close" };

export interface Notice {
  id: number;
  text: string;
  level: "error" | "info";
}

/** 网页自己的动作 (不经 DLL) */
export type LocalAction = { type: "_dismiss"; id: number } | { type: "_notice"; text: string; level?: "error" | "info" };

export interface State {
  connected: boolean;
  config: Config;
  busy: boolean;
  messages: Message[];
  draft: Attachment[];
  notices: Notice[];
}

export const initialState: State = {
  connected: false,
  config: { title: "AI 助手", welcome: "", theme: "#2563eb", placeholder: "", quick: [] },
  busy: false,
  messages: [],
  draft: [],
  notices: [],
};

let noticeSeq = 0;

function patchMessage(state: State, id: string, f: (m: Message) => Message): State {
  return { ...state, messages: state.messages.map((m) => (m.id === id ? f(m) : m)) };
}

export function reduce(state: State, evt: HostEvent | LocalAction): State {
  switch (evt.type) {
    case "batch":
      return evt.events.reduce<State>(reduce, state);
    case "snapshot":
      return { ...state, connected: true, config: evt.config, busy: evt.busy, messages: evt.messages, draft: evt.draft ?? [] };
    case "draft":
      return { ...state, draft: evt.draft };
    case "notice":
    case "_notice":
      return { ...state, notices: [...state.notices.slice(-2), { id: ++noticeSeq, text: evt.text, level: evt.level ?? "error" }] };
    case "_dismiss":
      return { ...state, notices: state.notices.filter((n) => n.id !== evt.id) };
    case "config":
      return { ...state, config: evt.config };
    case "busy":
      return { ...state, busy: evt.busy };
    case "message":
      return { ...state, messages: [...state.messages, evt.message] };
    case "delta":
      return patchMessage(state, evt.id, (m) =>
        evt.kind === "answer" ? { ...m, text: m.text + evt.text } : { ...m, think: (m.think ?? "") + evt.text },
      );
    case "tool":
      return patchMessage(state, evt.id, (m) => {
        const tools = m.tools ?? [];
        const i = tools.findIndex((t) => t.id === evt.tool.id);
        return { ...m, tools: i < 0 ? [...tools, evt.tool] : tools.map((t, j) => (j === i ? evt.tool : t)) };
      });
    case "update":
      return patchMessage(state, evt.id, (m) => ({ ...m, ...evt.patch }));
    default:
      return state;
  }
}
