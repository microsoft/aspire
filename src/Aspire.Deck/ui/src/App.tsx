import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { DeckConfig } from "./api/types";
import { PARAMETER_RESOURCE_TYPE } from "./api/types";
import { getConfig, retryBackendConnection } from "./api/deck";
import type { Theme, ThemeChoice } from "./lib/theme";
import type { TimeFormatChoice } from "./lib/timeFormat";
import { Sidebar } from "./components/Sidebar";
import { TopBar } from "./components/TopBar";
import { NotConnected } from "./components/NotConnected";
import { useConnection, useResources, useTelemetry, useApphosts, useInteractions } from "./lib/useDeckEvent";
import { ResourcesPage } from "./pages/ResourcesPage";
import { ParametersPage } from "./pages/ParametersPage";
import { ConsolePage } from "./pages/ConsolePage";
import { StructuredLogsPage } from "./pages/StructuredLogsPage";
import { TracesPage } from "./pages/TracesPage";
import { serializeTelemetryFilters } from "./lib/telemetryFilters";
import { MetricsPage } from "./pages/MetricsPage";
import { CanvasesPage } from "./pages/CanvasesPage";
import { RouteErrorPage } from "./pages/RouteErrorPage";
import { InteractionPane } from "./components/InteractionPane";
import { NotificationStack } from "./components/NotificationStack";
import { HelpDialog } from "./components/HelpDialog";
import { SettingsDialog } from "./components/SettingsDialog";
import { NotificationCenter, type NotificationHistoryItem } from "./components/NotificationCenter";
import { SystemNotifications } from "./components/SystemNotifications";
import { ManageDataDrawer } from "./components/ManageDataDrawer";
import { AIAgentsDrawer } from "./components/AIAgentsDrawer";
import { AssistantPanel } from "./components/AssistantPanel";
import {
  dashboardRouteHref,
  readDashboardRoute,
  type DashboardRoute,
  type PageId,
} from "./lib/routes";

export function App({
  theme,
  themeChoice,
  onThemeChoiceChange,
  onToggleTheme,
  timeFormatChoice,
  onTimeFormatChoiceChange,
}: {
  theme: Theme;
  themeChoice: ThemeChoice;
  onThemeChoiceChange: (choice: ThemeChoice) => void;
  onToggleTheme: () => void;
  timeFormatChoice: TimeFormatChoice;
  onTimeFormatChoiceChange: (choice: TimeFormatChoice) => void;
}) {
  const connection = useConnection();
  const { resources } = useResources();
  const telemetry = useTelemetry();
  const apphosts = useApphosts();
  const interactions = useInteractions();
  const [config, setConfig] = useState<DeckConfig | null>(null);
  const [route, setRoute] = useState<DashboardRoute>(readDashboardRoute);
  const [helpOpen, setHelpOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [manageDataOpen, setManageDataOpen] = useState(false);
  const [aiAgentsOpen, setAIAgentsOpen] = useState(false);
  const [assistantOpen, setAssistantOpen] = useState(false);
  const [assistantPrompt, setAssistantPrompt] = useState<string | null>(null);
  const [notificationCenterOpen, setNotificationCenterOpen] = useState(false);
  const [notificationHistory, setNotificationHistory] = useState<NotificationHistoryItem[]>(() => {
    try {
      return JSON.parse(window.sessionStorage.getItem("aspire-deck-notification-history") ?? "[]") as NotificationHistoryItem[];
    } catch {
      return [];
    }
  });
  const notificationIdsSeen = useRef(new Set(notificationHistory.map((item) => item.interactionId)));
  const page = route.page;

  useEffect(() => {
    const onPopState = (): void => setRoute(readDashboardRoute());
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  const navigate = useCallback((nextRoute: DashboardRoute): void => {
    const href = dashboardRouteHref(nextRoute);
    const current = `${window.location.pathname}${window.location.search}`;
    if (href !== current) {
      window.history.pushState({}, "", href);
    }
    setRoute(nextRoute);
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const target = event.target as HTMLElement | null;
      if (event.metaKey || event.ctrlKey || event.altKey || target?.closest("input, textarea, select, [contenteditable='true']")) {
        return;
      }
      if (event.key === "?") {
        event.preventDefault();
        setHelpOpen(true);
        return;
      }
      if (event.shiftKey && event.key.toLowerCase() === "s") {
        event.preventDefault();
        setSettingsOpen(true);
        return;
      }
      if (event.shiftKey || helpOpen || settingsOpen) {
        return;
      }
      const pageByKey: Partial<Record<string, PageId>> = {
        r: "resources",
        c: "console",
        s: "logs",
        t: "traces",
        m: "metrics",
      };
      const nextPage = pageByKey[event.key.toLowerCase()];
      if (nextPage) {
        event.preventDefault();
        navigate({ page: nextPage });
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [helpOpen, navigate, settingsOpen]);

  useEffect(() => {
    let cancelled = false;
    void getConfig()
      .then((result) => {
        if (!cancelled) {
          setConfig(result);
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  const counts: Partial<Record<PageId, number>> = {
    resources: resources.filter((r) => !r.isHidden && r.resourceType !== PARAMETER_RESOURCE_TYPE).length,
    parameters: resources.filter((r) => !r.isHidden && r.resourceType === PARAMETER_RESOURCE_TYPE).length,
    logs: telemetry?.recentLogs.length ?? undefined,
    traces: telemetry ? new Set(telemetry.recentSpans.map((s) => s.traceId)).size : undefined,
    metrics: telemetry?.metrics.length ?? undefined,
  };

  // Show the explanatory splash when there's nothing to show — either no AppHost is
  // attached at all (an idle hub), or the active AppHost's resource service can't be
  // reached and nothing has streamed in yet. This keeps the window from sitting empty
  // (which is indistinguishable from a broken/blank app).
  const resourceState = connection.resourceService;
  const showNotConnected =
    page === "resources"
    && (apphosts.length === 0 || (resourceState !== "connected" && resources.length === 0));

  // Interactions split by surface: inputs dialogs and message boxes are blocking and
  // shown one-at-a-time in the side drawer; notifications are non-blocking and stack
  // as toasts (matching the dashboard, which routes notifications to message bars).
  const dialog = interactions.find((i) => i.kind === "inputsDialog" || i.kind === "messageBox") ?? null;
  const notifications = interactions.filter((i) => i.kind === "notification");

  useLayoutEffect(() => {
    const additions = notifications
      .filter((notification) => !notificationIdsSeen.current.has(notification.interactionId))
      .map<NotificationHistoryItem>((notification) => ({
        interactionId: notification.interactionId,
        title: notification.title,
        message: notification.message,
        intent: notification.intent,
        enableMessageMarkdown: notification.enableMessageMarkdown,
        receivedAt: new Date().toISOString(),
      }));
    if (additions.length === 0) return;
    additions.forEach((notification) => notificationIdsSeen.current.add(notification.interactionId));
    setNotificationHistory((current) => {
      const next = [...current, ...additions];
      window.sessionStorage.setItem("aspire-deck-notification-history", JSON.stringify(next));
      return next;
    });
  }, [notifications]);

  return (
    <div className="app">
      <div className="app__sidebar">
        <Sidebar
          active={page}
          onNavigate={(nextPage) => navigate({ page: nextPage })}
          counts={counts}
          version={config?.version ?? ""}
        />
      </div>
      <div className="app__topbar">
        <TopBar
          config={config}
          connection={connection}
          apphosts={apphosts}
          theme={theme}
          onToggleTheme={onToggleTheme}
          onHelp={() => setHelpOpen(true)}
          onAIAgents={() => setAIAgentsOpen(true)}
          onAssistant={() => {
            setAssistantPrompt(null);
            setAssistantOpen((current) => !current);
          }}
          onNotifications={() => setNotificationCenterOpen(true)}
          notificationCount={notificationHistory.length}
          onSettings={() => setSettingsOpen(true)}
        />
      </div>
      <main className="app__content">
        {showNotConnected ? (
          <NotConnected
            config={config}
            state={resourceState === "error" ? "error" : "disconnected"}
            onRetry={retryBackendConnection}
          />
        ) : (
          <>
            {page === "resources" ? (
              <ResourcesPage
                route={{
                  resourceName: route.resourceName ?? null,
                  query: route.resourceQuery ?? "",
                  hiddenTypes: route.resourceHiddenTypes ?? [],
                  hiddenStates: route.resourceHiddenStates ?? [],
                  hiddenHealth: route.resourceHiddenHealth ?? [],
                  showHidden: route.resourceShowHidden ?? false,
                  showType: route.resourceShowType ?? false,
                  collapsed: route.resourceCollapsed ?? [],
                  sortColumn: route.resourceSortColumn ?? "name",
                  sortDirection: route.resourceSortDirection ?? "ascending",
                  view: route.resourceView ?? "table",
                }}
                onRouteChange={(state) => navigate({
                  page: "resources",
                  resourceName: state.resourceName ?? undefined,
                  resourceQuery: state.query || undefined,
                  resourceHiddenTypes: state.hiddenTypes.length > 0 ? state.hiddenTypes : undefined,
                  resourceHiddenStates: state.hiddenStates.length > 0 ? state.hiddenStates : undefined,
                  resourceHiddenHealth: state.hiddenHealth.length > 0 ? state.hiddenHealth : undefined,
                  resourceShowHidden: state.showHidden || undefined,
                  resourceShowType: state.showType || undefined,
                  resourceCollapsed: state.collapsed.length > 0 ? state.collapsed : undefined,
                  resourceSortColumn: state.sortColumn === "name" ? undefined : state.sortColumn,
                  resourceSortDirection: state.sortDirection === "ascending" ? undefined : state.sortDirection,
                  resourceView: state.view === "table" ? undefined : state.view,
                })}
              />
            ) : null}
            {page === "parameters" ? (
              <ParametersPage
                route={{
                  resourceName: route.parameterName ?? null,
                  query: route.parameterQuery ?? "",
                  sortColumn: route.parameterSortColumn ?? "name",
                  sortDirection: route.parameterSortDirection ?? "ascending",
                }}
                onRouteChange={(parameterRoute) => navigate({
                  page: "parameters",
                  parameterName: parameterRoute.resourceName ?? undefined,
                  parameterQuery: parameterRoute.query || undefined,
                  parameterSortColumn: parameterRoute.sortColumn === "name" ? undefined : parameterRoute.sortColumn,
                  parameterSortDirection: parameterRoute.sortDirection === "ascending" ? undefined : parameterRoute.sortDirection,
                })}
              />
            ) : null}
            {page === "console" ? (
              <ConsolePage
                routeResourceName={route.consoleResourceName ?? null}
                routeShowTimestamps={route.consoleShowTimestamps ?? false}
                routeTimestampsUtc={route.consoleTimestampsUtc ?? false}
                routeWrapLines={route.consoleWrapLines ?? false}
                routePaused={route.consolePaused ?? false}
                onRouteChange={(consoleRoute) => navigate({
                  page: "console",
                  consoleResourceName: consoleRoute.resourceName ?? undefined,
                  consoleShowTimestamps: consoleRoute.showTimestamps || undefined,
                  consoleTimestampsUtc: consoleRoute.timestampsUtc || undefined,
                  consoleWrapLines: consoleRoute.wrapLines || undefined,
                  consolePaused: consoleRoute.paused || undefined,
                })}
              />
            ) : null}
            {page === "logs" ? (
              <StructuredLogsPage
                routeSpanId={route.spanId ?? null}
                routeResourceName={route.logResourceName ?? null}
                routeQuery={route.logQuery ?? ""}
                routeSeverity={route.logSeverity ?? "All"}
                routePaused={route.logPaused ?? false}
                routeFilters={route.logFilters ?? null}
                routeLogId={route.logId ?? null}
                onClearRoute={() => navigate({ page: "logs" })}
                onFilterRouteChange={(logState) => navigate({
                  page: "logs",
                  logResourceName: logState.resourceName ?? undefined,
                  logQuery: logState.query || undefined,
                  logSeverity: logState.severity === "All" ? undefined : logState.severity,
                  logPaused: logState.paused || undefined,
                  logFilters: serializeTelemetryFilters(logState.filters),
                  logId: route.logId,
                })}
                onSelectedLogChange={(logId) => navigate({ ...route, page: "logs", logId: logId ?? undefined })}
                onNavigateToTrace={(traceId, spanId) => navigate({
                  page: "traces",
                  traceId,
                  spanId: spanId ?? undefined,
                })}
                onExplainErrors={config?.isAssistantEnabled ? (prompt) => {
                  setAssistantPrompt(prompt);
                  setAssistantOpen(true);
                } : undefined}
              />
            ) : null}
            {page === "traces" ? (
              <TracesPage
                routeTraceId={route.traceId ?? null}
                routeSpanId={route.spanId ?? null}
                routeResourceName={route.traceResourceName ?? null}
                routeType={route.traceType ?? "all"}
                routeQuery={route.traceQuery ?? ""}
                routeMinDurationMs={route.traceMinDurationMs ?? 0}
                routePaused={route.tracePaused ?? false}
                routeFilters={route.traceFilters ?? null}
                onFilterRouteChange={(traceState) => navigate({
                  ...route,
                  page: "traces",
                  traceId: undefined,
                  spanId: undefined,
                  traceResourceName: traceState.resourceName ?? undefined,
                  traceType: traceState.type === "all" ? undefined : traceState.type,
                  traceQuery: traceState.query || undefined,
                  traceMinDurationMs: traceState.minDurationMs || undefined,
                  tracePaused: traceState.paused || undefined,
                  traceFilters: serializeTelemetryFilters(traceState.filters),
                })}
                onSelectSpan={(span) => navigate({
                  ...route,
                  page: "traces",
                  traceId: span.traceId,
                  spanId: span.spanId,
                })}
                onNavigateToSpan={(traceId, spanId) => navigate({
                  ...route,
                  page: "traces",
                  traceId,
                  spanId: spanId ?? undefined,
                })}
                onNavigateToLogs={(spanId) => navigate({ page: "logs", spanId })}
                onCloseDetails={() => navigate({
                  ...route,
                  page: "traces",
                  traceId: undefined,
                  spanId: undefined,
                })}
                onExplainErrors={config?.isAssistantEnabled ? (prompt) => {
                  setAssistantPrompt(prompt);
                  setAssistantOpen(true);
                } : undefined}
              />
            ) : null}
            {page === "metrics" ? (
              <MetricsPage
                routeResourceName={route.metricResourceName ?? null}
                routeMeterName={route.metricMeterName ?? null}
                routeMetricName={route.metricName ?? null}
                routeWindowSeconds={route.metricWindowSeconds ?? 300}
                routeView={route.metricView ?? "chart"}
                routePaused={route.metricsPaused ?? false}
                routeDimensions={route.metricDimensions ?? {}}
                routeHistogramMode={route.metricHistogramMode ?? (route.metricHistogramCount ? "count" : "percentiles")}
                routeZoomStartMs={route.metricZoomStartMs ?? null}
                routeZoomEndMs={route.metricZoomEndMs ?? null}
                onRouteChange={(metricRoute) => navigate({
                  page: "metrics",
                  metricResourceName: metricRoute.resourceName ?? undefined,
                  metricMeterName: metricRoute.meterName ?? undefined,
                  metricName: metricRoute.metricName ?? undefined,
                  metricWindowSeconds: metricRoute.windowSeconds === 300 ? undefined : metricRoute.windowSeconds,
                  metricView: metricRoute.view === "chart" ? undefined : metricRoute.view,
                  metricsPaused: metricRoute.paused || undefined,
                  metricDimensions: Object.keys(metricRoute.dimensions).length > 0 ? metricRoute.dimensions : undefined,
                  metricHistogramMode: metricRoute.histogramMode === "percentiles" ? undefined : metricRoute.histogramMode,
                  metricZoomStartMs: metricRoute.zoomStartMs ?? undefined,
                  metricZoomEndMs: metricRoute.zoomEndMs ?? undefined,
                })}
                onNavigateToSpan={(traceId, spanId) => navigate({ page: "traces", traceId, spanId })}
              />
            ) : null}
            {page === "canvases" ? <CanvasesPage /> : null}
            {page === "notFound" || page === "error" ? (
              <RouteErrorPage kind={page} onHome={() => navigate({ page: "resources" })} />
            ) : null}
          </>
        )}
      </main>
      {dialog ? <InteractionPane interaction={dialog} /> : null}
      <NotificationStack
        notifications={notifications}
        onPrimaryAction={(notification) => {
          // This interaction is currently identified by its server-provided action text.
          // The interaction protocol does not expose an action kind or navigation target.
          if (notification.kind === "notification" && notification.primaryButtonText === "Enter values") {
            navigate({ page: "parameters" });
          }
        }}
      />
      <SystemNotifications
        config={config}
        connectionError={resourceState === "error" && resources.length > 0}
        onRetryConnection={retryBackendConnection}
      />
      <HelpDialog open={helpOpen} onClose={() => setHelpOpen(false)} />
      <NotificationCenter
        open={notificationCenterOpen}
        notifications={notificationHistory}
        onClear={() => {
          setNotificationHistory([]);
          window.sessionStorage.removeItem("aspire-deck-notification-history");
        }}
        onClose={() => setNotificationCenterOpen(false)}
      />
      <SettingsDialog
        open={settingsOpen}
        config={config}
        themeChoice={themeChoice}
        onThemeChoiceChange={onThemeChoiceChange}
        timeFormatChoice={timeFormatChoice}
        onTimeFormatChoiceChange={onTimeFormatChoiceChange}
        onManageData={() => {
          setSettingsOpen(false);
          setManageDataOpen(true);
        }}
        onClose={() => setSettingsOpen(false)}
      />
      {manageDataOpen ? <ManageDataDrawer onClose={() => setManageDataOpen(false)} /> : null}
      {aiAgentsOpen && config?.agentHelpMarkdown ? (
        <AIAgentsDrawer markdown={config.agentHelpMarkdown} onClose={() => setAIAgentsOpen(false)} />
      ) : null}
      {assistantOpen ? <AssistantPanel initialPrompt={assistantPrompt} onClose={() => {
        setAssistantOpen(false);
        setAssistantPrompt(null);
      }} /> : null}
    </div>
  );
}
