# Vendored xterm.js assets

These files back the dashboard's interactive terminal (`TerminalView`). They are
committed rather than fetched at runtime because the dashboard's Content Security
Policy is `script-src 'self'` and the dashboard must work offline.

| File | Source | Version |
|------|--------|---------|
| `xterm.min.js` | [`@xterm/xterm`](https://www.npmjs.com/package/@xterm/xterm) via jsDelivr | `6.1.0-beta.304` |
| `xterm.min.css` | `@xterm/xterm` via jsDelivr | `6.1.0-beta.304` |
| `addon-fit.min.js` | [`@xterm/addon-fit`](https://www.npmjs.com/package/@xterm/addon-fit) via jsDelivr | `0.12.0-beta.299` |
| `addon-image.min.js` | Local production build of `@xterm/addon-image` from [xterm.js PR #6098](https://github.com/xtermjs/xterm.js/pull/6098) | `0.9.0` |

The jsDelivr-sourced files are the upstream `dist` bundles; jsDelivr's minifier
adds the provenance header at the top of each file and reports that the JS was
already minified upstream. They can be refreshed with:

```bash
curl -o xterm.min.js  "https://cdn.jsdelivr.net/npm/@xterm/xterm@6.1.0-beta.304/lib/xterm.min.js"
curl -o xterm.min.css "https://cdn.jsdelivr.net/npm/@xterm/xterm@6.1.0-beta.304/css/xterm.min.css"
curl -o addon-fit.min.js "https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.12.0-beta.299/lib/addon-fit.min.js"
```

## Core and addon versions are coupled — bump them together

`addon-image` uses core APIs that are still evolving, so the core version is not
freely chosen: it must be new enough for the addon build.

The current addon build requires `IBufferLine.getExtended()` and
`IExtendedAttrs.payload`, which arrived in core via
[xterm.js PR #5879](https://github.com/xtermjs/xterm.js/pull/5879) and first
appear in npm `@xterm/xterm@6.1.0-beta.304`. Pairing this addon with
`6.1.0-beta.303` or earlier fails at runtime — `line.getExtended` is `undefined`.

Before re-vendoring the addon, diff the core internals it references and check
they all exist in the pinned core:

```bash
grep -oE '_core\.[_A-Za-z][\w.]*|\.getExtended\(|\.payload\b|\.getBg\(|register\w*Handler' \
  addon-image.min.js | sort -u
```

## Why the prerelease xterm.js

`@xterm/addon-image` reaches into xterm.js internals, so the addon build below
only composes with the 6.x core, and specifically with a core new enough to
satisfy the coupling described above. Move both to stable releases together.

## Why `addon-image.min.js` is not from npm

The published `@xterm/addon-image` package has only partial Kitty Graphics
Protocol support: it lacks placement identity, placement replacement, and
targeted deletion. Without those, a Hex1b widget that redraws an image (for
example while a window is dragged or resized) leaks placements and renders
stale copies. The build committed here comes from the PR that adds them:

- PR: <https://github.com/xtermjs/xterm.js/pull/6098>
- Commit: `17d763b5bc54b363ed17a0dee614a852dda19aab`
- PR base (upstream master): `c58ea3637f3968e0e6e79cd92cf9aace7ef89ee2`
- Package version: `0.9.0`
- Bundle SHA-256: `26fe1287e2f0aa00e7ce7d92a2b7ae6cc37da1e45ad47feb511af310b2c2961e`
- License: MIT (`addon-image.LICENSE.txt`)

Replace this with the upstream npm package once PR #6098 ships.

The hex1b sample vendors its own copy of this bundle at
`samples/WebMuxerDemo/wwwroot/vendor/xterm-addon-image.js`, from an earlier commit
on the same PR. That copy predates the fixes described below, so the two are no
longer byte-identical; this one is newer.

Note that the PR is periodically rebased and re-merged with upstream master, so
its commit SHAs change. Match the bundle by its SHA-256 rather than assuming the
commit above is still reachable.

The core `xterm.min.js` is taken from npm, not rebuilt from this PR — the PR only
touches `addons/addon-image/**`. It must still be new enough to satisfy the
coupling described above, so re-check that whenever the addon is re-vendored.

### How image cells are tracked

Worth knowing when reading this addon, because the design changed and older notes
about it are misleading.

Image tiles are recorded on the buffer cell's extended attributes. The addon used
to reach directly into `line._extendedAttrs` and gate on the `HAS_EXTENDED`
background flag (`1 << 28`). That was fragile: writing text over a cell clears the
flag but does **not** prune `_extendedAttrs`, so the renderer and the deletion
paths disagreed about which cells were still image cells. The visible symptom was
grey checkerboard rectangles left wherever a Hex1b window had previously been —
`a=d,d=a` ("delete all visible placements") skipped cells the renderer still drew,
so after the image was freed those cells referenced nothing.

Upstream [PR #5879](https://github.com/xtermjs/xterm.js/pull/5879) replaced that
with a first-class core API: `IBufferLine.getExtended(col)` and a generic
`IExtendedAttrs.payload`. The addon now stores an `ImageTileInfo` in `payload` and
no longer touches `_extendedAttrs`, `_data`, or `getBg` at all, which removes the
flag-vs-attrs asymmetry at the root.

If a future re-vendor reintroduces grey rectangles after moving a window, start
with the payload lifecycle in `ImageStorage` (`_writeToCell`, the index rebuild,
and the `a=d` handlers).

## Rebuilding `addon-image.min.js` from source

This file is a webpack bundle produced by the xterm.js repo, so re-vendoring is a
build-and-copy, not a hand edit. From a checkout of the PR branch
(`mitchdenny-fix-kitty-placements`):

```bash
cd addons/addon-image
npm run prepackage    # tsgo -p .
npm run package       # webpack -> lib/addon-image.js
```

then copy the result over this file:

```bash
cp addons/addon-image/lib/addon-image.js \
   src/Aspire.Dashboard/wwwroot/js/xterm/addon-image.min.js
```

`lib/addon-image.js` from that branch is what this file contains; its SHA-256 is
recorded above.

After re-vendoring, update the hashes in this file and re-check the terminal:
open a Hex1b `KgpDemo` image window in the dashboard and drag it around, then
confirm no grey rectangles remain at the previous positions.

## Graphics protocol constraints

`TerminalView.razor.js` loads the addon with **Kitty and Sixel enabled** and
iTerm inline images (IIP) disabled, because Hex1b can emit the first two but
never the third.

Both protocols travel the same path — workload PTY → Hex1b HMP1 producer →
dashboard WebSocket → `addon-image` — and the dashboard does not interpret
either. Sixel arrives DCS-framed (`ESC P … q … ESC \`) and is picked up by the
addon's DCS `q` handler; Kitty arrives APC-framed (`ESC _G … ESC \`).

### Why the CSP needs `'wasm-unsafe-eval'`

Kitty payloads are base64, and the addon decodes them with a streaming base64
decoder from the `sixel` package that is compiled to WebAssembly via `inwasm`.
It is instantiated lazily on the first payload chunk:

```text
KittyGraphicsHandler.put -> _streamPayload -> decoder.init()
  -> new WebAssembly.Module / new WebAssembly.Instance
```

This is on the mandatory path for every Kitty transmission, so it is not a
consequence of enabling Sixel — disabling Sixel would not avoid it, and the
addon has no JavaScript fallback for this decoder. (The `atob` fallbacks
elsewhere in the bundle decode the embedded wasm binary itself, which is stored
as base64.)

Under a bare `script-src 'self'` the browser refuses to compile the module, the
addon throws part-way through the APC sequence, and xterm's parser is left
mid-sequence — the image never appears *and* the terminal stops advancing until
the next reconnect. Hence `'wasm-unsafe-eval'` in
`Model/BrowserSecurityHeadersMiddleware.cs`. It permits only WebAssembly
compilation and still forbids `eval()` and `new Function()`.

A wasm-free fallback for this decode (`Uint8Array.fromBase64()`, or `atob`)
would let strict-CSP hosts drop the relaxation entirely; worth raising upstream
alongside PR #6098.

Other known limits of this path:

- Only direct transmission (`t=d`) works. File, temp-file, and shared-memory
  transmission name backend resources a browser cannot reach.
- Unicode placeholders, relative placement, animation, and the rarer deletion
  selectors are unsupported by the addon.

Images now survive reattach. HMP1's `StateSync` snapshot originally carried ANSI
cell state but not image payloads, so a viewer that attached after an image was
drawn saw only the surrounding text until the workload repainted. Hex1b
`0.166.0-beta.1416.1.1e1d57b` replays image state as well; reloading the
dashboard tab restores the image without touching the workload.
