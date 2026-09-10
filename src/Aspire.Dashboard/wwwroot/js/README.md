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

`hex1b-web-terminal/` vendors `@hex1b/web-terminal` **0.167.0-alpha.1549.1.496ccf5**,
paired with the Hex1b NuGet build from commit
`496ccf508470eed8744dbe46675e3d26928e8c91`. The client and server use the evolving
HWT1 presentation transport and must be updated together. Do not substitute a
different client based only on a similar version number.

From `src/Aspire.Dashboard`, use Node.js 22 or later to acquire and update assets:

```shell
npm ci --ignore-scripts
npm run update-terminal-assets
npm test
```

`update-terminal-assets` copies the package and then verifies that every emitted
file matches the installed package byte-for-byte, including metadata/licenses,
and that no stale files remain in `dist/`. To rerun that acquisition-only check
without copying, use `npm run verify-terminal-assets` after `npm ci`. This check
intentionally requires `node_modules`; ordinary regression tests do not.

Review and commit the manifest, lockfile, and generated asset changes together.
This is a manual acquisition step: ordinary .NET builds use the checked-in files
and do not run npm or download frontend packages.

The update script copies the **complete `dist/` tree**, preserving relative ES
module, module-worker, source-map, declaration, and font paths. It also retains
the package metadata, README, and MIT license. The bundled Cascadia Mono NF font
has its own SIL Open Font License and provenance under
`dist/fonts/cascadia-mono-nf/`. Do not flatten, selectively bundle, or edit these
vendored files. `TerminalView.razor.js` imports only the public `dist/index.js`
entry point, not the package's internal protocol/renderer modules.

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
The package import, worker entry, and bundled font resolve relative to their
modules, so deployments under a PathBase retain the prefix throughout the
asset tree. No blob worker, eval, CDN, or cross-origin font permission is
needed. The dashboard's existing `script-src 'self'` also allows same-origin
workers through the CSP worker-source fallback; its production
`default-src 'self'` covers the font and same-origin connections.

Each reconnect aborts the previous mount and creates a new client. Mounting is
deferred while initially hidden; once connected, changing the Console/Terminal
view retains the client, selection, and producer-backed history. Disposal closes
only this view, never the server-side producer. Sizing changes explicitly request
primary when necessary and wait for role confirmation; normal input does not
take resize ownership. Public font-size limits are 8–32 pixels.

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
between `Directory.Packages.props`, the npm manifest/lockfile, and the vendored
package. Complete installed-package byte comparison belongs to the separate
acquisition verification command above. Neither suite substitutes for a browser
WebGPU/WebGL2 rendering test or multi-peer server/CLI integration tests.
