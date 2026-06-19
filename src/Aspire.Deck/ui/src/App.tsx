import { useEffect, useState } from "react";
import type { DeckConfig } from "./api/types";
import { getConfig } from "./api/deck";
import { Sidebar, type PageId } from "./components/Sidebar";
import { TopBar } from "./components/TopBar";
import { NotConnected } from "./components/NotConnected";
import { useConnection, useResources, useTelemetry } from "./lib/useDeckEvent";
import { useTheme } from "./lib/theme";
import { ResourcesPage } from "./pages/ResourcesPage";
import { ConsolePage } from "./pages/ConsolePage";
import { StructuredLogsPage } from "./pages/StructuredLogsPage";
import { TracesPage } from "./pages/TracesPage";
import { MetricsPage } from "./pages/MetricsPage";
import { CanvasesPage } from "./pages/CanvasesPage";

export function App() {
  const { theme, toggleTheme } = useTheme();
  const connection = useConnection();
  const { resources } = useResources();
  const telemetry = useTelemetry();
  const [config, setConfig] = useState<DeckConfig | null>(null);
  const [page, setPage] = useState<PageId>("resources");

  useEffect(() => {
    let cancelled = false;
    void getConfig().then((result) => {
      if (!cancelled) {
        setConfig(result);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const counts: Partial<Record<PageId, number>> = {
    resources: resources.filter((r) => !r.isHidden).length,
    logs: telemetry?.recentLogs.length ?? undefined,
    traces: telemetry ? new Set(telemetry.recentSpans.map((s) => s.traceId)).size : undefined,
    metrics: telemetry?.metrics.length ?? undefined,
  };

  // When the resource service can't be reached and nothing has streamed in yet,
  // replace the page content with an explanatory splash so the window never just
  // sits empty (which is indistinguishable from a broken/blank app).
  const resourceState = connection.resourceService;
  const showNotConnected =
    (resourceState === "disconnected" || resourceState === "error") && resources.length === 0;

  return (
    <div className="app">
      <div className="app__sidebar">
        <Sidebar
          active={page}
          onNavigate={setPage}
          counts={counts}
          version={config?.version ?? ""}
        />
      </div>
      <div className="app__topbar">
        <TopBar config={config} connection={connection} theme={theme} onToggleTheme={toggleTheme} />
      </div>
      <main className="app__content">
        {showNotConnected ? (
          <NotConnected config={config} state={resourceState} />
        ) : (
          <>
            {page === "resources" ? <ResourcesPage /> : null}
            {page === "console" ? <ConsolePage /> : null}
            {page === "logs" ? <StructuredLogsPage /> : null}
            {page === "traces" ? <TracesPage /> : null}
            {page === "metrics" ? <MetricsPage /> : null}
            {page === "canvases" ? <CanvasesPage /> : null}
          </>
        )}
      </main>
    </div>
  );
}
