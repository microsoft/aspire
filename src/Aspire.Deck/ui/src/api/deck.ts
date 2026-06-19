// Dual-mode data layer. When running inside Tauri it dispatches to the real
// Rust backend via `invoke`/`listen`. Otherwise it serves the in-process mock
// backend. Both paths expose the SAME function surface so callers are identical.
//
// Detection: Tauri injects `__TAURI_INTERNALS__` (v2) / `__TAURI__` onto window
// before the UI bundle runs.

import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { mockBackend } from "./mock";
import type {
  AppHostInfo,
  CanvasManifest,
  CommandResponse,
  ConnectionStatus,
  ConsoleLogEvent,
  DeckConfig,
  ExecuteCommandArgs,
  Resource,
  ResourcesEvent,
  TelemetrySummary,
} from "./types";

type Unsubscribe = () => void;

export function isTauri(): boolean {
  return typeof window !== "undefined" && ("__TAURI_INTERNALS__" in window || "__TAURI__" in window);
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
  return Promise.resolve(mockBackend.getConfig());
}

export function listResources(): Promise<Resource[]> {
  if (isTauri()) {
    return invoke<Resource[]>("deck_list_resources");
  }
  return Promise.resolve(mockBackend.listResources());
}

export function listCanvases(): Promise<CanvasManifest[]> {
  if (isTauri()) {
    return invoke<CanvasManifest[]>("deck_list_canvases");
  }
  return Promise.resolve(mockBackend.listCanvases());
}

export function listApphosts(): Promise<AppHostInfo[]> {
  if (isTauri()) {
    return invoke<AppHostInfo[]>("deck_list_apphosts");
  }
  return Promise.resolve(mockBackend.listApphosts());
}

export function selectApphost(id: string): Promise<void> {
  if (isTauri()) {
    return invoke<void>("deck_select_apphost", { id });
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
  return mockBackend.onApphosts(cb);
}

export function getTelemetrySummary(): Promise<TelemetrySummary> {
  if (isTauri()) {
    return invoke<TelemetrySummary>("deck_get_telemetry_summary");
  }
  return Promise.resolve(mockBackend.getTelemetrySummary());
}

export function executeCommand(args: ExecuteCommandArgs): Promise<CommandResponse> {
  if (isTauri()) {
    return invoke<CommandResponse>("deck_execute_command", { ...args });
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
  return mockBackend.subscribeConsoleLogs(resourceName, cb);
}

export function onResources(cb: (event: ResourcesEvent) => void): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<ResourcesEvent>("deck://resources", cb);
    // Prime with the current snapshot; live deltas then arrive via the event.
    void listResources().then((resources) => cb({ type: "snapshot", resources }));
    return unlisten;
  }
  return mockBackend.onResources(cb);
}

export function onConnection(cb: (status: ConnectionStatus) => void): Unsubscribe {
  if (isTauri()) {
    return bridgeListen<ConnectionStatus>("deck://connection", cb);
  }
  return mockBackend.onConnection(cb);
}

export function onTelemetry(cb: (summary: TelemetrySummary) => void): Unsubscribe {
  if (isTauri()) {
    const unlisten = bridgeListen<TelemetrySummary>("deck://telemetry", cb);
    void getTelemetrySummary().then(cb);
    return unlisten;
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
