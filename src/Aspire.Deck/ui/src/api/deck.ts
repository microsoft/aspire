// Transport-neutral data layer. Tauri dispatches to the Rust backend via
// `invoke`/`listen`, explicit `?backend=http` mode calls the existing ASP.NET dashboard,
// `?backend=aot` negotiates versioned capabilities with the Native AOT host,
// and standalone browser development uses the in-process mock. Every transport
// exposes the same function surface so feature code remains independent of it.
//
// Tauri injects `__TAURI_INTERNALS__` (v2) / `__TAURI__` before the bundle runs.
// HTTP mode is opt-in so a missing live backend can never silently become demo data.

import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { httpBackend } from "./http";
import { mockBackend } from "./mock";
import { nativeBackend } from "./native";
import type {
  AppHostInfo,
  AssistantChatRequest,
  AssistantEvent,
  AssistantInfo,
  CanvasManifest,
  CommandResponse,
  ConnectionStatus,
  ConsoleLogEvent,
  DeckConfig,
  ExecuteCommandArgs,
  InteractionInfo,
  Resource,
  ResourcesEvent,
  MetricSeriesResponse,
  MetricSeriesQuery,
  ManageDataExport,
  ManageDataRequest,
  ManageDataResponse,
  TelemetrySummary,
} from "./types";

// (MetricSeriesQuery re-exported from types for callers that import from deck.)
export type { MetricSeriesQuery } from "./types";

type Unsubscribe = () => void;

export function isTauri(): boolean {
  return typeof window !== "undefined" && ("__TAURI_INTERNALS__" in window || "__TAURI__" in window);
}

export function isHttpBackend(): boolean {
  if (typeof window === "undefined") {
    return false;
  }

  const backend = getBackendMode();
  // AOT mode is a strangler path: capabilities not yet in the versioned contract
  // deliberately continue through the existing HTTP backend until parity is proven.
  return backend === "http" || backend === "aot";
}

export function isAotBackend(): boolean {
  return typeof window !== "undefined" && getBackendMode() === "aot";
}

function getBackendMode(): string | null {
  const requested = new URLSearchParams(window.location.search).get("backend");
  if (requested !== null) {
    return requested;
  }

  // The Native AOT host rewrites this build-time marker when it serves index.html.
  // Vite and Tauri keep the standalone marker, preserving their existing mock/bridge defaults.
  return document.querySelector<HTMLMetaElement>('meta[name="aspire-dashboard-backend"]')?.content === "aot"
    ? "aot"
    : null;
}

// Bridges Tauri's promise-returning `listen` (which resolves to an unlisten fn)
// to a synchronous unsubscribe so call sites in both modes look the same.
function bridgeListen<T>(event: string, cb: (payload: T) => void): Unsubscribe {
  let unlisten: UnlistenFn | null = null;
  let cancelled = false;
  void listen<T>(event, (e) => cb(e.payload)).then((fn) => {
    if (cancelled) {
      fn();
    } else {
      unlisten = fn;
    }
  });
  return () => {
    cancelled = true;
    unlisten?.();
  };
}

export function getConfig(): Promise<DeckConfig> {
  if (isTauri()) {
    return invoke<DeckConfig>("deck_get_config");
  }
  if (isAotBackend()) {
    return nativeBackend.getConfig();
  }
  if (isHttpBackend()) {
    return httpBackend.getConfig();
  }
  return Promise.resolve(mockBackend.getConfig());
}

export function retryBackendConnection(): void {
  if (isHttpBackend()) {
    httpBackend.retryConnection();
    return;
  }
  window.location.reload();
}

export function getManageData(): Promise<ManageDataResponse> {
  if (isHttpBackend()) {
    return httpBackend.getManageData();
  }
  return Promise.resolve(mockBackend.getManageData());
}

export function exportManageData(request: ManageDataRequest): Promise<ManageDataExport> {
  if (isHttpBackend()) {
    return httpBackend.exportManageData(request);
  }
  return Promise.resolve(mockBackend.exportManageData(request));
}

export function importManageData(file: File): Promise<void> {
  if (isHttpBackend()) {
    return httpBackend.importManageData(file);
  }
  mockBackend.importManageData(file);
  return Promise.resolve();
}

export function removeManageData(request: ManageDataRequest): Promise<void> {
  if (isHttpBackend()) {
    return httpBackend.removeManageData(request);
  }
  mockBackend.removeManageData(request);
  return Promise.resolve();
}

export function getAssistantInfo(): Promise<AssistantInfo> {
  if (isHttpBackend()) {
    return httpBackend.getAssistantInfo();
  }
  return Promise.resolve(mockBackend.getAssistantInfo());
}

export function streamAssistantChat(
  request: AssistantChatRequest,
  onEvent: (event: AssistantEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  if (isHttpBackend()) {
    return httpBackend.streamAssistantChat(request, onEvent, signal);
  }
  return mockBackend.streamAssistantChat(request, onEvent, signal);
}

export function listResources(): Promise<Resource[]> {
  if (isTauri()) {
    return invoke<Resource[]>("deck_list_resources");
  }
  if (isAotBackend()) {
    return nativeBackend.hasCapability("resources").then((supported) => (
      supported ? nativeBackend.listResources() : httpBackend.listResources()
    ));
  }
  if (isHttpBackend()) {
    return httpBackend.listResources();
  }
  return Promise.resolve(mockBackend.listResources());
}

export function listCanvases(): Promise<CanvasManifest[]> {
  if (isTauri()) {
    return invoke<CanvasManifest[]>("deck_list_canvases");
  }
  if (isHttpBackend()) {
    return httpBackend.listCanvases();
  }
  return Promise.resolve(mockBackend.listCanvases());
}

async function applyAotApphostIdentity(apphosts: AppHostInfo[]): Promise<AppHostInfo[]> {
  const config = await nativeBackend.getConfig();
  return apphosts.map((apphost) => (
    apphost.active ? { ...apphost, name: config.applicationName ?? apphost.name } : apphost
  ));
}

export function listApphosts(): Promise<AppHostInfo[]> {
  if (isTauri()) {
    return invoke<AppHostInfo[]>("deck_list_apphosts");
  }
  if (isAotBackend()) {
    return httpBackend.listApphosts(nativeBackend.getConfig).then(applyAotApphostIdentity);
  }
  if (isHttpBackend()) {
    return httpBackend.listApphosts();
  }
  return Promise.resolve(mockBackend.listApphosts());
}

export function selectApphost(id: string): Promise<void> {
  if (isTauri()) {
    return invoke<void>("deck_select_apphost", { id });
  }
  if (isHttpBackend()) {
    return httpBackend.selectApphost(id);
  }
  mockBackend.selectApphost(id);
  return Promise.resolve();
}

export function onApphosts(cb: (apphosts: AppHostInfo[]) => void): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<AppHostInfo[]>("deck://apphosts", cb);
    void listApphosts().then(cb);
    return unlisten;
  }
  if (isAotBackend()) {
    let cancelled = false;
    const unsubscribe = httpBackend.onApphosts((apphosts) => {
      void applyAotApphostIdentity(apphosts).then((identifiedApphosts) => {
        if (!cancelled) {
          cb(identifiedApphosts);
        }
      }).catch(() => undefined);
    }, nativeBackend.getConfig);
    return () => {
      cancelled = true;
      unsubscribe();
    };
  }
  if (isHttpBackend()) {
    return httpBackend.onApphosts(cb);
  }
  return mockBackend.onApphosts(cb);
}

// Subscribes to the active AppHost's open interactions (command inputs, message
// boxes, notifications). The full list is sent on every change; an empty list means
// there are no open interactions.
export function onInteractions(cb: (interactions: InteractionInfo[]) => void): Unsubscribe {
  if (isTauri()) {
    return bridgeListen<InteractionInfo[]>("deck://interactions", cb);
  }
  if (isHttpBackend()) {
    return httpBackend.onInteractions(cb);
  }
  return mockBackend.onInteractions(cb);
}

// Replies to one interaction on the active AppHost. `values` maps input names to
// string values (booleans as "true"/"false", choices as the option value).
export function respondInteraction(interactionId: number, action: string, values: Record<string, string>): void {
  if (isTauri()) {
    void invoke("deck_respond_interaction", { interactionId, action, values });
    return;
  }
  if (isHttpBackend()) {
    httpBackend.respondInteraction(interactionId, action, values);
    return;
  }
  mockBackend.respondInteraction(interactionId, action, values);
}

export function getTelemetrySummary(): Promise<TelemetrySummary> {
  if (isTauri()) {
    return invoke<TelemetrySummary>("deck_get_telemetry_summary");
  }
  if (isHttpBackend()) {
    return httpBackend.getTelemetrySummary();
  }
  return Promise.resolve(mockBackend.getTelemetrySummary());
}

export function clearStructuredLogs(resourceName: string | null): Promise<void> {
  if (isTauri()) {
    return invoke<void>("deck_clear_structured_logs", { resourceName });
  }
  if (isHttpBackend()) {
    return httpBackend.clearStructuredLogs(resourceName).then(async () => {
      if (isAotBackend() && await nativeBackend.hasCapability("structured-logs")) {
        await nativeBackend.refreshStructuredLogs();
      }
    });
  }
  mockBackend.clearStructuredLogs(resourceName);
  return Promise.resolve();
}

export function clearTraces(resourceName: string | null): Promise<void> {
  if (isTauri()) {
    return invoke<void>("deck_clear_traces", { resourceName });
  }
  if (isHttpBackend()) {
    return httpBackend.clearTraces(resourceName);
  }
  mockBackend.clearTraces(resourceName);
  return Promise.resolve();
}

export function clearMetrics(resourceName: string | null): Promise<void> {
  if (isTauri()) {
    return invoke<void>("deck_clear_metrics", { resourceName });
  }
  if (isHttpBackend()) {
    return httpBackend.clearMetrics(resourceName);
  }
  mockBackend.clearMetrics(resourceName);
  return Promise.resolve();
}

// Fetches the downsampled time series for a metric within a window. Returns null
// when the metric has no data yet.
export function getMetricSeries(query: MetricSeriesQuery): Promise<MetricSeriesResponse | null> {
  if (isTauri()) {
    return invoke<MetricSeriesResponse | null>("deck_get_metric_series", {
      name: query.name,
      resourceName: query.resourceName ?? null,
      windowSeconds: query.windowSeconds ?? null,
      maxPoints: query.maxPoints ?? null,
    });
  }
  if (isHttpBackend()) {
    return httpBackend.getMetricSeries(query);
  }
  return Promise.resolve(mockBackend.getMetricSeries(query));
}

export function executeCommand(args: ExecuteCommandArgs): Promise<CommandResponse> {
  if (isTauri()) {
    return invoke<CommandResponse>("deck_execute_command", { ...args });
  }
  if (isAotBackend()) {
    return nativeBackend.hasCapability("commands").then((supported) => (
      supported ? nativeBackend.executeCommand(args) : httpBackend.executeCommand(args)
    ));
  }
  if (isHttpBackend()) {
    return httpBackend.executeCommand(args);
  }
  return Promise.resolve(mockBackend.executeCommand(args));
}

export function subscribeConsoleLogs(
  resourceName: string,
  cb: (event: ConsoleLogEvent) => void,
): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<ConsoleLogEvent>("deck://console-log", (payload) => {
      if (payload.resourceName === resourceName) {
        cb(payload);
      }
    });
    void invoke("deck_subscribe_console_logs", { resourceName });
    return () => {
      void invoke("deck_unsubscribe_console_logs", { resourceName });
      unlisten();
    };
  }
  if (isHttpBackend()) {
    if (isAotBackend()) {
      let cancelled = false;
      let unsubscribe: Unsubscribe | null = null;
      void nativeBackend.hasCapability("console-logs-live").then((supported) => {
        if (cancelled) return;
        unsubscribe = supported
          ? nativeBackend.subscribeConsoleLogs(resourceName, cb)
          : httpBackend.subscribeConsoleLogs(resourceName, cb);
      }).catch(() => {
        if (!cancelled) unsubscribe = httpBackend.subscribeConsoleLogs(resourceName, cb);
      });
      return () => {
        cancelled = true;
        unsubscribe?.();
      };
    }
    return httpBackend.subscribeConsoleLogs(resourceName, cb);
  }
  return mockBackend.subscribeConsoleLogs(resourceName, cb);
}

export function onResources(cb: (event: ResourcesEvent) => void): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<ResourcesEvent>("deck://resources", cb);
    // Prime with the current snapshot; live deltas then arrive via the event.
    void listResources().then((resources) => cb({ type: "snapshot", resources }));
    return unlisten;
  }
  if (isHttpBackend()) {
    if (isAotBackend()) {
      let cancelled = false;
      let unsubscribe: Unsubscribe | null = null;
      void nativeBackend.hasCapability("resources-live").then((supported) => {
        if (cancelled) {
          return;
        }

        unsubscribe = supported
          ? nativeBackend.subscribeResources(
              cb,
              (status) => httpBackend.reportResourceConnection(status, nativeBackend.getConfig),
              (retry) => httpBackend.registerResourceRetry(retry),
            )
          : httpBackend.onResources(cb, listResources, nativeBackend.getConfig);
      }).catch((error: unknown) => {
        httpBackend.reportResourceConnection({
          target: "resourceService",
          state: "error",
          message: error instanceof Error ? error.message : String(error),
        }, nativeBackend.getConfig);
      });

      return () => {
        cancelled = true;
        unsubscribe?.();
      };
    }

    return httpBackend.onResources(
      cb,
    );
  }
  return mockBackend.onResources(cb);
}

export function onConnection(cb: (status: ConnectionStatus) => void): Unsubscribe {
  if (isTauri()) {
    return bridgeListen<ConnectionStatus>("deck://connection", cb);
  }
  if (isHttpBackend()) {
    return httpBackend.onConnection(cb);
  }
  return mockBackend.onConnection(cb);
}

export function onTelemetry(cb: (summary: TelemetrySummary) => void): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<TelemetrySummary>("deck://telemetry", cb);
    void getTelemetrySummary().then(cb);
    return unlisten;
  }
  if (isHttpBackend()) {
    if (isAotBackend()) {
      let cancelled = false;
      let unsubscribeLegacy: Unsubscribe | null = null;
      let unsubscribeStructuredLogs: Unsubscribe | null = null;
      let legacySummary: TelemetrySummary | null = null;
      let structuredLogs: Pick<TelemetrySummary, "logCount" | "recentLogs"> | null = null;
      const publish = (): void => {
        if (!cancelled && legacySummary !== null && structuredLogs !== null) {
          cb({ ...legacySummary, ...structuredLogs });
        }
      };

      void Promise.all([
        nativeBackend.hasCapability("structured-logs"),
        nativeBackend.hasCapability("structured-logs-live"),
      ]).then(([hasBacklog, hasLive]) => {
        if (cancelled) return;
        if (!hasBacklog || !hasLive) {
          unsubscribeLegacy = httpBackend.onTelemetry(cb);
          return;
        }

        unsubscribeLegacy = httpBackend.onTelemetry((summary) => {
          legacySummary = summary;
          publish();
        }, false);
        unsubscribeStructuredLogs = nativeBackend.subscribeStructuredLogs((logs) => {
          structuredLogs = logs;
          publish();
        });
      }).catch(() => {
        if (!cancelled) unsubscribeLegacy = httpBackend.onTelemetry(cb);
      });
      return () => {
        cancelled = true;
        unsubscribeLegacy?.();
        unsubscribeStructuredLogs?.();
      };
    }
    return httpBackend.onTelemetry(cb);
  }
  return mockBackend.onTelemetry(cb);
}

// Opens an external URL: dispatches to the Rust `deck_open_external` command
// when running inside Tauri, otherwise falls back to a new browser tab.
export async function openExternal(url: string): Promise<void> {
  if (isTauri()) {
    try {
      await invoke("deck_open_external", { url });
      return;
    } catch {
      // Command may be unavailable; fall through to window.open.
    }
  }
  window.open(url, "_blank", "noopener,noreferrer");
}
