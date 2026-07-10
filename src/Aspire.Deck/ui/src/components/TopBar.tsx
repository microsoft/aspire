import type { AppHostInfo, ConnectionState, ConnectionTarget, DeckConfig } from "../api/types";
import { ConnectionPill } from "./ConnectionPill";
import { AppHostSwitcher } from "./AppHostSwitcher";
import { MoonIcon, SunIcon } from "../toolkit";
import type { Theme } from "../lib/theme";

const TARGET_ORDER: ConnectionTarget[] = ["resourceService", "otlpGrpc", "otlpHttp"];

export function TopBar({
  config,
  connection,
  apphosts,
  theme,
  onToggleTheme,
}: {
  config: DeckConfig | null;
  connection: Record<ConnectionTarget, ConnectionState>;
  apphosts: AppHostInfo[];
  theme: Theme;
  onToggleTheme: () => void;
}) {
  const active = apphosts.find((a) => a.active);
  // The title reflects the AppHost the UI is currently showing. With multiple
  // attached AppHosts this follows the switcher's active selection; with none
  // attached yet it falls back to the bootstrap config.
  const appName = active?.name ?? config?.applicationName ?? "Aspire application";
  const appSub = active?.resourceServiceUrl ?? config?.resourceServiceUrl ?? "No resource service connected";
  return (
    <header className="topbar">
      <div className="topbar__title">
        <span className="topbar__app">{appName}</span>
        <span className="topbar__app-sub">{appSub}</span>
      </div>

      <AppHostSwitcher apphosts={apphosts} />

      <div className="topbar__spacer" />

      <div className="topbar__pills">
        {TARGET_ORDER.map((target) => (
          <ConnectionPill key={target} target={target} state={connection[target]} />
        ))}
      </div>

      <button
        className="icon-btn"
        onClick={onToggleTheme}
        title={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
        aria-label="Toggle theme"
      >
        {theme === "dark" ? <SunIcon size={17} /> : <MoonIcon size={17} />}
      </button>
    </header>
  );
}
