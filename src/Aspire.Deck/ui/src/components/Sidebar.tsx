import type { ComponentType } from "react";
import {
  CanvasIcon,
  ConsoleIcon,
  LogsIcon,
  MetricsIcon,
  ParametersIcon,
  ResourcesIcon,
  TracesIcon,
} from "./Icons";

export type PageId = "resources" | "parameters" | "console" | "logs" | "traces" | "metrics" | "canvases";

interface NavEntry {
  id: PageId;
  label: string;
  icon: ComponentType<{ size?: number; className?: string }>;
}

const NAV: NavEntry[] = [
  { id: "resources", label: "Resources", icon: ResourcesIcon },
  { id: "parameters", label: "Parameters", icon: ParametersIcon },
  { id: "console", label: "Console", icon: ConsoleIcon },
  { id: "logs", label: "Structured Logs", icon: LogsIcon },
  { id: "traces", label: "Traces", icon: TracesIcon },
  { id: "metrics", label: "Metrics", icon: MetricsIcon },
  { id: "canvases", label: "Canvases", icon: CanvasIcon },
];

export function Sidebar({
  active,
  onNavigate,
  counts,
  version,
}: {
  active: PageId;
  onNavigate: (page: PageId) => void;
  counts: Partial<Record<PageId, number>>;
  version: string;
}) {
  return (
    <nav className="sidebar">
      <div className="sidebar__brand">
        <div className="sidebar__logo">A</div>
        <div className="sidebar__brand-text">
          <span className="sidebar__brand-title">Aspire Deck</span>
          <span className="sidebar__brand-sub">Distributed app dashboard</span>
        </div>
      </div>

      <div className="sidebar__section">Observe</div>
      {NAV.map((entry) => {
        const Icon = entry.icon;
        const count = counts[entry.id];
        return (
          <button
            key={entry.id}
            className={`nav-item ${active === entry.id ? "active" : ""}`}
            onClick={() => onNavigate(entry.id)}
          >
            <Icon size={18} className="nav-item__icon" />
            <span className="nav-item__label">{entry.label}</span>
            {count !== undefined ? <span className="nav-item__badge">{count}</span> : null}
          </button>
        );
      })}

      <div className="sidebar__spacer" />
      <div className="sidebar__foot">Aspire Deck {version}</div>
    </nav>
  );
}
