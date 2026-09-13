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

### Connection closure and workload completion

Use `onClose(details)` to observe the browser's actual WebSocket close event.
`TerminalCloseDetails` contains readonly `code`, `reason`, and `wasClean` fields.
The callback runs once with the view already disconnected, **even if the socket
closes before the first HWT frame or authoritative HMP peer state**. A pending
mount rejects after the callback, so capture any host state before calling mount.
The details object is frozen. Reasons are untrusted text; do not render them as HTML.

```ts
import { WebTerminal, type TerminalCloseDetails } from "@hex1b/web-terminal";

const container = document.getElementById("terminal");
if (!container) throw new Error("Missing terminal container");
let closed: TerminalCloseDetails | undefined;
try {
  const terminal = await WebTerminal.mount(container, {
    url: "/ws/terminal",
    onClose(details) {
      closed = details;
      console.log("View closed", details.code, details.reason, details.wasClean);
    }
  });
  terminal.focus();
} catch (error) {
  // closed is set for a transport close, but not for local initialization failure.
  console.error("Mount failed", closed, error);
}
```

Transport loss is **not workload completion**. Code 1006 means the browser did not
receive a close frame; even code 1000 and `wasClean: true` only describe transport
closure, not successful process exit. Hosts can define an application close-code
contract and send it when their authoritative producer reports completion,
including before any HWT frame exists. Hex1b does not assign workload meaning to
close codes or reason strings. HTTP upgrade failures generally surface as 1006;
browsers do not expose the rejected HTTP response body or status through this API.

The client never retries automatically. The host decides whether to mount a new
view after transport loss or leave an ended tab/dialog visible. Abort, explicit
disposal, mount timeout, and local initialization/renderer failures do not
synthesize `onClose`; no callback runs after disposal. Callback exceptions are
reported to the host, not swallowed or retried, and do not leave mounting pending.
The API does not retain the producer after exit or promise a final rendered frame.

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
| `readOnly` | Initial per-view input policy; change it later with `setReadOnly(boolean)`. |
| `label` | Accessible label for the terminal's hidden keyboard input. |
| `onTitleChange` | Initial authoritative workload title, then distinct presented changes; see below. |
| `onClose` | Native WebSocket close details, including pre-mount transport failure; not workload completion. |
| `onProgressChange`, `onShellIntegrationChange` | Initial authoritative activity, then distinct presented changes for host-owned chrome. |
| `onWorkingDirectoryChange`, `onCommandMarkChange` | Initial authoritative OSC 7 directory and latest OSC 133 marker, then distinct presented changes; see below. |
| `inputBindings`, `onInput`, `actions` | Per-view input policy and custom actions. |
| `onSelectionUI` | Synchronous, cancelable UI notification hook. |

Font size is an integer from 8–32, defaulting to 16. Import `MIN_FONT_SIZE` and
`MAX_FONT_SIZE` from `@hex1b/web-terminal` for sizing controls. Requested fixed grids allow
20–300 columns and 10–100 rows. The producer still owns actual grid geometry.
`resize()`, `setSizing()`, and automatic resize requests require primary
ownership. `requestPrimary()` explicitly requests ownership; inspect `peer` or
`onRoleChange` to observe the result.

The handle exposes `geometry`, `peer`, `connected`, `readOnly`, `title`, `progress`, `shellIntegration`,
`workingDirectory`, `commandMark`, `stats`, `screenText`,
`sizing`, `viewport`, `selection`, `inputBindings`, and `inputContext`.
Metrics start empty; check optional fields before using them. History may be
unavailable, and selection can be unavailable, none, pending, valid, or
invalidated. Narrow `viewport.available` and `selection.status` before using
their state-specific values. `screenText` reflects the presented viewport, not
an independently reconstructed ANSI buffer.

Callbacks include `onGeometry`, `onRoleChange`, `onTitleChange`, `onSizingChange`, `onStats`,
`onProgressChange`, `onShellIntegrationChange`, `onWorkingDirectoryChange`, `onCommandMarkChange`,
`onViewportChange`, `onSelectionChange`, `onStatus`, and `onInputError`.

### Live read-only views

Call `terminal.setReadOnly(true)` to disable application input on an already
mounted view. `terminal.readOnly`, `inputContext.readOnly`, and selection UI
notifications reflect the new policy. Use `setReadOnly(false)` to re-enable input;
neither call remounts, reconnects, releases the peer's primary role, or changes
the server's current grid. A writable primary resumes automatic sizing requests.
Mutating the original `options.readOnly` after mount has no effect.

Read-only blocks keyboard/text/IME input, application mouse reports, direct
`paste()`/`pasteClipboard()`, the paste paths of `runAction()`, `resize()`,
`setSizing()`, `requestPrimary()`, and automatic resize requests. Explicit input
methods throw when disabled; DOM application input is not forwarded. Routing
overrides cannot bypass this policy. Custom actions can still run local operations,
but any terminal input method they call remains gated.

Active pointer capture and queued mouse movement, pending composition, queued
resize, and pending clipboard pastes are cancelled on policy change. Quickly
re-enabling input does not revive a previously pending paste. Commands already
dispatched cannot be recalled. Output, local selection gestures, history
navigation, resync, and copying remain available, including a copy already in
progress. UI notifications follow their usual coalescing rules.

```ts
import { WebTerminal } from "@hex1b/web-terminal";

const container = document.getElementById("terminal");
const inputEnabled = document.querySelector<HTMLInputElement>("#input-enabled");
if (!container || !inputEnabled) throw new Error("Missing terminal controls");

const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal",
  readOnly: !inputEnabled.checked
});
inputEnabled.addEventListener("change", () => terminal.setReadOnly(!inputEnabled.checked));
```

**Client policy is not authorization.** Enforce it independently on each server
view with `Hwt1PresentationAdapter.IsReadOnly`, initially or at runtime. For
example, in the host's existing connection setup (C# snippet):

```csharp
var presentation = new Hwt1PresentationAdapter { IsReadOnly = true };
// Attach this presentation to the view's terminal and drive its existing transport loops.
// Only trusted host policy should grant writes:
presentation.IsReadOnly = false;
```

The adapter ignores producer-mutating browser input, resize, and primary requests
while read-only, but still processes acknowledgements, resync, history, selection,
and copy. This is **per presentation**, not a producer-wide input lock: direct
terminal automation and other authorized viewers continue. A policy change does
not retract a command the adapter already accepted. The host must update both its
server policy and browser UX; client changes do not authorize themselves, and the
server property does not automatically change client UI.

### Workload titles

The read-only `terminal.title` is the current presented workload title. An empty
string means unset or explicitly cleared; choose your own fallback. The optional
`onTitleChange(title)` callback runs once with the first authoritative presented
value, **including `""`, before mount resolves**. The getter is updated before
the callback. Later notifications report only distinct presented values. Identical
updates, same-title resyncs, cursor blinking, and statistics do not notify again.
Intermediate workload changes can coalesce; this is not an event for every OSC
sequence.

```ts
import { WebTerminal } from "@hex1b/web-terminal";

const container = document.getElementById("terminal");
const header = document.getElementById("terminal-header");
if (!container || !header) throw new Error("Missing terminal elements");
const resourceName = "Build service";

const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal",
  onTitleChange(title) {
    header.textContent = title || resourceName;
  }
});
console.log(terminal.title);
```

The callback uses elements and fallback text captured **before** mounting, not
the still-pending `terminal` result. The component does not change `document.title`,
your header, or the input's accessible `label` automatically. There is no title
subscription method or DOM title event.

Titles are normalized by the core to at most 4,096 UTF-16 code units, with C0,
DEL, and C1 controls removed, malformed surrogates replaced with U+FFFD, and
truncation at a Unicode scalar boundary. They remain **untrusted text**: markup
and bidi characters are preserved. Use `textContent`, not `innerHTML`; apply your
own presentation and bidi policies.

OSC 0 and OSC 2 set or explicitly clear the window title; OSC 1 is icon-only.
Use `ESC ] 0 ; text BEL` or `ESC ] 2 ; text BEL`; `ESC \` (ST) may replace BEL.
The UTF-8 input path also accepts Unicode C1 OSC (`U+009D`) and ST (`U+009C`).
Semicolons within `text` are literal. Existing OSC 22/23 saved-title extensions
update the same state, including after a late HMP1 attachment.
RIS, soft reset, screen clearing, and buffer switching preserve the title and
existing saved-title behavior. History inspection retains the current workload
title rather than a title associated with an old row.

Disconnect and disposal retain the last known title without a synthetic clear.
Disposal (including abort) stops title callbacks. Attach a new view to reconnect;
it receives its own initial current title. Preliminary disconnected relay frames
do not trigger the initial notification. Callbacks run directly like the other
state callbacks; thrown host errors are not swallowed or retried.

The required title field needs the matching server build. Missing or malformed
title metadata fails the connection; an older server is not silently treated as
an empty title.
The per-title bound is not a limit on all parser buffering or saved-stack depth.
An HMP1 snapshot with too much saved title state fails its 16 MiB replay limit
rather than silently discarding saved titles.

### Application progress and shell activity

`terminal.progress` exposes OSC 9;4 state as `{ state, percentage }`.
The states are `"none"`, `"normal"`, `"error"`, `"indeterminate"`, and `"warning"`.
Normal/error/warning percentages are integers from 0 through 100. None and
indeterminate have `percentage: null`; none means the host should hide its indicator.
This is application-reported progress, not inferred from output or CPU activity.
It is independent of `ProgressWidget`, which draws inside terminal cells.

`terminal.shellIntegration` exposes OSC 133 as `{ phase, lastExitCode }`.
The phases are `"unknown"`, `"prompt"` (A), `"commandLine"` (B),
`"executing"` (C), and `"finished"` (D). B means input after the prompt, **not**
command execution. Unknown does not mean idle. `lastExitCode` is a signed
32-bit integer, or null when no status was reported; null is not success.
A/B/C preserve the last reported result, and D replaces it, including clearing
it to null when the shell omits its status. No command text, history, or output
locations are retained by these APIs.

`terminal.workingDirectory` exposes OSC 7 state as `{ uri, host, path }`, all
`null` until the first report. `uri` is the raw reported `file://` URI; `host`
and `path` are derived from it (`host` is `""` for a local/unqualified
authority). A malformed or non-`file` URI leaves the previous value unchanged.

`terminal.commandMark` exposes the single most-recently-reported OSC 133 marker
as `{ phase, exitCode, rawParameters } | null` — `null` until the first marker.
`phase` uses the same enum as `shellIntegration.phase`. `exitCode` is non-null
only on a `finished` (D) marker. `rawParameters` is the verbatim
`key=value[;key=value...]` text trailing the marker (for example a
`cmdline_url` extension on marker C), or `null` when none was present; use the
exported `parseCommandMarkParameters(rawParameters)` helper to parse it into a
`Map`, or `getCmdlineUrl(mark)` as a shortcut for the `cmdline_url` entry. This
is **not** a command-mark history — only the latest marker is exposed, mirroring
`shellIntegration`. A host that wants its own history should accumulate
distinct values from `onCommandMarkChange` itself.

All four getters return defensive copies. Their callbacks receive the first
authoritative presented state before mount resolves, then distinct presented
changes. All four getters are updated before their corresponding activity
callback. Callbacks use the same direct, synchronous host-callback convention
as title changes; host exceptions are not swallowed or retried.

Frames coalesce: the browser might see only Finished for a fast command, or
miss an entire command whose final state is unchanged. These callbacks are
**current-state notifications, not a lossless start/finish event stream**.
Resync/replay never invent commands, unchanged state does not notify again,
and a new mount receives its own baseline.

This example creates optional chrome outside the terminal:

```ts
import { WebTerminal, getCmdlineUrl } from "@hex1b/web-terminal";

const status = document.createElement("span");
const cwd = document.createElement("span");
const progress = document.createElement("progress");
progress.max = 100;
progress.hidden = true;
const container = document.createElement("div");
container.style.cssText = "width:800px;height:480px";
document.body.append(status, cwd, progress, container);

const terminal = await WebTerminal.mount(container, {
  url: "/ws/terminal",
  onProgressChange(value) {
    progress.hidden = value.state === "none";
    progress.dataset.state = value.state; // Host CSS can distinguish error/warning.
    if (value.percentage === null) progress.removeAttribute("value");
    else progress.value = value.percentage;
  },
  onShellIntegrationChange(value) {
    status.textContent = value.phase +
      (value.lastExitCode === null ? "" : ` (last exit ${value.lastExitCode})`);
  },
  onWorkingDirectoryChange(value) {
    cwd.textContent = value.path ?? "";
  },
  onCommandMarkChange(value) {
    const cmdlineUrl = getCmdlineUrl(value);
    if (cmdlineUrl) console.log("Command link:", cmdlineUrl);
  },
  onStats(stats) {
    if (!stats.connected) {
      progress.hidden = true;
      status.textContent = "Disconnected";
    }
  }
});
console.log(terminal.progress, terminal.shellIntegration, terminal.workingDirectory, terminal.commandMark);
```

No title, document chrome, or progress UI is changed automatically by the
component. The sample endpoint must be supplied by your application.
Callbacks can run before the `terminal` variable is assigned; use their
arguments during initial mounting.

RIS resets progress to None and shell integration to Unknown. Soft reset,
screen clearing, resize, and buffer switches preserve them. OSC 9;4 state 0
clears only progress; a shell completion does not implicitly clear it.
Disconnect, process exit, and disposal retain the last reported values
without inventing a completion or progress clear. Check `connected` before
showing active chrome, and remount to reconnect. Disposal stops callbacks.
Snapshots and historical viewports carry current activity, not activity at
the time a particular row was printed.

The core accepts BEL, ESC-backslash ST, and decoded Unicode C1 terminators
through its UTF-8 input path. Determinate progress requires unsigned decimal
0-100; clear/indeterminate allow an omitted percentage and ignore its optional
value. OSC 133 supports the basic A/B/C forms and D with an optional signed
decimal exit status (an empty field also means no status). Missing required,
malformed, overflowed, excess, or unsupported arguments do not change state.
The raw-output presentation path still forwards the original sequences to
supporting outer terminals. Required activity metadata needs the matching
server build; invalid/missing wire fields fail the connection, not silently
fall back to default state.

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
