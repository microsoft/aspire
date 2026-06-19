import type { AppHostInfo, ConnectionState, ConnectionTarget, DeckConfig } from "../api/types";
import { ConnectionPill } from "./ConnectionPill";
import { AppHostSwitcher } from "./AppHostSwitcher";
import { MoonIcon, SunIcon } from "./Icons";
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
  const appName = config?.applicationName ?? "Aspire application";
  return (
    <header className="topbar">
      <div className="topbar__title">
        <span className="topbar__app">{appName}</span>
        <span className="topbar__app-sub">
          {config?.resourceServiceUrl ?? "No resource service connected"}
        </span>
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
