# @hex1b/web-terminal

The first-party GPU-rendered browser terminal for Hex1b. It renders server-authoritative
cells and graphics in a module worker, with local input routing, producer-backed
history and selection, clipboard actions, and primary/secondary view sizing.
There are no runtime package dependencies.

**Experimental and paired with Hex1b:** this client speaks the evolving HWT1
transport implemented by the matching Hex1b server. HWT1 and internal browser
modules are not a supported third-party protocol or renderer API. The bootstrap
npm version `0.1.0` must be paired with the server build from the **same
implementation commit**; it is not compatible merely by version number with the
historical Hex1b NuGet `0.1.0`. Subsequent normal CI releases coordinate npm and
NuGet versions; use matching builds from the same release.

## Install and mount

```sh
npm install @hex1b/web-terminal
```

Give the container a nonzero width and height. Mount resolves after a connected
frame has been presented, or rejects on initialization failure or a 30-second
first-frame timeout.

```html
<div id="terminal" style="width: 100%; height: 480px"></div>
```

```ts
import { WebTerminal } from "@hex1b/web-terminal";

const container = document.getElementById("terminal");
if (!container) throw new Error("Missing terminal container");

const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal", // Your matching Hex1b HWT1 WebSocket endpoint.
  sizing: { mode: "auto", fontSize: 16 },
  onStatus(message, level) {
    console.log(level, message);
  }
});

terminal.focus();
// On component teardown:
// terminal.dispose();
```

`url` accepts a string or URL; relative URLs resolve against the page, and
`http:`/`https:` become `ws:`/`wss:`. An optional `AbortSignal` cancels mounting
or disposes a mounted view. Disposal removes only the appended element and its
connection, not the container or server-side shared terminal.

### Browser and deployment requirements

Use a browser with WebGPU or WebGL2, module workers, transferable OffscreenCanvas,
worker animation frames, ResizeObserver, and CSS Font Loading. WebGPU requires
HTTPS or localhost; WebGL2 rendering also works on ordinary HTTP origins.
Clipboard API access still requires a secure context, browser permission and,
for relevant actions, a user gesture. There is no Canvas2D terminal-rendering fallback.
Renderer selection does not change transport security: use HTTPS/WSS to protect
terminal input and output.

### Renderer selection

Set the mount-time `renderer` option to `"auto"` (the default), `"webgpu"`, or
`"webgl2"`. Auto prefers WebGPU and uses WebGL2 if the secure context, API,
adapter, device acquisition, or presentation context is unavailable. Explicit
modes require that backend and report an error rather than falling back.
Shader, font, validation, and unexpected initialization errors are not
compatibility fallbacks. Runtime GPU/context loss terminates the view with an
error; it does not switch backends behind the caller's back.

```ts
const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal",
  renderer: "webgl2", // Use WebGL2 even if WebGPU is available.
  onStats(stats) {
    console.log(stats.renderer, stats.rendererFallbackReason);
  }
});
```

`stats.renderer` identifies the active backend after initialization.
`stats.rendererFallbackReason` explains an automatic fallback and is absent
for explicit selections and successful WebGPU initialization. Both backends
share glyph rasterization, frame preparation, clipping, and image ordering.
WebGPU preference is not a performance guarantee; compare representative
workloads on your target browsers and devices.

### Module and worker deployment

The package contains browser ES modules, not a single bundle. For bare static
hosting, copy **all of `dist/`**, preserving its directory structure, and import
`/web-terminal/index.js` from a module script. `dist/web-terminal.js` also remains
available for relative static imports. Package consumers should import only from
`@hex1b/web-terminal`; internal protocol/renderer modules are not public exports.

The worker is created with
`new Worker(new URL("./terminal-worker.js", import.meta.url), { type: "module" })`.
The default font is resolved relative to its module, not the host page.
Bundlers differ in whether they discover and rewrite assets inside dependencies;
this package does not claim universal or individually verified bundler support.
The reliable static deployment layout is the complete emitted tree described
above.

If your bundler does not handle the dependency's worker URL, explicitly provide
the entry point you deployed:

```ts
const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal",
  workerUrl: "/web-terminal/terminal-worker.js"
});
```

`workerUrl` accepts a nonempty string or URL and resolves relative strings against
the page, not the package module. It still creates a **module** worker. Deploy its
complete relative module tree, or provide a separately bundled worker entry from
the same package build. The override does not automatically copy fonts: retain
the default font asset URL or provide explicit `font.faces` URLs as shown below.
The browser's worker origin and CSP restrictions still apply.

Configure your server's JavaScript and WOFF2 MIME types and CSP to allow these
workers, fonts, and the intended WebSocket endpoint.

## Configuration and state

`WebTerminalOptions` includes:

| Option | Meaning |
| --- | --- |
| `workerUrl` | Optional module-worker entry; useful when worker assets are deployed separately. |
| `scale` | GPU backing scale `0.5`–`3`, or `"auto"` (default, bounded device pixel ratio). |
| `renderer` | `"auto"` (prefer WebGPU), `"webgpu"`, or `"webgl2"`; selected once per mount. |
| `font` | One family and optional downloadable font faces; see below. |
| `sizing` | `{ mode: "auto", fontSize?: number }` or `{ mode: "fixed", columns, rows, fontSize?: number }`. |
| `readOnly` | Disable application input while retaining history inspection and selection. |
| `label` | Accessible label for the terminal's hidden keyboard input. |
| `inputBindings`, `onInput`, `actions` | Per-view input policy and custom actions. |
| `onSelectionUI` | Synchronous, cancelable UI notification hook. |

Font size is an integer from 8–32, defaulting to 16. Import `MIN_FONT_SIZE` and
`MAX_FONT_SIZE` from `@hex1b/web-terminal` for sizing controls. Requested fixed grids allow
20–300 columns and 10–100 rows. The producer still owns actual grid geometry.
`resize()`, `setSizing()`, and automatic resize requests require primary
ownership. `requestPrimary()` explicitly requests ownership; inspect `peer` or
`onRoleChange` to observe the result.

The handle exposes `geometry`, `peer`, `connected`, `stats`, `screenText`,
`sizing`, `viewport`, `selection`, `inputBindings`, and `inputContext`.
Metrics start empty; check optional fields before using them. History may be
unavailable, and selection can be unavailable, none, pending, valid, or
invalidated. Narrow `viewport.available` and `selection.status` before using
their state-specific values. `screenText` reflects the presented viewport, not
an independently reconstructed ANSI buffer.

Callbacks include `onGeometry`, `onRoleChange`, `onSizingChange`, `onStats`,
`onViewportChange`, `onSelectionChange`, `onStatus`, and `onInputError`.

## Input and clipboard

Import `InputRoute`, `TerminalAction`, and `defaultInputBindings` to inspect and
customize routing. Defaults preserve browser shortcuts, forward terminal keys,
copy with Cmd+C or Ctrl+Shift+C, and use a local right-click to copy or paste
when application mouse capture does not own that gesture. IME composition and
paste are forwarded through the producer's mode-aware input encoder.

Overrides match first. Reuse a default binding's ID to replace it, or specify
`{ id: "clipboard.context-click", remove: true }` to remove that default.
`match`, `when`, and `onInput` must finish synchronously. Actions may be async.

```ts
import { InputRoute, TerminalAction, type WebTerminalOptions } from "@hex1b/web-terminal";

const options: WebTerminalOptions = {
  url: "/ws/terminal",
  inputBindings: [
    {
      id: "history.previous-page",
      match: input => input.type === "key" && input.key === "PageUp" && input.shift,
      action: TerminalAction.ScrollLines,
      args: -20
    },
    { id: "clipboard.context-click", remove: true }
  ],
  onInput(input) {
    if (input.type === "key" && input.meta) return InputRoute.Browser;
    return InputRoute.Continue;
  }
};
```

Named actions are `copySelection`, `pasteClipboard`, `copyOrPaste`,
`clearSelection`, `scrollToLive`, and `scrollLines`.
`terminal.runAction(TerminalAction.ScrollLines, -20)` shares the same
implementation as bindings and UI controls. Custom `actions` receive
`(context, args, input)`; their argument/result types are `unknown`, so custom
handlers validate their own data. Built-in action names cannot be overridden.

You can also call `scrollLines()`, `scrollToLive()`, `clearSelection()`,
`copySelection({ clear: true })`, `paste(text)`, or `pasteClipboard()` directly.
Copy uses authoritative producer selection text, not rendered cells. Clipboard
actions reject if selection/input/focus changes before their asynchronous work
can be applied safely. Errors are surfaced rather than silently reported as
successful copies or pastes.

### Hyperlinks

Hold Ctrl or Cmd and click an OSC 8 hyperlink to open its destination in a new
tab. Hovering shows the destination and activation hint; holding the modifier
also shows a pointer cursor. Links work in live output, scrollback, and read-only
views. Plain clicks and drags retain their existing selection/application
behavior, and explicit input-policy routes or actions take precedence.
Shift and Alt/Option continue to reserve selection gestures.

Only absolute `http:`, `https:`, and `mailto:` destinations are activated
(`mailto:` handling depends on the browser). New tabs use `noopener,noreferrer`.
Script, data, file, relative, and custom-scheme URLs are not activated.
Plain URL text is not automatically detected; the workload must emit OSC 8.

## Selection UI hooks

`onSelectionUI` receives a typed `SelectionUIEvent`, also dispatched as the
`selectionui` DOM event on `terminal.element`. Its frozen detail includes
selection, viewport, geometry, canvas size, connection/read-only state, and:

- `overlay`: a stable light-DOM host for custom UI; style your controls and set
  `pointer-events: auto` on interactive descendants.
- `rects`: selection rectangles in overlay-local CSS pixels.
- `runAction`: the same typed action API as the terminal handle.
- `signal`: cleanup lifetime, aborted on disposal.

Call `event.preventDefault()` **synchronously** to replace the default Copy
button. This does not remove selection highlights or transfer ownership of
selection/clipboard state. The callback must return `undefined`, not a Promise.
Events coalesce meaningful changes; `refreshSelectionUI()` re-notifies hosts
after external styling or policy changes. External DOM listeners may also
cancel the default UI.

Inspection UI inherits the embedding page's `--cp-*` theme tokens and otherwise
uses its own light/dark defaults. Shadow parts include `selection-highlights`,
`selection-highlight`, and `selection-copy-button`.

## Fonts and licenses

The default is the bundled **Cascadia Mono NF** variable WOFF2 font. The package
includes the unmodified font, SIL Open Font License, and provenance in
`dist/fonts/cascadia-mono-nf/`; no sample assets are required.

```ts
const font = {
  family: "My Terminal Font",
  faces: [{ url: "/fonts/my-terminal.woff2", weight: "100 900", style: "normal" }]
};
```

Custom face URLs resolve against the host page before the configuration reaches
the worker. Page-loaded fonts are not inherited by workers: supply face URLs,
a locally installed family, or a generic family such as `monospace`. A generic
family cannot have downloadable faces. Font loading failures reject rather
than silently selecting a different font.

Hex1b's code is MIT licensed (`LICENSE`); the bundled font uses its separate
SIL Open Font License.

## Build, test, and pack

From this package directory, with Node.js 22 or later:

```sh
npm ci
npm run build
npm test
npm pack
```

The strict TypeScript build emits JavaScript, declarations, declaration maps,
and source maps (with embedded sources), then copies fonts into `dist/`.
`npm test` runs zero-dependency `node:test` tests against those emitted modules
and strict public-consumer declaration checks in NodeNext and bundler modes.
`npm run typecheck` validates sources without emitting.

`prepack` rebuilds for `npm pack` and manual `npm publish` from this directory.
Only `dist/`, this README, the MIT license, and package metadata are shipped.
A prepared tarball is self-contained and can be published with
`npm publish ./hex1b-web-terminal-<version>.tgz --ignore-scripts`; it does not
need development sources or build scripts. The package name is always
`@hex1b/web-terminal`. CI publishes main/release builds to npmjs; PR builds
provide the tarball as the `npm-web-terminal` workflow artifact and do not
publish it to a registry. No registry is pinned in `package.json`.
