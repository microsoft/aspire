import type { AppHostInfo, ConnectionState, ConnectionTarget, DeckConfig } from "../api/types";
import { ConnectionPill } from "./ConnectionPill";
import { AppHostSwitcher } from "./AppHostSwitcher";
import { MoonIcon, NamedIcon, SunIcon } from "../toolkit";
import type { Theme } from "../lib/theme";
import { UserProfile } from "./UserProfile";

const TARGET_ORDER: ConnectionTarget[] = ["resourceService", "otlpGrpc", "otlpHttp"];

export function TopBar({
  config,
  connection,
  apphosts,
  theme,
  onToggleTheme,
  onHelp,
  onAIAgents,
  onAssistant,
  onNotifications,
  notificationCount,
  onSettings,
}: {
  config: DeckConfig | null;
  connection: Record<ConnectionTarget, ConnectionState>;
  apphosts: AppHostInfo[];
  theme: Theme;
  onToggleTheme: () => void;
  onHelp: () => void;
  onAIAgents: () => void;
  onAssistant: () => void;
  onNotifications: () => void;
  notificationCount: number;
  onSettings: () => void;
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

      <a
        className="icon-btn"
        href="https://aka.ms/aspire/repo"
        target="_blank"
        rel="noreferrer noopener"
        title="Aspire repository"
        aria-label="Aspire repository"
      >
        <NamedIcon name="BranchFork" size={17} />
      </a>
      <button className="icon-btn" type="button" onClick={onHelp} title="Help" aria-label="Help">
        <NamedIcon name="QuestionCircle" size={17} />
      </button>
      {config?.isAgentHelpEnabled && config.agentHelpMarkdown ? (
        <button className="icon-btn" type="button" onClick={onAIAgents} title="AI agents" aria-label="AI agents">
          <NamedIcon name="ChatSparkle" size={17} />
        </button>
      ) : null}
      {config?.isAssistantEnabled ? (
        <button className="icon-btn" type="button" onClick={onAssistant} title="Assistant" aria-label="Assistant">
          <NamedIcon name="BrainCircuit" size={17} />
        </button>
      ) : null}
      <button className="icon-btn topbar__notification-button" type="button" onClick={onNotifications} title="Notifications" aria-label={`Notifications ${notificationCount}`}>
        <NamedIcon name="Info" size={17} />
        {notificationCount > 0 ? <span className="topbar__notification-count" aria-hidden="true">{notificationCount}</span> : null}
      </button>

      <button
        className="icon-btn"
        onClick={onToggleTheme}
        title={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
        aria-label="Toggle theme"
      >
        {theme === "dark" ? <SunIcon size={17} /> : <MoonIcon size={17} />}
      </button>
      <button className="icon-btn" type="button" onClick={onSettings} title="Settings" aria-label="Settings">
        <NamedIcon name="Settings" size={17} />
      </button>
      {config?.user ? <UserProfile user={config.user} /> : null}
    </header>
  );
}
