import { useEffect, useRef } from "react";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import type { MetricKind } from "../api/types";
import { formatMetricValue } from "../lib/format";

function cssVar(name: string, fallback: string): string {
  if (typeof window === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

// One y-line of the chart: a label, a color, and its values aligned to the shared x.
export interface ChartLine {
  label: string;
  color: string;
  values: number[];
}

// A real time-axis metric chart. The x-axis is wall-clock time (unix seconds);
// callers pass timestamped lines. Supports uPlot's built-in cursor crosshair +
// live legend readout and drag-to-zoom on x. A user zoom is reported via
// onUserZoom so the page can pause live updates while inspecting.
export function MetricChart({
  timestampsMs,
  lines,
  unit,
  kind,
  height = 300,
  onUserZoom,
}: {
  timestampsMs: number[];
  lines: ChartLine[];
  unit: string | null;
  kind: MetricKind;
  height?: number;
  onUserZoom?: () => void;
}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const plotRef = useRef<uPlot | null>(null);
  const applyingRef = useRef(false);
  const onUserZoomRef = useRef(onUserZoom);
  onUserZoomRef.current = onUserZoom;

  // Recreate the plot when its structural shape changes (series count, unit,
  // kind, height). Live data updates go through the separate effect below.
  const shapeKey = `${kind}|${unit ?? ""}|${height}|${lines.map((l) => l.label).join(",")}`;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) {
      return;
    }

    const grid = cssVar("--border", "rgba(255,255,255,0.07)");
    const text = cssVar("--text-muted", "#6f6f80");
    const fmt = (v: number | null | undefined) =>
      v === null || v === undefined || Number.isNaN(v) ? "—" : formatMetricValue(v, unit);

    const series: uPlot.Series[] = [
      {
        // x: show full timestamp in the legend (date + time with seconds).
        value: (_self, raw) =>
          raw === null
            ? "—"
            : new Date(raw * 1000).toLocaleTimeString(undefined, {
                hour: "2-digit",
                minute: "2-digit",
                second: "2-digit",
              }),
      },
      ...lines.map<uPlot.Series>((line) => ({
        label: line.label,
        stroke: line.color,
        width: 2,
        // Fill only single-line charts; overlaid percentile lines stay unfilled.
        fill: lines.length === 1 ? cssVar("--accent-soft", "rgba(168,85,247,0.16)") : undefined,
        points: { show: false },
        value: (_self, raw) => fmt(raw),
      })),
    ];

    const opts: uPlot.Options = {
      width: container.clientWidth || 600,
      height,
      padding: [14, 14, 0, 0],
      cursor: {
        show: true,
        // Drag on x to zoom; this is what triggers the auto-pause.
        drag: { x: true, y: false },
      },
      legend: { show: true, live: true },
      scales: {
        x: { time: true },
        y: { auto: true },
      },
      axes: [
        {
          stroke: text,
          grid: { stroke: grid, width: 1 },
          ticks: { stroke: grid, width: 1 },
          font: "11px system-ui, sans-serif",
        },
        {
          stroke: text,
          grid: { stroke: grid, width: 1 },
          ticks: { stroke: grid, width: 1 },
          font: "11px system-ui, sans-serif",
          size: 60,
          values: (_self, splits) => splits.map((v) => formatMetricValue(v, unit)),
        },
      ],
      series,
      hooks: {
        setScale: [
          (self, key) => {
            // A user drag-zoom narrows x. Ignore scale changes we cause ourselves
            // when feeding live data; only react to genuine user interaction.
            if (key !== "x" || applyingRef.current) {
              return;
            }
            const xData = self.data[0];
            if (!xData || xData.length === 0) {
              return;
            }
            const dataMin = xData[0]!;
            const dataMax = xData[xData.length - 1]!;
            const scale = self.scales.x;
            const zoomed =
              scale?.min !== undefined &&
              scale?.max !== undefined &&
              (scale.min > dataMin + 0.5 || scale.max < dataMax - 0.5);
            if (zoomed) {
              onUserZoomRef.current?.();
            }
          },
        ],
      },
    };

    const plot = new uPlot(opts, [[], ...lines.map(() => [])] as uPlot.AlignedData, container);
    plotRef.current = plot;

    const onResize = (): void => {
      plot.setSize({ width: container.clientWidth || 600, height });
    };
    const observer = new ResizeObserver(onResize);
    observer.observe(container);

    return () => {
      observer.disconnect();
      plot.destroy();
      plotRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shapeKey]);

  // Feed new data on every change. uPlot needs unix *seconds* for time scales.
  useEffect(() => {
    const plot = plotRef.current;
    if (!plot) {
      return;
    }
    const xs = timestampsMs.map((t) => t / 1000);
    const data: uPlot.AlignedData = [xs, ...lines.map((l) => l.values)];
    applyingRef.current = true;
    plot.setData(data);
    applyingRef.current = false;
  }, [timestampsMs, lines]);

  return <div ref={containerRef} className="metric-chart" style={{ height }} />;
}
