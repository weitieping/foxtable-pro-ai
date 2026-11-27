// 模拟宿主: 在浏览器里回放 DLL 会发的各种消息, 前端脱离狐表开发。
// 状态存 sessionStorage, 刷新页面后照样要快照还原, 与真实 DLL 的行为一致。
// 问题里带"错误"回放失败, 带"慢"回放慢速输出 (测停止), 带"明细"回放查询明细, 带"新增"回放写入确认卡片,
// 带"填"或附了图片回放填窗口, 带"图表"回放每种图表各一张 (?ask=图表 打开就看), 其余回放统计。
// 统计、明细的表格卡片上有 "画图": 点了按表格里的数现画一张 (与 DLL 的 ChartBuilder.Auto 同一思路)。
import type { Attachment, ChartData, Config, HostEvent, Message, ResultTable, ToolCall, WebMessage } from "./protocol";
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
  quick: ["图表样例", "本月销售额是多少？", "各产品的销售额（明细）", "新增一张订单", "把这张单填进订单录入", "制造一个错误"],
  images: true,
  files: true,
  voice: true,
  closable: true,
  charts: true,
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
    if (text.includes("图表")) return replayCharts(aid);
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
          ? { ...tool, status: "done", summary: "共 23 行, 显示前 8 行", table: detailTable, result: "r" + st.seq, chartable: true }
          : { ...tool, status: "done", summary: "13 行, 4 组", table: salesTable, result: "r" + st.seq, chartable: true },
      }),
    );
    t = stream(aid, "answer", answer, t + 300, 30, 4);
    later(t + 100, () => finish(aid, { status: "done", usage: { prompt_tokens: 2142, completion_tokens: 263 } }));
  }

  function replayCharts(aid: string) {
    let t = stream(aid, "think", "用户想看各种图表的样子：每种类型各统计一次并附上 chart。", 200, 30);
    chartSamples().forEach(([label, chart], i) => {
      const tool: ToolCall = { id: `${aid}-t${i + 1}`, name: "aggregate", label, status: "running" };
      later(t, () => emit({ type: "tool", id: aid, tool }));
      t += 250;
      const table = chartTable(chart);
      later(t, () => emit({ type: "tool", id: aid, tool: { ...tool, status: "done", summary: `${chart.categories.length} 组`, table, chart } }));
      t += 100;
    });
    t = stream(
      aid,
      "answer",
      "以上是每种图表的样例：折线 (3 月没有订单, 补为 0)、面积 (365 天, 可拖动缩放条)、柱状、横向柱状、堆叠柱状、饼图 (30 个客户, 前 8 块之外合并为“其他”)、环形、组合图、" +
        "热力图 (月份 × 星期)、漏斗 (按状态流程)、帕累托 (二八分析)、指标卡 (实际与目标)、散点、矩形树图、雷达和瀑布图。",
      t + 200,
      20,
      6,
    );
    later(t + 100, () => finish(aid, { status: "done" }));
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

  /** 表格卡片上的 "画图": 第一列做横轴, 数字列做数值 */
  function drawChart(id: string) {
    for (const m of st.messages) {
      const tool = m.tools?.find((x) => x.id === id);
      if (!tool?.table) continue;
      const chart = autoChart(tool.table);
      if (!chart) return notice("没能画图: 表格里没有可以画的数字列");
      emit({ type: "tool", id: m.id, tool: { ...tool, chart, chartable: false } });
      return;
    }
    notice("这个结果已经不能画图了");
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
        case "chart":
          drawChart(msg.id);
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

// ---------- 图表样例 ----------

const months = Array.from({ length: 12 }, (_, i) => `2026-${String(i + 1).padStart(2, "0")}`);
const monthSales = [182300, 164500, 201800, 226400, 248900, 273100, 259800, 281600, 305200, 296700, 331500, 358900];
const monthOrders = [96, 88, 104, 117, 125, 139, 131, 142, 151, 149, 163, 176];

function chartSamples(): [string, ChartData][] {
  const singles = ["bar", "bar_horizontal", "line", "area", "pie", "donut"] as const;
  const days = Array.from({ length: 365 }, (_, i) => new Date(Date.UTC(2026, 0, 1 + i)).toISOString().slice(0, 10));
  const daySales = days.map((_, i) => Math.round(6000 + 2500 * Math.sin(i / 9) + 1800 * Math.sin(i / 2.3) + i * 12));
  const reps = ["张伟", "王芳", "李娜", "刘洋", "陈静", "杨磊", "赵敏", "黄晓明", "周杰", "吴婷婷", "郑凯", "孙悦"];
  const customers = Array.from({ length: 30 }, (_, i) => `客户${String(i + 1).padStart(2, "0")}`);
  const cats = ["家电", "服装", "食品", "数码", "家居"];
  return [
    ["统计 订单 · 每月销售额", {
      type: "line", types: [...singles], title: "2026 年每月销售额", x: "日期(月)", categories: months,
      series: [{ name: "金额 合计", data: monthSales }],
    }],
    ["统计 订单 · 每天销售额", {
      type: "area", types: [...singles], title: "每天销售额 (365 天)", x: "日期(日)", categories: days,
      series: [{ name: "金额 合计", data: daySales }],
    }],
    ["统计 订单 · 各类别销售额", {
      type: "bar", types: [...singles], title: "各产品类别销售额排名", x: "产品类别", categories: ["家电", "数码", "服装", "家居", "食品"],
      series: [{ name: "金额 合计", data: [1186500, 902300, 534800, 318900, 147600] }],
    }],
    ["统计 订单 · 各业务员销售额", {
      type: "bar_horizontal", types: [...singles], title: "各业务员销售额", x: "业务员", categories: reps,
      series: [{ name: "金额 合计", data: reps.map((_, i) => 420000 - i * 27500 + (i % 3) * 8000) }],
    }],
    ["统计 订单 · 各月各类别销售额", {
      type: "bar_stacked", types: ["line", "bar", "bar_stacked", "bar_horizontal", "area"], title: "金额 合计 · 按 日期(月)、产品类别",
      x: "日期(月)", y: "金额 合计", categories: months,
      series: cats.map((name, k) => ({ name, data: monthSales.map((v, i) => (k === 4 && i === 2 ? null : Math.round((v * [0.36, 0.27, 0.17, 0.12, 0.08][k] * (1 + ((i + k) % 4) / 20))))) })),
    }],
    ["统计 订单 · 各客户销售额占比", {
      type: "pie", types: [...singles], title: "各客户销售额占比 (30 个客户)", x: "客户", categories: customers,
      series: [{ name: "金额 合计", data: customers.map((_, i) => Math.round(260000 / (i + 1.6))) }],
    }],
    ["统计 订单 · 各类别订单数占比", {
      type: "donut", types: [...singles], title: "各类别订单数占比", x: "产品类别", categories: cats,
      series: [{ name: "订单数", data: [412, 356, 298, 187, 96] }],
    }],
    ["统计 订单 · 每月销售额和订单数", {
      type: "combo", types: ["combo", "bar", "bar_horizontal", "line"], title: "每月销售额和订单数", x: "日期(月)", categories: months,
      series: [{ name: "金额 合计", data: monthSales }, { name: "订单数", data: monthOrders, axis: 1 }],
    }],
    ["统计 订单 · 按星期统计订单数", {
      type: "bar", types: [...singles], title: "按星期统计订单数", x: "日期(星期)",
      categories: ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"],
      series: [{ name: "行数", data: [231, 248, 226, 259, 274, 162, 118] }],
    }],
    // ---------- M2 ----------
    ["统计 订单 · 每月销售额 (3 月没有订单)", {
      type: "line", types: [...singles], title: "每月销售额 (缺的月份补 0)", x: "日期(月)", categories: months.slice(0, 6),
      series: [{ name: "金额 合计", data: [182300, 164500, 0, 226400, 248900, 273100] }],
    }],
    ["统计 订单 · 每月按星期的订单数", {
      type: "heatmap", types: [...pivots, "heatmap"], title: "行数 · 按 日期(月)、日期(星期)", x: "日期(月)", y: "行数", group: "日期(星期)",
      categories: months, series: weekdays.map((name, k) => ({ name, data: months.map((_, i) => Math.max(0, Math.round(18 + 10 * Math.sin(i / 2 + k) + (k > 4 ? -9 : 4) + i))) })),
    }],
    ["统计 订单 · 各状态订单数", {
      type: "funnel", types: [...singles, "funnel", "treemap", "pareto", "waterfall"], title: "订单各状态数量", x: "状态",
      categories: ["下单", "付款", "发货", "签收", "完成"], series: [{ name: "行数", data: [1240, 1012, 876, 802, 745] }],
    }],
    ["统计 订单 · 各客户销售额 (二八分析)", {
      type: "pareto", types: [...singles, "treemap", "pareto"], title: "哪些客户贡献了八成销售额", x: "客户", categories: customers,
      series: [{ name: "金额 合计", data: customers.map((_, i) => Math.round(520000 / Math.pow(i + 1, 1.15))) }],
    }],
    ["画图 · 指标卡", {
      type: "kpi", types: ["kpi"], title: "本月销售额完成情况", x: "指标", categories: [], series: [],
      kpis: [{ name: "金额 合计", value: 420360.5, target: 500000, targetName: "本月目标", rate: 0.8407 }],
    }],
    ["统计 订单 · 今年概况", {
      type: "kpi", types: ["kpi"], title: "今年概况", x: "指标", categories: [], series: [],
      kpis: [{ name: "金额 合计", value: 3130700 }, { name: "行数", value: 1581 }, { name: "客户编号 不同值个数", value: 286 }],
    }],
    ["统计 订单 · 各客户单数和金额", {
      type: "scatter", types: ["combo", "scatter", "bar", "bar_horizontal", "line"], title: "金额 合计 与 行数", x: "客户", categories: customers,
      series: [
        { name: "行数", data: customers.map((_, i) => 4 + ((i * 7) % 23) + (i % 3)) },
        { name: "金额 合计", data: customers.map((_, i) => Math.round((4 + ((i * 7) % 23) + (i % 3)) * (2600 + ((i * 37) % 9) * 300))) },
      ],
    }],
    ["统计 订单 · 各类别销售额构成", {
      type: "treemap", types: [...singles, "funnel", "treemap", "pareto"], title: "各产品类别销售额构成", x: "产品类别", categories: ["家电", "数码", "服装", "家居", "食品", "母婴", "图书"],
      series: [{ name: "金额 合计", data: [1186500, 902300, 534800, 318900, 147600, 96200, 41800] }],
    }],
    ["统计 订单 · 各类别两年对比", {
      type: "radar", types: [...pivots, "radar", "heatmap"], title: "金额 合计 · 按 产品类别、日期(年)", x: "产品类别", y: "金额 合计", group: "日期(年)",
      categories: ["家电", "数码", "服装", "家居", "食品", "母婴"],
      series: [
        { name: "2025", data: [980000, 840000, 560000, 290000, 160000, 70000] },
        { name: "2026", data: [1186500, 902300, 534800, 318900, 147600, 96200] },
      ],
    }],
    ["查询 库存流水 · 本月库存变化", {
      type: "waterfall", types: ["bar", "bar_horizontal", "line", "area", "waterfall", "radar"], title: "本月库存变化", x: "项目",
      categories: ["期初库存", "采购入库", "销售出库", "退货入库", "盘亏"], series: [{ name: "数量", data: [1200, 800, -950, 60, -35] }],
    }],
  ];
}

const weekdays = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"];
const pivots = ["line", "bar", "bar_stacked", "bar_horizontal", "area"] as const;

/** 表格 → 图: 第一列横轴, 数字列数值; 两个数字列默认组合图 */
function autoChart(t: ResultTable): ChartData | null {
  const nums = t.columns
    .map((name, j) => ({ name, j }))
    .filter(({ j }) => j > 0 && t.rows.some((r) => typeof r[j] === "number") && t.rows.every((r) => r[j] === null || typeof r[j] === "number"));
  if (nums.length === 0 || t.rows.length < 2) return null;
  const categories = t.rows.map((r) => String(r[0]));
  const ys = nums.slice(0, nums.length === 2 ? 2 : 1);
  const series = ys.map(({ name, j }, i) => ({ name, data: t.rows.map((r) => r[j] as number | null), ...(ys.length === 2 && i === 1 ? { axis: 1 as const } : {}) }));
  const multi = series.length > 1;
  const nonNeg = series[0].data.every((v) => v === null || v >= 0);
  const types: ChartData["types"] = multi
    ? ["combo", "scatter", "bar", "bar_horizontal", "line"]
    : ["bar", "bar_horizontal", "line", "area", ...(nonNeg ? (["pie", "donut", "funnel", "treemap", "pareto"] as const) : []), "waterfall"];
  const type = multi ? "combo" : categories.length > 8 ? "bar_horizontal" : "bar";
  return { type, types, title: ys.map((y) => y.name).join("、") + " · 按 " + t.columns[0], x: t.columns[0], categories, series };
}

/** 工具卡片上的结果表格 (与图表同一份数) */
function chartTable(c: ChartData): ResultTable {
  const columns = [c.x, ...c.series.map((s) => s.name)];
  const rows = c.categories.slice(0, 50).map((cat, i) => [cat, ...c.series.map((s) => s.data[i])]);
  return { title: "订单", columns, rows, total: c.categories.length };
}
