---
name: deck-canvas
description: Authors an Aspire Deck canvas — a sandboxed HTML panel that renders a custom, live view of a running Aspire app (resources + OTLP telemetry) inside Aspire Deck. Use when asked to create, scaffold, or modify a Deck canvas, build a custom Deck panel/dashboard, or visualize app data in Aspire Deck.
---

You author **Aspire Deck canvases**. A canvas is a self-contained, sandboxed HTML panel
that Aspire Deck renders in an `<iframe>` and can drive with live data from the running
Aspire application. This is the Deck equivalent of a GitHub Copilot App "canvas": a custom
UI surface that an agent can generate on demand.

Aspire Deck is the native (Tauri) preview alternative to the Aspire dashboard. It ingests the
same OTLP telemetry and speaks the same resource-service protocol, then exposes that data to
canvases over a small, well-defined `postMessage` bridge.

## What you produce

A canvas is a directory containing:

```
<canvas-id>/
  canvas.json      # manifest (required)
  index.html       # entry point (required; name set by manifest "entry")
  …                # any additional local assets the entry references
```

### `canvas.json` (manifest)

```json
{
  "id": "resource-radar",
  "title": "Resource Radar",
  "description": "Live resource health and telemetry counters.",
  "icon": "📡",
  "entry": "index.html"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Stable, unique, kebab-case. First-writer-wins across canvas directories. |
| `title` | yes | Shown on the card and panel header. |
| `description` | no | One line shown under the title. |
| `icon` | no | An emoji or short label. Defaults to 🧩. |
| `entry` | no | Relative path to the HTML entry. Defaults to `index.html`. |

## Where canvases live (discovery)

Deck discovers canvases from these locations (first match by `id` wins):

1. Any directory listed in `ASPIRE_DECK_CANVASES_DIR` (`:`- or `;`-separated).
2. `<user-data>/AspireDeck/canvases/` (per-user install location).
3. `canvases/` next to the Deck executable, and the Deck project's `canvases/` directory
   during local development (e.g. `src/Aspire.Deck/canvases/`).

Each canvas is its own subdirectory containing a `canvas.json`. For repo-local samples, add a
new folder under `src/Aspire.Deck/canvases/`. For ad-hoc/user canvases, drop the folder under
the user-data location or point `ASPIRE_DECK_CANVASES_DIR` at it.

## The canvas bridge (how a canvas gets live data)

Canvases run in a sandboxed iframe and **cannot** call Tauri commands directly. Instead the
Deck host exposes a `postMessage` bridge. Every message carries `channel: "aspire-deck"`.

- **Request** (canvas → host): `{ channel, kind: "request", id, method, params? }`
- **Response** (host → canvas): `{ channel, kind: "response", id, ok, result?, error? }`
- **Event** (host → canvas): `{ channel, kind: "event", event, payload }`

### Methods (request `method`)

| Method | Params | Result |
| --- | --- | --- |
| `ready` | – | Handshake. Call this first; it tells the host to start streaming `resources` and `telemetry` events. |
| `getConfig` | – | `DeckConfig` (`applicationName`, endpoint URLs, `version`). |
| `listResources` | – | `Resource[]` — current snapshot. |
| `getTelemetrySummary` | – | `TelemetrySummary` (`logCount`, `spanCount`, `metricCount`, `recentLogs`, `recentSpans`, `metrics`). |
| `executeCommand` | `{ resourceName, resourceType, commandName }` | `CommandResponse` (`kind`, `message`). |

### Events (host → canvas, after `ready`)

| Event | Payload | Notes |
| --- | --- | --- |
| `resources` | `ResourcesEvent` | `{ type: "snapshot", resources }` or `{ type: "change", upserts?, deletes? }`. |
| `telemetry` | `TelemetrySummary` | Debounced push whenever new OTLP data is ingested. |

The full type definitions are the single source of truth in
[`src/Aspire.Deck/CONTRACT.md`](../../../src/Aspire.Deck/CONTRACT.md) and mirrored in
`src/Aspire.Deck/ui/src/api/types.ts`. Read them before relying on a field.

## Minimal bridge client (copy into a canvas)

```html
<script>
  const CHANNEL = "aspire-deck";
  let nextId = 1;
  const pending = new Map();
  const handlers = {};

  function request(method, params) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      parent.postMessage({ channel: CHANNEL, kind: "request", id, method, params }, "*");
    });
  }
  function on(event, cb) { handlers[event] = cb; }

  window.addEventListener("message", (e) => {
    const msg = e.data;
    if (!msg || msg.channel !== CHANNEL) return;
    if (msg.kind === "response") {
      const p = pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      msg.ok ? p.resolve(msg.result) : p.reject(new Error(msg.error));
    } else if (msg.kind === "event") {
      handlers[msg.event]?.(msg.payload);
    }
  });

  on("resources", (evt) => { /* update your view */ });
  on("telemetry", (summary) => { /* update your counters */ });

  (async () => {
    await request("ready");                       // start the event stream
    const cfg = await request("getConfig");
    const resources = await request("listResources");
    const telemetry = await request("getTelemetrySummary");
    // …render…
  })();
</script>
```

## Authoring rules

- **Self-contained.** The entry must work from a `file://` URL. Inline your CSS/JS or include
  only local, relative assets. Do **not** fetch remote scripts/styles — the canvas runs with
  `sandbox="allow-scripts allow-same-origin"` and offline-friendly first-party assets only.
- **No build step required.** Plain HTML/CSS/JS is preferred so a canvas is a single droppable
  folder. If you must bundle, commit the built output into the canvas folder.
- **Degrade gracefully.** When opened outside a Deck host (no bridge responds), show a friendly
  empty state instead of hanging. Time out or catch rejected requests.
- **Always handshake.** Call `request("ready")` before expecting `resources`/`telemetry` events.
- **Treat data as read-mostly.** Only `executeCommand` mutates app state; gate it behind explicit
  user action and honor any `confirmationMessage` on the command.
- **Match the look.** Deck uses a dark theme with an Aspire purple accent (`#9f7aea`/`#b794f6`).
  Keep canvases visually consistent unless the user asks otherwise.

## Reference sample

A complete, working example lives at
[`src/Aspire.Deck/canvases/resource-radar/`](../../../src/Aspire.Deck/canvases/resource-radar/)
(`canvas.json` + `index.html`). It implements the bridge client above and renders live resource
cards plus telemetry counters. Start from it when authoring a new canvas.

## Checklist before finishing

1. `canvas.json` has a unique kebab-case `id` and points `entry` at an existing file.
2. The entry implements the `ready` handshake and handles `resources`/`telemetry` events.
3. It renders a graceful empty state when no host is connected.
4. All assets are local/relative; nothing is fetched from the network.
5. The folder is placed somewhere Deck discovers (repo `src/Aspire.Deck/canvases/`,
   the user-data canvases dir, or a path in `ASPIRE_DECK_CANVASES_DIR`).
