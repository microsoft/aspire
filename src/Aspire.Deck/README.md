# Aspire Deck (preview)

**Aspire Deck** is a native (Tauri) desktop alternative to the Aspire Blazor dashboard. It is a
**drop-in replacement in terms of inter-process communication**: it hosts the same OTLP
ingestion endpoints, speaks the same resource-service gRPC protocol, and is configured by the
same environment variables as the dashboard. The difference is the UI — Deck renders a fast,
native, dependency-light experience instead of a browser app.

Deck ships **side-by-side** with the existing dashboard as a **preview feature**. The Blazor
dashboard is unchanged; Deck is additive. Deck deliberately does **not** carry over the
dashboard's Copilot/VS integration — its extensibility story is the **canvas** model instead
(see below).

> Preview: the `aspire deck` command is hidden behind a feature flag while the app matures.
> Enable it with `aspire config set features.deckCommandEnabled true`.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Aspire Deck (Tauri process)                                 │
│                                                             │
│  Rust backend (src-tauri/)                                  │
│   • OTLP ingestion servers  (gRPC + HTTP)  ◄── app telemetry │
│   • Resource-service client (gRPC)         ──► AppHost       │
│   • Canvas discovery + bridge                               │
│   • Tauri commands/events  ◄──────────────►  UI             │
│                                                             │
│  Web UI (ui/, React + TypeScript + Vite)                   │
│   • Resources, Console, Logs, Traces, Metrics, Canvases     │
└─────────────────────────────────────────────────────────────┘
```

- **No .NET runs in the Deck process.** The Rust backend implements the OTLP servers and the
  resource-service client directly (tonic + axum).
- The **Tauri ⇄ UI contract** (commands, events, payload shapes) is the single source of truth in
  [`CONTRACT.md`](./CONTRACT.md), mirrored in `ui/src/api/types.ts`.

### Directory layout

| Path | What |
| --- | --- |
| `src-tauri/` | Rust backend: OTLP servers, gRPC resource client, canvas loader, Tauri wiring. |
| `src-tauri/proto/` | Protobuf definitions (OTLP + Aspire resource service) compiled at build time. |
| `ui/` | React/TypeScript/Vite frontend. Builds to `ui/dist/`, which Tauri embeds. |
| `canvases/` | First-party sample canvases discovered at runtime. |
| `CONTRACT.md` | Authoritative Tauri ⇄ UI boundary. |

## IPC compatibility (same as the dashboard)

Deck reads the **same configuration** the dashboard does:

- **Resource service (client)** — gRPC `aspire.v1.DashboardService`
  - `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` (or `DOTNET_RESOURCE_SERVICE_ENDPOINT_URL`)
  - Auth: `DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE` + `…APIKEY` (header `x-resource-service-api-key`)
- **OTLP ingestion (server)** — gRPC `Trace/Metrics/Logs` + HTTP `/v1/{traces,metrics,logs}`
  - `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`, `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL`
  - Auth: `DASHBOARD__OTLP__AUTHMODE` + `…PRIMARYAPIKEY`/`…SECONDARYAPIKEY` (header `x-otlp-api-key`)

Because the configuration surface matches, an AppHost that points at Deck's endpoints behaves
exactly as it would with the dashboard.

## Running

### Via the CLI (preview)

```bash
# one-time: opt into the preview command
aspire config set features.deckCommandEnabled true

# run your app with Aspire Deck substituting the dashboard
aspire run --deck

# or launch Deck standalone (wire it up yourself / attach to a running resource service)
aspire deck
```

With `aspire run --deck` the AppHost runs in **external dashboard mode**: the built-in dashboard
process is not started, but the AppHost still hosts the resource service and exports OTLP telemetry
to Deck. The CLI picks the endpoints and wires both sides (unsecured loopback transport for local
dev). Deck hosts the OTLP endpoints and connects to the resource service — fully replacing the
dashboard for that run.

#### Multiple AppHosts + switching

Deck is a persistent app that **multiple AppHosts can attach to**. The first `aspire run --deck`
launches Deck; subsequent `aspire run --deck` invocations discover the running Deck (via its
instance file) and **attach** their AppHost to it instead of launching another. The Deck top bar
shows an **AppHost switcher** when more than one is attached, so you can switch which AppHost you're
viewing live.

- **Discovery**: Deck writes `<home>/.aspire/deck/instance.json` (control endpoint, shared OTLP
  endpoints, a registration token, and its PID). The CLI reads it, verifies the process is alive,
  and registers/unregisters AppHosts over the loopback control endpoint (token-gated).
- **Lifetime**: the run that launches Deck owns its lifetime (closing that run closes Deck).
  Attached runs only register on start and unregister on exit.
- **Telemetry note (preview)**: OTLP telemetry is currently shared across attached AppHosts (a
  single ingestion store). Resources, console logs, and commands are per-AppHost; per-AppHost
  telemetry isolation is a planned follow-up.

`aspire deck` options: `--otlp-grpc-url`, `--otlp-http-url`, `--resource-service-url`,
`--deck-path`. The binary is resolved from `--deck-path`, then `ASPIRE_DECK_PATH`, then a local
build under `src/Aspire.Deck/src-tauri/target/`.

#### External dashboard mode (`ASPIRE_DASHBOARD_EXTERNAL`)

`aspire run --deck` sets `ASPIRE_DASHBOARD_EXTERNAL=true` on the AppHost. This is a general
hosting capability: when set, the AppHost keeps hosting the resource service and configures
resources to export OTLP telemetry, but does **not** launch the built-in dashboard. Point the
OTLP endpoints (`ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` / `..._HTTP_ENDPOINT_URL`) and the resource
service (`ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL`) at the external dashboard.

### From source (development)

Prerequisites: a Rust toolchain, Node.js, and the Tauri prerequisites for your OS.

```bash
# build the UI (embedded by Tauri)
npm --prefix ui install
npm --prefix ui run build

# run the native app (Rust backend + embedded UI)
cargo tauri dev --manifest-path src-tauri/Cargo.toml      # or: cargo run --manifest-path src-tauri/Cargo.toml
```

The UI can also run standalone in a browser against a built-in **mock backend** (no AppHost
required) via `npm --prefix ui run dev` — useful for UI work and for previewing canvases.

## Canvas extensibility

Canvases are the Deck analogue of GitHub Copilot App "canvases": sandboxed HTML panels that
render a custom, live view of your app. They receive data from Deck over a small `postMessage`
bridge (`ready` handshake, then `resources`/`telemetry` events plus request/response methods).

- **Author one with the agent skill**: [`.agents/skills/deck-canvas/SKILL.md`](../../.agents/skills/deck-canvas/SKILL.md).
- **Reference sample**: [`canvases/resource-radar/`](./canvases/resource-radar/) — a complete
  canvas (`canvas.json` + `index.html`) that renders live resource health and telemetry counters.
- **Discovery**: Deck loads canvases from `ASPIRE_DECK_CANVASES_DIR`, the per-user
  `…/AspireDeck/canvases/` directory, and the project's `canvases/` directory.

## Relationship to the dashboard

Deck is a preview and does not replace the dashboard. The dashboard remains the supported,
full-featured experience; Deck explores a native UI and the canvas extensibility model. The two
share only the wire protocols and configuration, not code.
