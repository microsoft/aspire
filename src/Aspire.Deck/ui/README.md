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

The UI is built on the reusable module in `src/toolkit`. The toolkit owns the Fluent React
provider, Fluent System Icons, and domain-neutral controls such as buttons, badges, search,
tables, state indicators, empty states, and confirmation dialogs. Deck pages import those
controls only through `src/toolkit/index.ts`; AppHost, resource, and telemetry behavior remains
in `src/api`, `src/components`, and `src/pages`.

## Develop

```bash
npm install
npm run dev      # standalone dev server with mocks at http://localhost:1430
```

Open the dev URL in a normal browser. You get a fully interactive dashboard backed by the
mock backend: live resource state changes, streaming console logs, growing telemetry, and
an animated sample canvas.

Open `http://localhost:1430/?view=toolkit` for the standalone toolkit playground. It exercises
the shared controls without depending on the Deck backend or mock data layer, making it the
starting point for new dashboard UI and visual regression coverage.

## Verify the toolkit

```bash
npm run test:e2e
```

The TypeScript Playwright suite starts Vite when needed and verifies the feature inventory in
`e2e/toolkit-features.ts`. Every inventory ID must be registered by a browser scenario or the
test module fails to load. The suite checks the reviewed YAML accessibility snapshot, desktop
and mobile containment, light and dark theme contrast, filtering, dialogs, drawers, and browser
errors. Passing runs attach desktop/mobile screenshots to the HTML report; failures retain a
screenshot, video, trace, and page context under `test-results`.

Use `npm run test:e2e:update` only after reviewing an intentional accessibility-tree change.

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
  toolkit/  Fluent provider and reusable, domain-neutral UI primitives
  components/  Deck shell and domain components such as Sidebar, TopBar, DetailsDrawer,
               InteractionPane, NotificationStack, and MetricChart
  pages/    ResourcesPage, ConsolePage, StructuredLogsPage, TracesPage, MetricsPage,
            CanvasesPage
  lib/      format.ts (duration/time/bytes), useDeckEvent.ts (live hooks), theme.ts
  styles/   theme.css (design tokens + dark/light), global.css (components)
public/
  sample-canvas.html   bundled demo canvas used by the mock canvas manifest
```

## Conventions

- TypeScript strict; static imports only (no dynamic `import()`).
- Domain-neutral UI is exported from `src/toolkit/index.ts`; feature code does not import
  toolkit implementation files directly.
- Toolkit controls use Fluent React components and Fluent System Icons while Deck CSS tokens
  define the product-specific color, spacing, and density.
- Dark theme by default; light theme via the top-bar toggle (`[data-theme]` on `<html>`).
- Charts use `uplot` (canvas-based). The telemetry summary only exposes `lastValue`, so the
  Metrics page keeps a small client-side ring buffer per metric to animate the series.
