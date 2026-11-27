// 图表卡片: 结果画成的图, 可切换类型、查看数据。ECharts 按需引入, 只要用到的图和组件。
import { memo, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import * as echarts from "echarts/core";
import { BarChart, FunnelChart, HeatmapChart, LineChart, PieChart, RadarChart, ScatterChart, TreemapChart } from "echarts/charts";
import {
  DataZoomInsideComponent,
  DataZoomSliderComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  RadarComponent,
  TooltipComponent,
  VisualMapContinuousComponent,
} from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";
import type { ChartData, ChartType, Kpi, ResultTable } from "./protocol";
import { CHART_NAMES, PARETO_CUT, chartHeight, fmtAxis, fmtFull, niceMax, palette, shape, tint, toTable, zoomOf } from "./chartData";
import { IconChart, IconSheet } from "./icons";

echarts.use([
  BarChart,
  LineChart,
  PieChart,
  HeatmapChart,
  ScatterChart,
  FunnelChart,
  TreemapChart,
  RadarChart,
  GridComponent,
  LegendComponent,
  TooltipComponent,
  VisualMapContinuousComponent,
  DataZoomInsideComponent,
  DataZoomSliderComponent,
  MarkLineComponent,
  RadarComponent,
  CanvasRenderer,
]);

const INK = "#1d1d22";
const INK2 = "#666666";
const GRID = "#ebebef";
const AXIS = "#c2c2c2";
/** 饼图里合并出的"其他": 中性灰, 不占系列色 */
const OTHER = "#b9b9c3";
/** 瀑布图: 减少、合计 */
const DOWN = "#ec4647";
const TOTAL = "#5b5b66";

export function ChartCard({ chart, theme, renderTable }: { chart: ChartData; theme: string; renderTable: (t: ResultTable) => ReactNode }) {
  const [type, setType] = useState<ChartType>(chart.type);
  const [asTable, setAsTable] = useState(false);
  const [menu, setMenu] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const types = chart.types.includes(type) ? chart.types : [type, ...chart.types];

  // 点菜单外面就收起
  useEffect(() => {
    if (!menu) return;
    const off = (e: MouseEvent) => {
      if (!menuRef.current?.contains(e.target as Node)) setMenu(false);
    };
    window.addEventListener("mousedown", off);
    return () => window.removeEventListener("mousedown", off);
  }, [menu]);

  return (
    <div className="chart-card">
      <div className="chart-head">
        <span className="chart-title" title={chart.title}>
          {chart.title}
        </span>
        {types.length > 1 && (
          <div className="chart-menu" ref={menuRef}>
            <button className="chart-btn" title="切换图表类型" onClick={() => setMenu(!menu)}>
              {CHART_NAMES[type]}
              <span className={"chev" + (menu ? " down" : "")}>›</span>
            </button>
            {menu && (
              <div className="chart-pop" role="menu">
                {types.map((t) => (
                  <button
                    key={t}
                    role="menuitemradio"
                    aria-checked={t === type}
                    className={t === type ? "on" : undefined}
                    onClick={() => {
                      setType(t);
                      setAsTable(false);
                      setMenu(false);
                    }}
                  >
                    {CHART_NAMES[t]}
                  </button>
                ))}
              </div>
            )}
          </div>
        )}
        <button className="chart-btn" title={asTable ? "查看图表" : "查看数据"} onClick={() => setAsTable(!asTable)}>
          {asTable ? <IconChart /> : <IconSheet />}
          {asTable ? "查看图表" : "查看数据"}
        </button>
      </div>
      {asTable ? (
        renderTable(toTable(chart, type))
      ) : type === "kpi" ? (
        <KpiView kpis={chart.kpis ?? []} />
      ) : (
        <ChartCanvas chart={chart} type={type} theme={theme} />
      )}
      {chart.note && <div className="note chart-note">{chart.note}</div>}
    </div>
  );
}

/** 指标卡: 不用 ECharts。有目标时显示目标、完成率和进度条 */
function KpiView({ kpis }: { kpis: Kpi[] }) {
  return (
    <div className={"kpis" + (kpis.length === 1 ? " one" : "")}>
      {kpis.map((k, i) => {
        const hasTarget = k.target !== undefined;
        const rate = k.rate;
        return (
          <div key={i} className="kpi">
            <div className="kpi-name" title={k.name}>
              {k.name}
            </div>
            <div className="kpi-value">{fmtFull(k.value)}</div>
            {hasTarget && (
              <>
                <div className="kpi-meta">
                  <span title={k.targetName}>目标 {fmtFull(k.target)}</span>
                  {rate !== undefined && <b className={rate >= 1 ? "reached" : undefined}>完成 {(rate * 100).toFixed(1)}%</b>}
                </div>
                {rate !== undefined && (
                  <div className="kpi-bar" role="progressbar" aria-valuenow={Math.round(rate * 100)} aria-valuemin={0} aria-valuemax={100}>
                    <i style={{ width: `${Math.max(0, Math.min(1, rate)) * 100}%` }} />
                  </div>
                )}
              </>
            )}
          </div>
        );
      })}
    </div>
  );
}

/** 滚动到看得见时才绘制; 随容器宽度自动调整; 卸载 (新对话、切到表格) 时释放 */
const ChartCanvas = memo(function ChartCanvas({ chart, type, theme }: { chart: ChartData; type: ChartType; theme: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const inst = useRef<echarts.ECharts | null>(null);
  const [seen, setSeen] = useState(false);
  const colors = useMemo(() => palette(theme), [theme]);

  useEffect(() => {
    const el = ref.current!;
    const io = new IntersectionObserver(
      (es) => {
        if (es.some((e) => e.isIntersecting)) {
          setSeen(true);
          io.disconnect();
        }
      },
      { rootMargin: "200px 0px" },
    );
    io.observe(el);
    return () => io.disconnect();
  }, []);

  useEffect(() => {
    if (!seen) return;
    const el = ref.current!;
    const c = echarts.init(el, undefined, { renderer: "canvas" });
    inst.current = c;
    let w = el.clientWidth;
    let h = el.clientHeight;
    const ro = new ResizeObserver(() => {
      // 收起到停放窗口时宽度为 0, 不跟着缩
      if (el.clientWidth > 0 && (el.clientWidth !== w || el.clientHeight !== h)) {
        w = el.clientWidth;
        h = el.clientHeight;
        c.resize();
      }
    });
    ro.observe(el);
    return () => {
      ro.disconnect();
      c.dispose();
      inst.current = null;
    };
  }, [seen]);

  useEffect(() => {
    inst.current?.setOption(buildOption(chart, type, colors), true);
  }, [seen, chart, type, colors]);

  return <div ref={ref} className="chart-canvas" style={{ height: chartHeight(chart, type) }} />;
});

// ---------- ECharts 配置 ----------

type Params = { name: string; value: unknown; marker: string; percent?: number; seriesName?: string; dataIndex: number; data?: unknown };

function buildOption(c: ChartData, type: ChartType, colors: string[]): EChartsCoreOption {
  const base = {
    color: colors,
    animationDuration: 350,
    textStyle: { fontFamily: "inherit", color: INK2 },
  };
  switch (type) {
    case "pie":
    case "donut":
      return { ...base, ...pieOption(c, type) };
    case "funnel":
      return { ...base, ...funnelOption(c) };
    case "treemap":
      return { ...base, ...treemapOption(c, colors) };
    case "radar":
      return { ...base, ...radarOption(c) };
    case "scatter":
      return { ...base, ...scatterOption(c) };
    case "heatmap":
      return { ...base, ...heatmapOption(c, colors[0]) };
    default:
      return { ...base, ...axisOption(c, type, colors) };
  }
}

const legendStyle = { type: "scroll", top: 0, left: 0, icon: "roundRect", itemWidth: 10, itemHeight: 10, itemGap: 14, textStyle: { color: INK2 } };

const catAxis = (data: string[], horizontal = false) => ({
  type: "category",
  data,
  inverse: horizontal,
  axisTick: { show: false },
  axisLine: { lineStyle: { color: AXIS } },
  axisLabel: { color: INK2, hideOverlap: true, ...(horizontal ? { width: 96, overflow: "truncate" } : {}) },
});

const valAxis = (show = true, extra: object = {}) => ({
  type: "value",
  splitLine: { show, lineStyle: { color: GRID } },
  axisLabel: { color: INK2, formatter: fmtAxis },
  ...extra,
});

/** 缩放条: 鼠标滚轮/拖动缩放, 底部 (横向柱状图在右侧) 有可拖动的滑块 */
function dataZoom(c: ChartData, type: ChartType) {
  const z = zoomOf(c, type);
  if (!z) return { zoom: false, option: {} };
  const axis = z.axis === "x" ? { xAxisIndex: 0 } : { yAxisIndex: 0 };
  const slider =
    z.axis === "x"
      ? { type: "slider", ...axis, start: 0, end: z.end, height: 16, bottom: 6, left: 40, right: 40, brushSelect: false, showDetail: false }
      : { type: "slider", ...axis, start: 0, end: z.end, width: 14, right: 2, top: 12, bottom: 12, brushSelect: false, showDetail: false };
  return {
    zoom: true,
    option: {
      dataZoom: [{ type: "inside", ...axis, start: 0, end: z.end, zoomOnMouseWheel: "shift", moveOnMouseWheel: z.axis === "y" }, slider],
    },
  };
}

function pieOption(c: ChartData, type: ChartType) {
  const d = shape(c, type);
  const s = d.series[0];
  return {
    tooltip: {
      trigger: "item",
      confine: true,
      formatter: (p: Params) => `${p.marker}${esc(p.name)}<br/><b>${fmtFull(p.value)}</b>　${(p.percent ?? 0).toFixed(1)}%`,
    },
    series: [
      {
        type: "pie",
        name: s?.name,
        radius: type === "donut" ? ["42%", "66%"] : ["0%", "66%"],
        center: ["50%", "52%"],
        minAngle: 2,
        avoidLabelOverlap: true,
        itemStyle: { borderColor: "#fff", borderWidth: 2, borderRadius: type === "donut" ? 4 : 2 },
        percentPrecision: 1,
        label: { color: INK, formatter: "{b}  {d}%", overflow: "truncate", width: 110 },
        labelLine: { length: 8, length2: 8 },
        data: d.categories.map((name, i) => ({
          name,
          value: s?.data[i],
          ...(d.other && i === d.categories.length - 1 ? { itemStyle: { color: OTHER } } : {}),
        })),
      },
    ],
  };
}

/** 漏斗图: 按给定顺序 (如状态流程) 从上到下, 不按大小重排; 提示里给出占第一层的比例 */
function funnelOption(c: ChartData) {
  const d = shape(c, "funnel");
  const s = d.series[0];
  const first = (s?.data[0] as number) || 0;
  const max = Math.max(0, ...s.data.map((v) => v ?? 0));
  return {
    tooltip: {
      trigger: "item",
      confine: true,
      formatter: (p: Params) => {
        const v = p.value as number;
        const prev = p.dataIndex > 0 ? (s.data[p.dataIndex - 1] as number) : 0;
        const rows = [`${p.marker}${esc(p.name)}`, `<b>${fmtFull(v)}</b>`];
        if (first > 0 && p.dataIndex > 0) rows.push(`占第一层 ${((v / first) * 100).toFixed(1)}%`);
        if (prev > 0) rows.push(`较上一层 ${((v / prev) * 100).toFixed(1)}%`);
        return rows.join("<br/>");
      },
    },
    series: [
      {
        type: "funnel",
        name: s?.name,
        sort: "none",
        min: 0,
        max,
        minSize: "8%",
        left: "6%",
        right: "6%",
        top: 8,
        bottom: 8,
        gap: 2,
        itemStyle: { borderColor: "#fff", borderWidth: 1 },
        label: { show: true, position: "inside", color: "#fff", formatter: (p: Params) => `${p.name}  ${fmtAxis(p.value as number)}`, overflow: "truncate" },
        emphasis: { label: { fontWeight: "bold" } },
        data: d.categories.map((name, i) => ({ name, value: s.data[i] })),
      },
    ],
  };
}

/** 矩形树图: 一个系列时每项一块; 按两列分组时外层是横轴的每一组, 里面是各系列 */
function treemapOption(c: ChartData, colors: string[]) {
  const d = shape(c, "treemap");
  const nested = d.series.length > 1;
  let total = 0;
  const data = nested
    ? d.categories
        .map((name, i) => {
          const children = d.series
            .map((s) => ({ name: s.name, value: s.data[i] ?? 0 }))
            .filter((x) => x.value > 0);
          return { name, value: children.reduce((sum, x) => sum + x.value, 0), children };
        })
        .filter((x) => x.value > 0)
    : d.categories.map((name, i) => ({ name, value: d.series[0].data[i] as number }));
  data.forEach((x) => (total += x.value));
  return {
    tooltip: {
      confine: true,
      formatter: (p: { name: string; value: number; treePathInfo: { name: string }[]; marker: string }) => {
        const path = p.treePathInfo
          .slice(1)
          .map((x) => esc(x.name))
          .join(" · ");
        return `${p.marker}${path}<br/><b>${fmtFull(p.value)}</b>${total > 0 ? `　${((p.value / total) * 100).toFixed(1)}%` : ""}`;
      },
    },
    series: [
      {
        type: "treemap",
        name: nested ? c.y : d.series[0]?.name,
        roam: false,
        nodeClick: false,
        breadcrumb: { show: false },
        left: 0,
        right: 0,
        top: 0,
        bottom: 0,
        label: { show: true, formatter: (p: Params) => `${p.name}\n${fmtAxis(p.value as number)}`, overflow: "truncate", fontSize: 12 },
        upperLabel: nested ? { show: true, height: 20, color: "#fff", fontWeight: "bold" } : undefined,
        itemStyle: { borderColor: "#fff", borderWidth: 2, gapWidth: 2 },
        levels: nested
          ? [
              { itemStyle: { borderColor: "#fff", borderWidth: 3, gapWidth: 3 } },
              { colorSaturation: [0.35, 0.6], itemStyle: { borderColorSaturation: 0.7, gapWidth: 1, borderWidth: 1 } },
            ]
          : undefined,
        data: data.map((x, i) => ({ ...x, itemStyle: { color: colors[i % colors.length] } })),
      },
    ],
  };
}

/** 雷达图: 横轴的每组是一根轴, 每个系列一个多边形; 各轴用同一刻度, 形状才能比 */
function radarOption(c: ChartData) {
  const all = c.series.flatMap((s) => s.data.filter((v): v is number => typeof v === "number"));
  const hi = niceMax(Math.max(0, ...all));
  const lo = Math.min(0, ...all);
  const min = lo < 0 ? -niceMax(-lo) : 0;
  const multi = c.series.length > 1;
  return {
    legend: multi ? legendStyle : undefined,
    tooltip: { trigger: "item", confine: true, valueFormatter: fmtFull },
    radar: {
      center: ["50%", multi ? "56%" : "52%"],
      radius: "66%",
      indicator: c.categories.map((name) => ({ name, max: hi, min })),
      axisName: { color: INK2, overflow: "truncate", width: 80 },
      splitLine: { lineStyle: { color: GRID } },
      splitArea: { show: false },
      axisLine: { lineStyle: { color: GRID } },
    },
    series: [
      {
        type: "radar",
        symbolSize: 4,
        lineStyle: { width: 2 },
        areaStyle: { opacity: multi ? 0.08 : 0.18 },
        emphasis: { focus: "self", areaStyle: { opacity: 0.25 } },
        data: c.series.map((s) => ({ name: s.name, value: s.data.map((v) => v ?? "-") })),
      },
    ],
  };
}

/** 散点图: 每组一个点, 两个系列分别是横轴、纵轴的数值 */
function scatterOption(c: ChartData) {
  const [sx, sy] = c.series;
  const pts = c.categories
    .map((name, i) => [sx?.data[i], sy?.data[i], name] as const)
    .filter((p) => typeof p[0] === "number" && typeof p[1] === "number");
  return {
    grid: { left: 8, right: 16, top: 28, bottom: 28, containLabel: true },
    tooltip: {
      trigger: "item",
      confine: true,
      formatter: (p: { data: [number, number, string]; marker: string }) =>
        `${p.marker}${esc(p.data[2])}<br/>${esc(sx.name)}　<b>${fmtFull(p.data[0])}</b><br/>${esc(sy.name)}　<b>${fmtFull(p.data[1])}</b>`,
    },
    xAxis: valAxis(true, { name: sx?.name, nameLocation: "middle", nameGap: 24, nameTextStyle: { color: INK2 }, scale: true }),
    yAxis: valAxis(true, { name: sy?.name, nameTextStyle: { color: INK2, align: "left" }, scale: true }),
    series: [
      {
        type: "scatter",
        name: sy?.name,
        symbolSize: pts.length > 200 ? 6 : 10,
        itemStyle: { opacity: 0.75 },
        emphasis: { focus: "self", label: { show: true, formatter: (p: { data: [number, number, string] }) => p.data[2], position: "top", color: INK } },
        data: pts,
      },
    ],
  };
}

/** 热力图: 横轴 × 纵轴 (系列), 颜色深浅表示大小; 色标放在右上角 */
function heatmapOption(c: ChartData, theme: string) {
  const data: [number, number, number][] = [];
  c.series.forEach((s, j) =>
    s.data.forEach((v, i) => {
      if (typeof v === "number") data.push([i, j, v]);
    }),
  );
  const vals = data.map((d) => d[2]);
  const lo = Math.min(...vals);
  const hi = Math.max(...vals);
  const { zoom, option } = dataZoom(c, "heatmap");
  return {
    grid: { left: 4, right: 8, top: 34, bottom: zoom ? 34 : 4, containLabel: true },
    tooltip: {
      confine: true,
      formatter: (p: { data: [number, number, number]; marker: string }) =>
        `${esc(c.series[p.data[1]].name)} · ${esc(c.categories[p.data[0]])}<br/><b>${fmtFull(p.data[2])}</b>`,
    },
    xAxis: { ...catAxis(c.categories), splitArea: { show: false } },
    yAxis: { ...catAxis(c.series.map((s) => s.name), true), splitArea: { show: false } },
    visualMap: {
      type: "continuous",
      min: lo === hi ? lo - 1 : lo,
      max: hi,
      calculable: false,
      orient: "horizontal",
      right: 0,
      top: 0,
      itemWidth: 10,
      itemHeight: 100,
      text: [fmtAxis(hi), fmtAxis(lo)],
      textStyle: { color: INK2, fontSize: 11 },
      inRange: { color: [tint(theme, 0.9), theme] },
    },
    ...option,
    series: [
      {
        type: "heatmap",
        name: c.y,
        data,
        label: { show: data.length <= 120, formatter: (p: { data: [number, number, number] }) => fmtAxis(p.data[2]), fontSize: 11, color: INK },
        itemStyle: { borderColor: "#fff", borderWidth: 1 },
        emphasis: { itemStyle: { borderColor: INK, borderWidth: 1 } },
      },
    ],
  };
}

/** 有横轴、纵轴的图: 柱状、折线、面积、组合、帕累托、瀑布 */
function axisOption(c: ChartData, type: ChartType, colors: string[]) {
  const d = shape(c, type);
  const horizontal = type === "bar_horizontal";
  const multi = d.series.length > 1;
  const combo = type === "combo";
  const many = d.categories.length > 40;
  const { zoom, option: zoomOpt } = dataZoom(c, type);
  const grid = {
    left: 4,
    right: horizontal && zoom ? 24 : combo || type === "pareto" ? 4 : 12,
    top: multi || type === "pareto" || type === "waterfall" ? 34 : 12,
    bottom: zoom && !horizontal ? 32 : 4,
    containLabel: true,
  };
  const tooltip = {
    trigger: "axis",
    confine: true,
    axisPointer: { type: type === "line" || type === "area" ? "line" : "shadow", lineStyle: { color: AXIS } },
    valueFormatter: fmtFull,
  };

  if (type === "pareto") {
    const s = d.series[0];
    const cum = d.cumulative ?? [];
    // 累计到 80% 的那一项为止是"关键少数", 其余调淡
    const cut = cum.findIndex((v) => v >= PARETO_CUT - 1e-9);
    return {
      legend: { ...legendStyle, data: [s.name, "累计占比"] },
      grid,
      tooltip: {
        ...tooltip,
        formatter: (ps: Params[]) => {
          const i = ps[0]?.dataIndex ?? 0;
          return `${esc(d.categories[i])}<br/>${esc(s.name)}　<b>${fmtFull(s.data[i])}</b><br/>占比　${((d.share?.[i] ?? 0) * 100).toFixed(1)}%<br/>累计占比　<b>${(cum[i] * 100).toFixed(1)}%</b>`;
        },
      },
      xAxis: catAxis(d.categories),
      yAxis: [valAxis(), { type: "value", min: 0, max: 100, splitLine: { show: false }, axisLabel: { color: INK2, formatter: "{value}%" } }],
      ...zoomOpt,
      series: [
        {
          type: "bar",
          name: s.name,
          barMaxWidth: 28,
          data: s.data.map((v, i) => ({ value: v, itemStyle: { color: cut < 0 || i <= cut ? colors[0] : tint(colors[0], 0.6) } })),
          itemStyle: { borderRadius: [4, 4, 0, 0] },
        },
        {
          type: "line",
          name: "累计占比",
          yAxisIndex: 1,
          data: cum.map((v) => Math.round(v * 1000) / 10),
          symbol: "circle",
          symbolSize: 5,
          showSymbol: !many,
          lineStyle: { width: 2 },
          itemStyle: { color: colors[1] },
          tooltip: { valueFormatter: (v: number) => v + "%" },
          markLine: {
            silent: true,
            symbol: "none",
            lineStyle: { type: "dashed", color: INK2 },
            label: { formatter: "80%", color: INK2, position: "insideEndTop" },
            data: [{ yAxis: PARETO_CUT * 100 }],
          },
        },
      ],
    };
  }

  if (type === "waterfall") {
    const s = d.series[0];
    const steps = d.steps ?? [];
    const last = steps.length - 1;
    const lows = steps.map((st) => Math.min(st.from, st.to));
    const spans = steps.map((st) => Math.abs(st.to - st.from));
    return {
      legend: { ...legendStyle, data: ["增加", "减少", "合计"] },
      grid,
      tooltip: {
        ...tooltip,
        formatter: (ps: Params[]) => {
          const i = ps[0]?.dataIndex ?? 0;
          if (i === last) return `合计<br/><b>${fmtFull(steps[i].to)}</b>`;
          const v = s.data[i] ?? 0;
          return `${esc(d.categories[i])}<br/>${esc(s.name)}　<b>${v > 0 ? "+" : ""}${fmtFull(v)}</b><br/>累计　${fmtFull(steps[i].to)}`;
        },
      },
      xAxis: catAxis(d.categories),
      yAxis: valAxis(),
      ...zoomOpt,
      series: [
        // 透明的垫底柱, 让每一步从上一步的终点开始
        {
          type: "bar",
          name: "_base",
          stack: "wf",
          stackStrategy: "all",
          silent: true,
          tooltip: { show: false },
          itemStyle: { color: "transparent" },
          emphasis: { disabled: true },
          data: lows,
        },
        ...(["增加", "减少", "合计"] as const).map((name) => ({
          type: "bar",
          name,
          stack: "wf",
          stackStrategy: "all",
          barMaxWidth: 28,
          itemStyle: { color: name === "增加" ? colors[0] : name === "减少" ? DOWN : TOTAL, borderRadius: 2 },
          data: spans.map((v, i) => {
            const kind = i === last ? "合计" : (s.data[i] ?? 0) >= 0 ? "增加" : "减少";
            // 不属于这一类的填 0 (填 "-" 会让叠在它上面的柱子丢掉底座)
            return kind === name ? v : 0;
          }),
        })),
      ],
    };
  }

  const series = d.series.map((s) => {
    const right = combo && s.axis === 1;
    const kind = type === "line" || type === "area" || right ? "line" : "bar";
    const name = right ? `${s.name}（右轴）` : s.name;
    if (kind === "line") {
      return {
        type: "line",
        name,
        data: s.data,
        yAxisIndex: right ? 1 : 0,
        lineStyle: { width: 2 },
        symbol: "circle",
        symbolSize: 6,
        showSymbol: !many,
        emphasis: { focus: multi ? "series" : "none" },
        ...(type === "area" ? { areaStyle: { opacity: multi ? 0.12 : 0.2 } } : {}),
      };
    }
    const stacked = type === "bar_stacked";
    return {
      type: "bar",
      name,
      data: s.data,
      stack: stacked ? "total" : undefined,
      barMaxWidth: horizontal ? 20 : 28,
      barGap: "12%",
      itemStyle: stacked ? { borderColor: "#fff", borderWidth: 1 } : { borderRadius: horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0] },
      emphasis: { focus: multi ? "series" : "none" },
    };
  });

  return {
    legend: multi ? legendStyle : undefined,
    grid,
    tooltip,
    xAxis: horizontal ? valAxis() : catAxis(d.categories),
    yAxis: horizontal ? catAxis(d.categories, true) : combo ? [valAxis(), valAxis(false)] : valAxis(),
    ...zoomOpt,
    series,
  };
}

function esc(s: string) {
  return String(s).replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]!);
}
