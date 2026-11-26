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
};

/** 饼图最多显示几块, 其余合并为"其他" */
export const PIE_SLICES = 8;

export interface Shaped {
  categories: string[];
  series: ChartSeries[];
  /** 饼图: 每块的占比 (0~1) */
  share?: number[];
  /** 饼图: 最后一块是合并出的"其他" */
  other?: boolean;
}

export const isPie = (t: ChartType) => t === "pie" || t === "donut";

/** 按图表类型整理数据: 饼图按数值从大到小, 前 8 块之外合并为"其他" (等于其余各项之和) */
export function shape(c: ChartData, type: ChartType): Shaped {
  if (!isPie(type) || c.series.length === 0) return { categories: c.categories, series: c.series };
  const s = c.series[0];
  const items = c.categories
    .map((name, i) => ({ name, v: s.data[i] }))
    .filter((x): x is { name: string; v: number } => typeof x.v === "number" && x.v > 0)
    .sort((a, b) => b.v - a.v);
  let shown = items;
  if (items.length > PIE_SLICES) {
    const rest = items.slice(PIE_SLICES).reduce((sum, x) => sum + x.v, 0);
    shown = [...items.slice(0, PIE_SLICES), { name: "其他", v: round(rest) }];
  }
  const total = shown.reduce((sum, x) => sum + x.v, 0);
  return {
    categories: shown.map((x) => x.name),
    series: [{ name: s.name, data: shown.map((x) => x.v) }],
    share: shown.map((x) => (total > 0 ? x.v / total : 0)),
    other: shown.length > PIE_SLICES,
  };
}

/** 消掉浮点加法的尾差 (如 0.1 + 0.2) */
function round(v: number) {
  return Math.round(v * 1e6) / 1e6;
}

/** "查看数据": 与图上画的完全相同的数 */
export function toTable(c: ChartData, type: ChartType): ResultTable {
  const d = shape(c, type);
  const columns = [c.x, ...d.series.map((s) => s.name)];
  if (d.share) columns.push("占比");
  const rows = d.categories.map((cat, i) => {
    const row: (string | number | null)[] = [cat, ...d.series.map((s) => s.data[i] ?? null)];
    if (d.share) row.push((d.share[i] * 100).toFixed(1) + "%");
    return row;
  });
  return { title: c.title, columns, rows, total: rows.length };
}

/** 图的高度: 横向柱状图按类别数加高 */
export function chartHeight(c: ChartData, type: ChartType): number {
  if (type === "bar_horizontal") return Math.max(200, Math.min(560, c.categories.length * 26 + 56));
  if (isPie(type)) return 280;
  return 260;
}

// ---------- 配色 ----------

/** 经过色盲校验的 8 色 (按此顺序相邻两色可区分); 第一色换成开发者的主题色 */
const BASE = ["#257de0", "#f47230", "#00b894", "#f2a600", "#ee7fac", "#008a17", "#6a4ce2", "#ec4647"];

function hue(hex: string): number | null {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  let h = m[1];
  if (h.length === 3) h = h.replace(/./g, (x) => x + x);
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16) / 255);
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
