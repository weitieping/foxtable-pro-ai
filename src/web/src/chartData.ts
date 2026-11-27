// 图表数据的整理: 图和"查看数据"的表格都从这里取数, 两边的数字始终一致。
// 不依赖 ECharts, 面板打开时不必加载它。
import type { ChartData, ChartSeries, ChartType, ResultTable } from "./protocol";

export const CHART_NAMES: Record<ChartType, string> = {
  bar: "柱状图",
  bar_horizontal: "横向柱状图",
  bar_stacked: "堆叠柱状图",
  line: "折线图",
  area: "面积图",
  pie: "饼图",
  donut: "环形图",
  combo: "组合图",
  heatmap: "热力图",
  scatter: "散点图",
  funnel: "漏斗图",
  treemap: "矩形树图",
  radar: "雷达图",
  waterfall: "瀑布图",
  pareto: "帕累托图",
  kpi: "指标卡",
};

/** 饼图最多显示几块, 其余合并为"其他" */
export const PIE_SLICES = 8;
/** 帕累托图: 累计占比到这里为"关键少数" */
export const PARETO_CUT = 0.8;

export interface Shaped {
  categories: string[];
  series: ChartSeries[];
  /** 饼图、帕累托图: 每项的占比 (0~1) */
  share?: number[];
  /** 帕累托图: 累计占比 (0~1) */
  cumulative?: number[];
  /** 瀑布图: 每一步从哪到哪 (最后一步是合计, 从 0 起) */
  steps?: { from: number; to: number }[];
  /** 饼图: 最后一块是合并出的"其他" */
  other?: boolean;
}

export const isPie = (t: ChartType) => t === "pie" || t === "donut";

type Item = { name: string; v: number };

function items(c: ChartData): Item[] {
  const s = c.series[0];
  return c.categories
    .map((name, i) => ({ name, v: s.data[i] }))
    .filter((x): x is Item => typeof x.v === "number");
}

/** 按图表类型整理数据。饼图按数值从大到小, 前 8 块之外合并为"其他" (等于其余各项之和);
 *  帕累托图从大到小并算累计占比; 瀑布图逐项累计, 最后加一根"合计" */
export function shape(c: ChartData, type: ChartType): Shaped {
  if (c.series.length === 0) return { categories: c.categories, series: c.series };
  const s = c.series[0];

  if (isPie(type)) {
    const sorted = items(c)
      .filter((x) => x.v > 0)
      .sort((a, b) => b.v - a.v);
    let shown = sorted;
    if (sorted.length > PIE_SLICES) {
      const rest = sorted.slice(PIE_SLICES).reduce((sum, x) => sum + x.v, 0);
      shown = [...sorted.slice(0, PIE_SLICES), { name: "其他", v: round(rest) }];
    }
    const total = shown.reduce((sum, x) => sum + x.v, 0);
    return {
      categories: shown.map((x) => x.name),
      series: [{ name: s.name, data: shown.map((x) => x.v) }],
      share: shown.map((x) => (total > 0 ? x.v / total : 0)),
      other: shown.length > PIE_SLICES,
    };
  }

  if (type === "pareto") {
    const sorted = items(c)
      .filter((x) => x.v >= 0)
      .sort((a, b) => b.v - a.v);
    const total = sorted.reduce((sum, x) => sum + x.v, 0);
    let run = 0;
    return {
      categories: sorted.map((x) => x.name),
      series: [{ name: s.name, data: sorted.map((x) => x.v) }],
      share: sorted.map((x) => (total > 0 ? x.v / total : 0)),
      cumulative: sorted.map((x) => {
        run += x.v;
        return total > 0 ? run / total : 0;
      }),
    };
  }

  if (type === "waterfall") {
    let run = 0;
    const steps = c.categories.map((_, i) => {
      const from = run;
      run = round(run + (s.data[i] ?? 0));
      return { from, to: run };
    });
    steps.push({ from: 0, to: run });
    return {
      categories: [...c.categories, "合计"],
      series: [{ name: s.name, data: [...s.data, run] }],
      steps,
    };
  }

  if (type === "funnel" || type === "treemap") {
    // 只画大于 0 的项 (漏斗、矩形树图里 0 和负数没有面积)
    if (c.series.length > 1) return { categories: c.categories, series: c.series };
    const kept = c.categories.map((name, i) => ({ name, v: s.data[i] })).filter((x) => typeof x.v === "number" && x.v > 0);
    return { categories: kept.map((x) => x.name), series: [{ name: s.name, data: kept.map((x) => x.v) }] };
  }

  return { categories: c.categories, series: c.series };
}

/** 消掉浮点加法的尾差 (如 0.1 + 0.2) */
function round(v: number) {
  return Math.round(v * 1e6) / 1e6;
}

const pct = (v: number) => (v * 100).toFixed(1) + "%";

/** "查看数据": 与图上画的完全相同的数 */
export function toTable(c: ChartData, type: ChartType): ResultTable {
  if (type === "kpi") {
    const ks = c.kpis ?? [];
    const withTarget = ks.some((k) => k.target !== undefined);
    const columns = withTarget ? ["指标", "实际", "目标", "完成率"] : ["指标", "数值"];
    const rows = ks.map((k) =>
      withTarget ? [k.name, k.value, k.target ?? null, k.rate === undefined ? null : pct(k.rate)] : [k.name, k.value],
    );
    return { title: c.title, columns, rows, total: rows.length };
  }
  const d = shape(c, type);
  if (type === "heatmap" || (d.series.length > 1 && c.group)) {
    // 按两列分组: 第一列是横轴, 其余每列是一个系列 (热力图的纵轴)
    const columns = [`${c.x} \\ ${c.group ?? ""}`.trim(), ...d.series.map((s) => s.name)];
    const rows = d.categories.map((cat, i) => [cat, ...d.series.map((s) => s.data[i] ?? null)]);
    return { title: c.title, columns, rows, total: rows.length };
  }
  const columns = [c.x, ...d.series.map((s) => s.name)];
  if (type === "waterfall") columns.push("累计");
  if (d.share) columns.push("占比");
  if (d.cumulative) columns.push("累计占比");
  const rows = d.categories.map((cat, i) => {
    const row: (string | number | null)[] = [cat, ...d.series.map((s) => s.data[i] ?? null)];
    if (d.steps) row.push(d.steps[i].to);
    if (d.share) row.push(pct(d.share[i]));
    if (d.cumulative) row.push(pct(d.cumulative[i]));
    return row;
  });
  return { title: c.title, columns, rows, total: rows.length };
}

// ---------- 尺寸与缩放 ----------

/** 横轴上超过这么多组时出现缩放条 (可拖动看局部) */
export function zoomOf(c: ChartData, type: ChartType): { axis: "x" | "y"; end: number } | null {
  const n = c.categories.length + (type === "waterfall" ? 1 : 0);
  const show = (limit: number) => Math.min(100, Math.max(5, (limit / n) * 100));
  switch (type) {
    case "line":
    case "area":
      return n > 60 ? { axis: "x", end: 100 } : null;
    case "bar":
    case "bar_stacked":
    case "combo":
    case "pareto":
    case "waterfall":
      return n > 30 ? { axis: "x", end: show(30) } : null;
    case "heatmap":
      return n > 40 ? { axis: "x", end: show(40) } : null;
    case "bar_horizontal":
      return n > 20 ? { axis: "y", end: show(20) } : null;
    default:
      return null;
  }
}

/** 图的高度: 横向柱状图按类别数加高, 热力图按行数加高 */
export function chartHeight(c: ChartData, type: ChartType): number {
  const n = c.categories.length;
  const zoom = zoomOf(c, type);
  switch (type) {
    case "bar_horizontal":
      return Math.max(200, Math.min(560, Math.min(n, 20) * 26 + 56));
    case "pie":
    case "donut":
      return 280;
    case "funnel":
      return Math.max(220, Math.min(420, n * 36 + 40));
    case "treemap":
    case "radar":
      return 300;
    case "scatter":
      return 290;
    case "heatmap":
      return Math.max(200, Math.min(620, c.series.length * 26 + 96)) + (zoom ? 30 : 0);
    default:
      return 260 + (zoom ? 30 : 0);
  }
}

// ---------- 配色 ----------

/** 经过色盲校验的 8 色 (按此顺序相邻两色可区分); 第一色换成开发者的主题色 */
const BASE = ["#257de0", "#f47230", "#00b894", "#f2a600", "#ee7fac", "#008a17", "#6a4ce2", "#ec4647"];

function rgb(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  let h = m[1];
  if (h.length === 3) h = h.replace(/./g, (x) => x + x);
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)) as [number, number, number];
}

function hue(hex: string): number | null {
  const c = rgb(hex);
  if (!c) return null;
  const [r, g, b] = c.map((x) => x / 255);
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  if (max - min < 0.08) return null; // 接近灰色, 没有色相
  const d = max - min;
  const x = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return (x * 60 + 360) % 360;
}

/** 主题色打头, 去掉与它色相最接近的一色, 其余按校验过的顺序 */
export function palette(theme: string): string[] {
  const th = hue(theme);
  if (th === null) return BASE;
  let near = 0;
  let best = 999;
  BASE.forEach((c, i) => {
    const h = hue(c)!;
    const dist = Math.min(Math.abs(h - th), 360 - Math.abs(h - th));
    if (dist < best) {
      best = dist;
      near = i;
    }
  });
  return [theme, ...BASE.filter((_, i) => i !== near)];
}

/** 往白色里调淡: t = 0 原色, 1 白色 (热力图的浅端、帕累托图的"其余各项") */
export function tint(hex: string, t: number): string {
  const c = rgb(hex);
  if (!c) return hex;
  return "#" + c.map((x) => Math.round(x + (255 - x) * t).toString(16).padStart(2, "0")).join("");
}

// ---------- 数字 ----------

export const fmtFull = (v: unknown) =>
  typeof v === "number" ? v.toLocaleString("zh-CN", { maximumFractionDigits: 2 }) : v == null || v === "" ? "-" : String(v);

/** 坐标轴上的简写: 1.2万、3.4亿 */
export function fmtAxis(v: number): string {
  const a = Math.abs(v);
  if (a >= 1e8) return trim(v / 1e8) + "亿";
  if (a >= 1e4) return trim(v / 1e4) + "万";
  return v.toLocaleString("zh-CN", { maximumFractionDigits: 2 });
}

function trim(v: number) {
  return v.toLocaleString("zh-CN", { maximumFractionDigits: 1 });
}

/** 雷达图等的刻度上限: 取整到前两位数字, 如 358900 → 360000、1186500 → 1200000 */
export function niceMax(v: number): number {
  if (!(v > 0)) return 1;
  const m = Math.pow(10, Math.floor(Math.log10(v)) - 1);
  return Math.ceil(v / m) * m;
}
