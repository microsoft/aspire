import type { AppHostInfo, ConnectionState, ConnectionTarget, DeckConfig } from "../api/types";
import { ConnectionPill } from "./ConnectionPill";
import { AppHostSwitcher } from "./AppHostSwitcher";
import { IconButton, MoonIcon, NamedIcon, SunIcon } from "../toolkit";
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
      <IconButton label="Help" onClick={onHelp} icon={<NamedIcon name="QuestionCircle" size={17} />} />
      {config?.isAgentHelpEnabled && config.agentHelpMarkdown ? (
        <IconButton label="AI agents" onClick={onAIAgents} icon={<NamedIcon name="ChatSparkle" size={17} />} />
      ) : null}
      <IconButton
        label={`Notifications ${notificationCount}`}
        className="topbar__notification-button"
        onClick={onNotifications}
        icon={<>
          <NamedIcon name="Info" size={17} />
          {notificationCount > 0 ? <span className="topbar__notification-count" aria-hidden="true">{notificationCount}</span> : null}
        </>}
      />
      <IconButton
        label="Toggle theme"
        onClick={onToggleTheme}
        title={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
        icon={theme === "dark" ? <SunIcon size={17} /> : <MoonIcon size={17} />}
      />
      <IconButton label="Settings" onClick={onSettings} icon={<NamedIcon name="Settings" size={17} />} />
      {config?.user ? <UserProfile user={config.user} /> : null}
    </header>
  );
}
