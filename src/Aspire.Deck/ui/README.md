# Aspire Deck — UI

The web UI for **Aspire Deck**, a native desktop replacement for the Aspire Blazor
dashboard. It is a Vite + React 18 + TypeScript (strict) app that runs in two modes from
the **same** code:

- **Standalone (browser / preview)** — when no Tauri runtime is detected, the UI serves
  rich mock data from `src/api/mock.ts`. Resources, console logs, telemetry, and canvases
  all animate so the whole app is demonstrable without the Rust backend.
- **Tauri-embedded** — when running inside the Tauri shell, the data layer
  (`src/api/deck.ts`) calls the real backend via `invoke(...)` / `listen(...)` exactly as
  described in [`../CONTRACT.md`](../CONTRACT.md).

Mode detection is automatic (`"__TAURI_INTERNALS__" in window`), so callers never branch.

## Develop

```bash
npm install
npm run dev      # standalone dev server with mocks at http://localhost:1430
```

Open the dev URL in a normal browser. You get a fully interactive dashboard backed by the
mock backend: live resource state changes, streaming console logs, growing telemetry, and
an animated sample canvas.

## Build & preview

```bash
npm run build    # tsc -b && vite build -> dist/
npm run preview  # serve the production build locally
```

`vite.config.ts` sets `base: "./"` and `build.outDir: "dist"` so the bundle loads from
`file://` inside Tauri.

## Tauri integration

A `tauri.conf.json` in the Rust crate points at this UI's build output. Deck loads the embedded
build (no `devUrl`), so debug and release builds behave the same:

```json
{
  "build": {
    "frontendDist": "../ui/dist"
  }
}
```

External link opening uses `@tauri-apps/plugin-opener` when present and falls back to
`window.open` in the browser. If the Tauri opener plugin is enabled, register it in the
Rust app; otherwise links still open in a new tab.

## Structure

```
src/
  api/      types.ts (mirrors CONTRACT.md), deck.ts (dual-mode), mock.ts (standalone data)
  components/  Sidebar, TopBar, ConnectionPill, StateDot, DataTable, DetailsDrawer,
               SearchBox, Sparkline (uPlot), ConfirmDialog, EmptyState, Badge, Icons
  pages/    ResourcesPage, ConsolePage, StructuredLogsPage, TracesPage, MetricsPage,
            CanvasesPage
  lib/      format.ts (duration/time/bytes), useDeckEvent.ts (live hooks), theme.ts
  styles/   theme.css (design tokens + dark/light), global.css (components)
public/
  sample-canvas.html   bundled demo canvas used by the mock canvas manifest
```

## Conventions

- TypeScript strict; static imports only (no dynamic `import()`).
- Dark theme by default; light theme via the top-bar toggle (`[data-theme]` on `<html>`).
- Charts use `uplot` (canvas-based). The telemetry summary only exposes `lastValue`, so the
  Metrics page keeps a small client-side ring buffer per metric to animate the series.
