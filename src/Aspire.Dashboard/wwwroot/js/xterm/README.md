# Vendored xterm.js assets

These files back the dashboard's interactive terminal (`TerminalView`). They are
committed rather than fetched at runtime because the dashboard's Content Security
Policy is `script-src 'self'` and the dashboard must work offline.

| File | Source | Version |
|------|--------|---------|
| `xterm.min.js` | [`@xterm/xterm`](https://www.npmjs.com/package/@xterm/xterm) via jsDelivr | `6.1.0-beta.301` |
| `xterm.min.css` | `@xterm/xterm` via jsDelivr | `6.1.0-beta.301` |
| `addon-fit.min.js` | [`@xterm/addon-fit`](https://www.npmjs.com/package/@xterm/addon-fit) via jsDelivr | `0.12.0-beta.299` |
| `addon-image.min.js` | Local production build of `@xterm/addon-image` from [xterm.js PR #6098](https://github.com/xtermjs/xterm.js/pull/6098) | `0.9.0` |

The jsDelivr-sourced files are the upstream `dist` bundles; jsDelivr's minifier
adds the provenance header at the top of each file and reports that the JS was
already minified upstream. They can be refreshed with:

```bash
curl -o xterm.min.js  "https://cdn.jsdelivr.net/npm/@xterm/xterm@6.1.0-beta.301/lib/xterm.min.js"
curl -o xterm.min.css "https://cdn.jsdelivr.net/npm/@xterm/xterm@6.1.0-beta.301/css/xterm.min.css"
curl -o addon-fit.min.js "https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.12.0-beta.299/lib/addon-fit.min.js"
```

## Why the prerelease xterm.js

`@xterm/addon-image` reaches into xterm.js internals (`term._core`), so the addon
build below only composes with the 6.x core. The 6.1.0 beta line is the version
the Hex1b `WebMuxerDemo` sample validates the Kitty graphics path against, so the
dashboard tracks the same pair. Move both to stable releases together.

## Why `addon-image.min.js` is not from npm

The published `@xterm/addon-image` package has only partial Kitty Graphics
Protocol support: it lacks placement identity, placement replacement, and
targeted deletion. Without those, a Hex1b widget that redraws an image (for
example while a window is dragged or resized) leaks placements and renders
stale copies. The build committed here comes from the PR that adds them:

- PR: <https://github.com/xtermjs/xterm.js/pull/6098>
- Commit: `5b65c03690770673f407931c767f72ec908dce2c`
- PR base (upstream master): `d3e32b344dfe7dd6015cff6a9aeaaeaeccdc2789`
- Package version: `0.9.0`
- Bundle SHA-256: `94fb5ca7413520807bb1efd9639f06c869ad8e913620393d4d7a02dba2ac5093`
- License: MIT (`addon-image.LICENSE.txt`)

Replace this with the upstream npm package once PR #6098 ships.

The hex1b sample vendors its own copy of this bundle at
`samples/WebMuxerDemo/wwwroot/vendor/xterm-addon-image.js`, from an earlier commit
on the same PR. That copy predates the fixes described below, so the two are no
longer byte-identical; this one is newer.

Note that the PR is periodically rebased, so its commit SHAs change. Match the
bundle by its SHA-256 rather than assuming the commit above is still reachable.

The core `xterm.min.js` is deliberately *not* rebuilt from this PR — the PR only
touches `addons/addon-image/**`, and the addon's use of core internals (`_core.*`,
`_extendedAttrs`, `_data`, `getBg`, the parser `register*Handler` hooks) is
unchanged by it, so the npm core build below stays compatible.

### Placement index vs. `HAS_EXTENDED`

Worth knowing when reading this addon, because it is subtle and was a live bug.

xterm.js flags cells carrying extended attributes with `HAS_EXTENDED` (`1 << 28`)
and stores the data in the line's `_extendedAttrs` sparse array. Overwriting a
cell with plain text clears the flag but does **not** prune `_extendedAttrs`.
`ImageStorage.render()` relies on that deliberately: it treats a leftover
`_extendedAttrs.imageId` as live so an image keeps drawing over text written on
top of it (Kitty `C=1` semantics).

The placement index this PR adds originally treated the flag as authoritative, so
`a=d,d=a` ("delete all visible placements") skipped exactly the cells the renderer
still drew. Hex1b moves a window by issuing `a=d,d=a`, freeing the old image with
`a=d,d=I`, then transmitting under a new image id — so those skipped cells were
left referencing a deleted image and rendered as grey checkerboard placeholders at
every previous window position.

The PR fixes this by aligning the index and deletion paths with the renderer
(`getVisibleImageStorageIds`, `_clearImageCells`, `_rebuildImageCellIndex`,
`_untrackCell`, `_writeToCell`) rather than making the renderer strict. If a future
re-vendor reintroduces grey rectangles after moving a window, this is the first
place to look.

Upstream is actively working the same area — `xtermjs/xterm.js#6131` ("Restore text
overwrite of image tiles") landed on master and is included here — so this
interaction is worth re-checking on every re-vendor.

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

## Kitty graphics constraints

`TerminalView.razor.js` loads the addon with Sixel and iTerm inline images
disabled and Kitty enabled, because Hex1b only emits Kitty sequences.

### Why the CSP needs `'wasm-unsafe-eval'`

Kitty payloads are base64, and the addon decodes them with a streaming base64
decoder from the `sixel` package that is compiled to WebAssembly via `inwasm`.
It is instantiated lazily on the first payload chunk:

```
KittyGraphicsHandler.put -> _streamPayload -> decoder.init()
  -> new WebAssembly.Module / new WebAssembly.Instance
```

This is on the mandatory path for every Kitty transmission, so turning Sixel off
does not avoid it, and the addon has no JavaScript fallback for this decoder.
(The `atob` fallbacks elsewhere in the bundle decode the embedded wasm binary
itself, which is stored as base64.)

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
- HMP1's `StateSync` snapshot carries ANSI cell state, not image payloads, so a
  viewer that attaches after an image was drawn will not see it until the
  workload repaints.
