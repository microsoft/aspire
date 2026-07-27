// Host side of the Aspire Deck canvas bridge.
//
// Canvases are sandboxed `<iframe>` panels (see CanvasesPage). They cannot call
// Tauri commands directly, so the host exposes a tiny `postMessage` protocol that
// lets a canvas request app data and subscribe to live updates. The exact wire
// format is documented for canvas authors in
// `.agents/skills/deck-canvas/SKILL.md` and demonstrated by the bundled sample
// canvas — keep all three in sync.
//
// Wire format (all messages carry `channel: "aspire-deck"`):
//   canvas -> host (request):  { channel, kind: "request", id, method, params? }
//   host  -> canvas (response):{ channel, kind: "response", id, ok, result?, error? }
//   host  -> canvas (event):   { channel, kind: "event", event, payload }
//
// Methods: "ready" (handshake; triggers initial snapshots), "getConfig",
//          "listResources", "getTelemetrySummary", "executeCommand".
// Events:  "resources" (ResourcesEvent), "telemetry" (TelemetrySummary).

import {
  executeCommand,
  getConfig,
  getTelemetrySummary,
  listResources,
  onResources,
  onTelemetry,
} from "../api/deck";
import type { ExecuteCommandArgs } from "../api/types";

const CHANNEL = "aspire-deck";

interface CanvasRequest {
  channel: typeof CHANNEL;
  kind: "request";
  id: string | number;
  method: string;
  params?: unknown;
}

function isCanvasRequest(value: unknown): value is CanvasRequest {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const msg = value as Record<string, unknown>;
  return msg.channel === CHANNEL && msg.kind === "request" && "id" in msg && typeof msg.method === "string";
}

/**
 * Attaches the host bridge to a canvas iframe. Returns a disposer that removes
 * the listener and tears down any data subscriptions. The iframe must already
 * be in the DOM; the canvas drives the handshake by sending a `ready` request.
 */
export function attachCanvasBridge(iframe: HTMLIFrameElement): () => void {
  const subscriptions: Array<() => void> = [];

  function post(message: Record<string, unknown>): void {
    // The sandboxed canvas has an opaque origin ("null"), so we cannot target a
    // specific origin here. The payload is non-sensitive app telemetry/state and
    // only this iframe receives it.
    iframe.contentWindow?.postMessage({ channel: CHANNEL, ...message }, "*");
  }

  function postEvent(event: string, payload: unknown): void {
    post({ kind: "event", event, payload });
  }

  function startStreaming(): void {
    // Idempotent: a canvas that re-sends `ready` (e.g. after its own reload)
    // should not accumulate duplicate subscriptions.
    if (subscriptions.length > 0) {
      return;
    }
    subscriptions.push(onResources((evt) => postEvent("resources", evt)));
    subscriptions.push(onTelemetry((summary) => postEvent("telemetry", summary)));
  }

  async function handle(method: string, params: unknown): Promise<unknown> {
    switch (method) {
      case "ready":
        startStreaming();
        return { ok: true };
      case "getConfig":
        return getConfig();
      case "listResources":
        return listResources();
      case "getTelemetrySummary":
        return getTelemetrySummary();
      case "executeCommand":
        return executeCommand(params as ExecuteCommandArgs);
      default:
        throw new Error(`Unknown canvas method: ${method}`);
    }
  }

  function onMessage(e: MessageEvent): void {
    // Only handle messages originating from this canvas's iframe.
    if (e.source !== iframe.contentWindow || !isCanvasRequest(e.data)) {
      return;
    }
    const { id, method, params } = e.data;
    void handle(method, params).then(
      (result) => post({ kind: "response", id, ok: true, result }),
      (error: unknown) => post({ kind: "response", id, ok: false, error: String(error) }),
    );
  }

  window.addEventListener("message", onMessage);

  return () => {
    window.removeEventListener("message", onMessage);
    for (const dispose of subscriptions) {
      dispose();
    }
    subscriptions.length = 0;
  };
}
