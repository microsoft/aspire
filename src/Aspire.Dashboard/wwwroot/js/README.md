# JavaScript Libraries

The Aspire Dashboard bundles a few JavaScript libraries.

## IMask

`imask-7.6.1.min.js` is the browser build from the `imask@7.6.1` npm package. It is loaded before Blazor because Fluent UI Blazor's `FluentNumberInput` checks for the global `IMask` object and otherwise attempts to load the library from a CDN. The dashboard bundles it locally so number inputs work offline and with the dashboard's `script-src 'self'` content security policy.

## Plotly

The default Plotly JS library is around 4MB in size (minified), as it supports many different chart types. Currently, we only use simple chart types, so can use the `basic` distribution which is around 1MB instead.

From [Plotly JS's docs](https://github.com/plotly/plotly.js/blob/22efc2fb76f4c890a2c33448e6f1485ecab77f26/dist/README.md#plotlyjs-basic):

> The `basic` partial bundle contains trace modules `bar`, `pie` and `scatter`.

If we ever want to show more chart types than those, we'll need to change the bundle we use.

## Hex1b web terminal

`hex1b-web-terminal/` vendors the published `@hex1b/web-terminal` **0.169.0** release,
paired with the Hex1b, Hex1b.McpServer, and Hex1b.Tool NuGet packages and the
repository-local `hex1b` tool at the same version. The client and server use the evolving
HWT1 presentation transport and must be updated together. Do not substitute a
different client based only on a similar version number.

From `src/Aspire.Dashboard`, use Node.js 22 or later to acquire and update assets:

```shell
npm ci --ignore-scripts
npm run update-terminal-assets
npm test
```

`update-terminal-assets` first requires the installed distribution to contain
exactly one JavaScript file, `dist/index.js`, before replacing checked-in assets.
It minifies the bundle and copies the runtime assets, then verifies that every emitted
font/license file matches the installed package byte-for-byte, that the minified
bundle matches reproducible Terser output, and that no stale files remain.
To rerun that acquisition-only check
without copying, use `npm run verify-terminal-assets` after `npm ci`. This check
intentionally requires `node_modules`; ordinary regression tests do not.

Review and commit the manifest, lockfile, and generated asset changes together.
This is a manual acquisition step: ordinary .NET builds use the checked-in files
and do not run npm or download frontend packages.

The published package contains one unminified runtime bundle. The acquisition
script minifies it with the pinned Terser dependency in module mode, producing
`dist/index.min.js` with the public API and both workers. It does not rebuild
upstream source or split the workers. Only **four files** are vendored: this
minified bundle, the package's MIT license, the unmodified Cascadia Mono NF
WOFF2 font and its SIL Open Font License (including copyright notice).
Declarations, maps, package metadata and package/font READMEs are not deployed.
Version provenance remains in the bundle header and the Dashboard's manifest
and lockfile.

The font comes from Microsoft's [Cascadia Code v2407.24 release](https://github.com/microsoft/cascadia-code/releases/tag/v2407.24),
path `woff2/CascadiaMonoNF.woff2`. Its relative path under
`dist/fonts/cascadia-mono-nf/` is preserved. Do not hand-edit generated assets.
`TerminalView.razor.js` imports only the minified public entry point.

The temporary `nuget-hex1b` source in the repository's `NuGet.config` maps only
`Hex1b`, `Hex1b.McpServer`, and `Hex1b.Tool` to nuget.org while these versions await
mirroring. Other packages continue to use the existing feeds.

The terminal uses `renderer: "auto"`: WebGPU is preferred, with the package's
WebGL2 compatibility backend used when WebGPU's secure context, API, adapter,
device acquisition, or presentation context is unavailable. Shader, font,
validation and unexpected initialization failures remain errors; runtime GPU
loss ends that view rather than switching renderers. `stats.renderer` and
`stats.rendererFallbackReason` expose the selection for diagnostics. See
[the renderer PR](https://github.com/mitchdenny/hex1b/pull/491).

WebGPU requires HTTPS or localhost; WebGL2 rendering also works on ordinary
HTTP. Clipboard API permissions still require a secure context, and HTTPS/WSS
is needed to protect terminal traffic. Renderer selection does not relax the
dashboard's transport, authentication or origin protections.

Both backends require module workers, transferable OffscreenCanvas, worker
animation frames, ResizeObserver, and CSS Font Loading. There is no Canvas2D
or xterm renderer fallback. Serve JavaScript
and WOFF2 with their correct MIME types and allow same-origin workers, fonts,
and `/api/terminal` and `/api/apphost-terminal` WebSockets in the deployment CSP. The dashboard displays a
localized error if mounting fails.

The component import and socket endpoint resolve beneath `NavigationManager.BaseUri`.
Both module workers load that same `dist/index.min.js` URL using the fragments
`#hex1b-terminal-worker` and `#hex1b-link-detection-worker`; the path and query
string are preserved. The bundled font resolves relative to the module, so
deployments under a PathBase retain the prefix throughout the asset tree.
No blob worker, eval, CDN, or cross-origin font permission is
needed. The dashboard's existing `script-src 'self'` also allows same-origin
workers through the CSP worker-source fallback; its production
`default-src 'self'` covers the font and same-origin connections.

### Terminal metadata

Workload-reported titles (OSC 0/2), working directories (OSC 7) and progress
(OSC 9;4) flow through the public client callbacks to the Dashboard title bar.
The resource view, active dock pane, detached window and interaction dialog
share the same title/directory/progress presentation. Titles and directory URIs
are treated as untrusted text, not HTML or navigable links. The decoded directory
is displayed at the right of the title bar as a copy button. Clicking anywhere
on the path copies the full value using the Dashboard's shared client-side
clipboard handler; the copy icon appears on hover or keyboard focus without
changing the layout.
Titles use the same borderless, hover-icon copy interaction, including the fallback
resource name when no workload title is present. Both buttons copy the full text.
Long paths omit whole middle segments to retain leading and trailing context, while
the clipboard retains the full decoded path. Cleared titles fall
back to the surface's original name. Progress supports determinate,
indeterminate, error and warning states, and is hidden when disconnected.
It appears before the title, reserving a stable percentage width only for
determinate states. Error and warning labels appear in the progress tooltip
and accessible name rather than as inline text.
These values require the application or shell to emit the corresponding OSC
sequences; the Dashboard does not infer them from output.

### Scrollbar and retained command marks

The pinned client has no public option for suppressing its legacy "rows above
live" status and "Return to live" button. The adapter installs a small
shadow-DOM style/observer shim for those two elements only, leaving errors and
selection feedback intact. The observer is disconnected on reconnect/disposal.
Remove the shim when the upstream client offers a history-chrome option.
The overlay scrollbar and keyboard navigation still provide history navigation.

The terminal uses Hex1b's default Canvas2D **overlay** scrollbar, not a native
HTML scrollbar or a reserved gutter. The mount requests 3 CSS pixels of internal
padding on every side. `createDefaultScrollbarRenderer` keeps the
upstream capsule thumb, marker drawing, gestures, hit testing and auto-hide.
The painter adapter suppresses its additional canvas focus ring after pointer
release. A scoped shadow-DOM override also hides the track's DOM focus outline
after pointer input, restoring the upstream `:focus-visible` outline on keyboard
input without changing actual focus. The modality listeners are removed on disposal.
The track uses the terminal frame's background at 35% opacity. The thumb uses
the light foreground token from the terminal's dark theme scope, rather than
the Dashboard page's foreground, so it remains contrasting in either page theme.
Markers use the Dashboard's brand foreground and error tokens at 65% opacity,
restoring full opacity for increased contrast and forced colors. The track remains
translucent and dark in the light Dashboard theme; terminal cell colors are not changed.

Marker and tooltip colors are resolved outside the terminal's dark theme scope. Dashboard theme
changes and the `forced-colors` and `prefers-contrast` media queries recreate the
snapshotted painter and replace the complete overlay configuration. Forced
colors use resolved system colors, and increased contrast makes the track
opaque. Hex1b owns reduced-motion behavior. Theme observers and media listeners
are removed when the view is disposed.

Marker hover previews decorate `renderDefaultScrollbarTooltip` with Aspire's
popup background, border, radius, shadow and UI typography. Their colors are
resolved in the same outer Dashboard theme scope as the markers, with system
colors in forced-color mode. Hex1b still owns safe text rendering, detail loading,
positioning and tooltip lifetime.

The existing HMP-to-HWT mirror has its own 10,000-row scrollback capacity.
Hex1b now negotiates retained text and OSC 133 command-mark checkpoints by
default, restoring producer-backed history and marks on late attachment and
reconnect when both peers support them. The built-in scrollbar exposes mark
navigation without a Dashboard mode chooser or custom tooltip UI. Marks follow
retained content and disappear on eviction; unavailable marker rows are not row
zero. Browser-owned bookmarks remain per-view and do not survive reconnect.

### View lifecycle

Each reconnect aborts the previous mount and creates a new client. Mounting is
deferred while initially hidden; once connected, changing the Console/Terminal
view retains the client, selection, and producer-backed history. Disposal closes
only this view, never the server-side producer. Sizing changes explicitly request
primary when necessary and wait for role confirmation; normal input does not
take resize ownership. Public font-size limits are 8–32 pixels.

Opening an interactive terminal or activating its view focuses its keyboard input
once it is ready. Inactive dock panes do not take focus, and an asynchronous mount
does not take focus back from a control the user selected while it was loading.
Mouse clicks on the font stepper, Fit button, or a dimensions option return focus
to terminal input; keyboard activation keeps focus on the control for repeated
adjustments. Opening the dimensions picker keeps focus until an option is chosen.
Input and clipboard action failures are logged with `console.log` without an Aspire
banner. Diagnostics include the exception, selection status, document focus, and
clipboard permissions policy, never clipboard or selected text. These failures can
include pending selection resolution before the browser clipboard API is called;
they do not necessarily mean clipboard permission was denied.
Hex1b also displays its own inspection status inside its shadow root.
Its public API does not currently expose an option to suppress that native message.
Other terminal status and sizing errors offer **Dismiss**, which clears the local
error and returns focus without reconnecting or discarding terminal history.
Only connection/initialization failures offer **Reconnect terminal**.

Native `onClose` reports transport closure even before mounting completes.
Aspire reserves WebSocket close code `4000` for authoritative AppHost producer
completion; normal closure, abnormal disconnects, close reasons and `wasClean`
never imply completion. Completed views stay visible without reconnecting;
other disconnects use bounded retries. No application messages are added to HWT.
The last available projection can remain after completion, but a final frame is
not guaranteed and completion before mounting can leave an empty view.

The browser's `setReadOnly` and the server's per-presentation
`Hwt1PresentationAdapter.IsReadOnly` enforce live input policy independently.
The component updates server policy before browser UX. Native browser gating
also covers held pointers, queued gestures, direct paste/action calls and
pending clipboard reads. Inspection remains available while the connection is
live; already accepted or in-flight commands cannot be recalled.

Role state comes from the public `onRoleChange` callback's `id`, `primaryId`,
and `isPrimary` fields. The backend's direct HMP workload mirror preserves
remote primary identity, takeover, and resize authority in this metadata.
The callback does not expose a full peer roster, and the dashboard does not
infer one from the primary identity.

### Migration boundaries

Ctrl/Cmd+click opens server-authoritative OSC 8 hyperlinks using the package's
built-in routing, including links in history and read-only views. Only absolute
HTTP, HTTPS and mailto destinations are allowed, and tabs use
`noopener,noreferrer`. Plain clicks and drags retain selection/application
behavior; Shift and Alt reserve selection gestures. Aspire does not add its own
opener, custom-scheme support, or plain-text URL detection. HMP state replay
preserves link destinations when a browser attaches or reconnects. See
[the hyperlink PR](https://github.com/mitchdenny/hex1b/pull/489) and
[the replay fix](https://github.com/mitchdenny/hex1b/pull/493).

The public API supports auto/fixed sizing, primary requests, keyboard and mouse
input, paste/copy, selection, and producer-backed history. It exposes workload
title, progress, and shell-integration state with change callbacks; the dashboard
does not yet consume these and still labels the terminal with the resource name.
It has no terminal theme setter, search API, or clear-buffer API. Terminal
colors and content are server-authoritative; inspection UI uses the package's
theme defaults/tokens. The previous terminal hardcoded dark xterm colors rather
than offering a theme control. Search, filtering, clearing the log display, and
downloads belong to the separate Blazor `LogViewer`, which does not use xterm
and is unchanged by this migration.

The dashboard's lifecycle adapter is covered by Node's built-in test runner.
From the repository root, the core suite can run directly with **no npm install
and no `node_modules` directory**:

```shell
node --test tests/Aspire.Dashboard.Components.Tests/JavaScript/*.test.mjs
```

These tests use only Node built-ins and checked-in assets. Both suites also
run in CI through `Infrastructure.Tests` using the existing `NodeCommand`
helper. They verify focused shadow-DOM input isolation from dashboard shortcuts,
cancellation, reconnect generations, visibility, role-gated sizing, failure
state, PathBase asset URLs, deployment asset presence, and exact version parity
between `Directory.Packages.props`, the npm manifest/lockfile, and the minified
bundle header. Reproducible minification and installed font/license byte comparison belong to the separate
acquisition verification command above. Neither suite substitutes for a browser
WebGPU/WebGL2 rendering test or multi-peer server/CLI integration tests.
