import { useCallback, useEffect, useState } from "react";
import type { DeckConfig } from "./api/types";
import { PARAMETER_RESOURCE_TYPE } from "./api/types";
import { getConfig } from "./api/deck";
import type { Theme } from "./lib/theme";
import { Sidebar } from "./components/Sidebar";
import { TopBar } from "./components/TopBar";
import { NotConnected } from "./components/NotConnected";
import { useConnection, useResources, useTelemetry, useApphosts, useInteractions } from "./lib/useDeckEvent";
import { ResourcesPage } from "./pages/ResourcesPage";
import { ParametersPage } from "./pages/ParametersPage";
import { ConsolePage } from "./pages/ConsolePage";
import { StructuredLogsPage } from "./pages/StructuredLogsPage";
import { TracesPage } from "./pages/TracesPage";
import { MetricsPage } from "./pages/MetricsPage";
import { CanvasesPage } from "./pages/CanvasesPage";
import { InteractionPane } from "./components/InteractionPane";
import { NotificationStack } from "./components/NotificationStack";
import {
  dashboardRouteHref,
  readDashboardRoute,
  type DashboardRoute,
  type PageId,
} from "./lib/routes";

export function App({ theme, onToggleTheme }: { theme: Theme; onToggleTheme: () => void }) {
  const connection = useConnection();
  const { resources } = useResources();
  const telemetry = useTelemetry();
  const apphosts = useApphosts();
  const interactions = useInteractions();
  const [config, setConfig] = useState<DeckConfig | null>(null);
  const [route, setRoute] = useState<DashboardRoute>(readDashboardRoute);
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
    apphosts.length === 0 ||
    ((resourceState === "disconnected" || resourceState === "error") && resources.length === 0);

  // Interactions split by surface: inputs dialogs and message boxes are blocking and
  // shown one-at-a-time in the side drawer; notifications are non-blocking and stack
  // as toasts (matching the dashboard, which routes notifications to message bars).
  const dialog = interactions.find((i) => i.kind === "inputsDialog" || i.kind === "messageBox") ?? null;
  const notifications = interactions.filter((i) => i.kind === "notification");

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
        <TopBar config={config} connection={connection} apphosts={apphosts} theme={theme} onToggleTheme={onToggleTheme} />
      </div>
      <main className="app__content">
        {showNotConnected ? (
          <NotConnected config={config} state={resourceState === "error" ? "error" : "disconnected"} />
        ) : (
          <>
            {page === "resources" ? <ResourcesPage /> : null}
            {page === "parameters" ? <ParametersPage /> : null}
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
                onClearRoute={() => navigate({ page: "logs" })}
                onNavigateToTrace={(traceId, spanId) => navigate({
                  page: "traces",
                  traceId,
                  spanId: spanId ?? undefined,
                })}
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
              />
            ) : null}
            {page === "metrics" ? (
              <MetricsPage
                routeResourceName={route.metricResourceName ?? null}
                routeMetricName={route.metricName ?? null}
                routeWindowSeconds={route.metricWindowSeconds ?? 300}
                routeView={route.metricView ?? "chart"}
                routePaused={route.metricsPaused ?? false}
                onRouteChange={(metricRoute) => navigate({
                  page: "metrics",
                  metricResourceName: metricRoute.resourceName ?? undefined,
                  metricName: metricRoute.metricName ?? undefined,
                  metricWindowSeconds: metricRoute.windowSeconds === 300 ? undefined : metricRoute.windowSeconds,
                  metricView: metricRoute.view === "chart" ? undefined : metricRoute.view,
                  metricsPaused: metricRoute.paused || undefined,
                })}
              />
            ) : null}
            {page === "canvases" ? <CanvasesPage /> : null}
          </>
        )}
      </main>
      {dialog ? <InteractionPane interaction={dialog} /> : null}
      <NotificationStack notifications={notifications} />
    </div>
  );
}
