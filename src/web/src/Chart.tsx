// 图表卡片: 统计结果画成的图, 可切换类型、查看数据。ECharts 按需引入 (只要柱状、折线、饼图)。
import { memo, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import * as echarts from "echarts/core";
import { BarChart, LineChart, PieChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";
import type { ChartData, ChartType, ResultTable } from "./protocol";
import { CHART_NAMES, chartHeight, fmtAxis, fmtFull, isPie, palette, shape, toTable } from "./chartData";
import { IconChart, IconSheet } from "./icons";

echarts.use([BarChart, LineChart, PieChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer]);

const INK = "#1d1d22";
const INK2 = "#666666";
const GRID = "#ebebef";
const AXIS = "#c2c2c2";
/** 饼图里合并出的"其他": 中性灰, 不占系列色 */
const OTHER = "#b9b9c3";

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
      {asTable ? renderTable(toTable(chart, type)) : <ChartCanvas chart={chart} type={type} theme={theme} />}
      {chart.note && <div className="note chart-note">{chart.note}</div>}
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
    const ro = new ResizeObserver(() => {
      // 收起到停放窗口时宽度为 0, 不跟着缩
      if (el.clientWidth > 0 && el.clientWidth !== w) {
        w = el.clientWidth;
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

function buildOption(c: ChartData, type: ChartType, colors: string[]): EChartsCoreOption {
  const d = shape(c, type);
  const base = {
    color: colors,
    animationDuration: 350,
    textStyle: { fontFamily: "inherit", color: INK2 },
  };

  if (isPie(type)) {
    const s = d.series[0];
    return {
      ...base,
      tooltip: {
        trigger: "item",
        confine: true,
        formatter: (p: { name: string; value: number; percent: number; marker: string }) =>
          `${p.marker}${esc(p.name)}<br/><b>${fmtFull(p.value)}</b>　${p.percent.toFixed(1)}%`,
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

  const horizontal = type === "bar_horizontal";
  const multi = d.series.length > 1;
  const combo = type === "combo";
  const many = d.categories.length > 40;
  const catAxis = {
    type: "category",
    data: d.categories,
    inverse: horizontal,
    axisTick: { show: false },
    axisLine: { lineStyle: { color: AXIS } },
    axisLabel: { color: INK2, hideOverlap: true, ...(horizontal ? { width: 96, overflow: "truncate" } : {}) },
  };
  const valAxis = (show = true) => ({
    type: "value",
    splitLine: { show, lineStyle: { color: GRID } },
    axisLabel: { color: INK2, formatter: fmtAxis },
  });

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
      itemStyle: stacked
        ? { borderColor: "#fff", borderWidth: 1 }
        : { borderRadius: horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0] },
      emphasis: { focus: multi ? "series" : "none" },
    };
  });

  return {
    ...base,
    legend: multi
      ? { type: "scroll", top: 0, left: 0, icon: "roundRect", itemWidth: 10, itemHeight: 10, itemGap: 14, textStyle: { color: INK2 } }
      : undefined,
    grid: { left: 4, right: combo ? 4 : 12, top: multi ? 34 : 12, bottom: 4, containLabel: true },
    tooltip: {
      trigger: "axis",
      confine: true,
      axisPointer: { type: type === "line" || type === "area" ? "line" : "shadow", lineStyle: { color: AXIS } },
      valueFormatter: fmtFull,
    },
    xAxis: horizontal ? valAxis() : catAxis,
    yAxis: horizontal ? catAxis : combo ? [valAxis(), valAxis(false)] : valAxis(),
    series,
  };
}

function esc(s: string) {
  return s.replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]!);
}
