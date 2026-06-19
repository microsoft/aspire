import { useEffect, useRef } from "react";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";

function cssVar(name: string, fallback: string): string {
  if (typeof window === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

// Lightweight area/line chart over a numeric series. The x-axis is a synthetic
// sample index (0..n-1) because the telemetry summary only exposes lastValue;
// callers maintain a client-side ring buffer and pass the latest values here.
export function Sparkline({
  values,
  unit,
  height = 260,
}: {
  values: number[];
  unit: string | null;
  height?: number;
}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const plotRef = useRef<uPlot | null>(null);

  // (Re)create the plot when the container mounts or height changes.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) {
      return;
    }

    const accent = cssVar("--accent", "#a855f7");
    const accentSoft = cssVar("--accent-soft", "rgba(168,85,247,0.16)");
    const grid = cssVar("--border", "rgba(255,255,255,0.07)");
    const text = cssVar("--text-muted", "#6f6f80");

    const opts: uPlot.Options = {
      width: container.clientWidth || 600,
      height,
      padding: [12, 12, 0, 0],
      cursor: { show: true, y: false },
      legend: { show: false },
      scales: { x: { time: false } },
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
          size: 52,
        },
      ],
      series: [
        {},
        {
          label: unit ?? "value",
          stroke: accent,
          width: 2,
          fill: accentSoft,
          points: { show: false },
        },
      ],
    };

    const plot = new uPlot(opts, [[], []], container);
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
  }, [height, unit]);

  // Feed new data on every value change.
  useEffect(() => {
    const plot = plotRef.current;
    if (!plot) {
      return;
    }
    const xs = values.map((_, i) => i);
    plot.setData([xs, values]);
  }, [values]);

  return <div ref={containerRef} className="sparkline" style={{ height }} />;
}
