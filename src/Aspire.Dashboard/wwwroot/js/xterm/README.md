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
- Commit: `fc43cb00b0ad6fa440bd3c2373b73b3bad0bdcaa`
- Package version: `0.9.0`
- Bundle SHA-256: `f6c1449ff66340af3f6bc60f1afa3c4617a7866d63a95ac9934465aee6adef9e` (before the local patch below)
- License: MIT (`addon-image.LICENSE.txt`)

It is based on the bundle vendored at
`samples/WebMuxerDemo/wwwroot/vendor/xterm-addon-image.js` in
[mitchdenny/hex1b](https://github.com/mitchdenny/hex1b), which is the reference
implementation for this end-to-end path. Replace it with the upstream npm package
once PR #6098 ships.

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

`lib/addon-image.js` from that branch is byte-identical to the pre-patch bundle
recorded above (`f6c1449f…`), which confirms this is the same pipeline that
produced the hex1b sample's copy.

After re-vendoring, update the hashes in this file and re-check the terminal:
open a Hex1b `KgpDemo` image window in the dashboard and drag it around, then
confirm no grey rectangles remain at the previous positions.

### Local patch: stale image cells rendered as grey tombstones

This section exists only because the fix below is not yet in the PR's source. As
soon as it is, rebuild per the steps above and delete this section — do not carry
the hand patch forward.

The bundle here is **not** byte-identical to the hex1b copy. One patch is applied
on top of it (post-patch SHA-256
`603bc9292df5c99569f50e4448f9efcb47b29c1fc9a23b103287021cdd2ee431`), making
`ImageStorage.render()` skip cells whose `HAS_EXTENDED` background flag is clear.

xterm.js marks a cell as carrying extended attributes with the `HAS_EXTENDED`
background flag (`1 << 28`), and stores the attributes in the line's
`_extendedAttrs` sparse array. When a cell is overwritten with ordinary text,
xterm clears the flag but **does not prune `_extendedAttrs`** — the old object
stays behind.

xterm.js master deliberately reads `_extendedAttrs[col]` in `ImageStorage.render()`
even when the flag is clear, so that an image keeps drawing over text written on
top of it (Kitty `C=1` semantics). That part is intentional and is not something
PR #6098 introduced.

The bug is an asymmetry the PR creates. It adds `getVisibleImageStorageIds()`,
which backs the `a=d,d=a` ("delete all visible placements") command, and that scan
*does* gate on `HAS_EXTENDED`. So a cell the application later painted text over
is still rendered, but is invisible to delete-all. Hex1b moves a window by issuing
`a=d,d=a`, then freeing the old image with `a=d,d=I`, then transmitting under a new
image id — after which those skipped cells reference an image that no longer
exists:

- if the image is still stored, it is redrawn on top of whatever text now
  occupies those cells;
- if the image has since been deleted, the cell has no image to resolve, so
  `showPlaceholder` paints the grey checkerboard instead.

The second case is what produced grey rectangles wherever a Hex1b window had
previously been. Hex1b redraws a moved window by transmitting a new image id,
clearing placements with `a=d,d=a`, then freeing the old image with `a=d,d=I`.
`a=d,d=a` only clears cells that still have `HAS_EXTENDED` set, so any cell the
application had already painted over kept a dangling `imageId` and turned grey
once its image was freed.

The applied patch (two edits, both in the render loop) suppresses the symptom by
making the renderer strict, i.e. ignoring cells whose flag was cleared:

```js
// before
let s;if(268435456&i.getBg(A))s=null!==(t=i._extendedAttrs[A])&&void 0!==t?t:r;else{const e=i._extendedAttrs[A];if(!e||void 0===e.imageId||-1===e.imageId)continue;s=e}
// after
let s;if(!(268435456&i.getBg(A)))continue;s=null!==(t=i._extendedAttrs[A])&&void 0!==t?t:r;

// before
for(;++A<n;){const e=i._extendedAttrs[A];if(!e||e.imageId!==a||e.tileId!==t+l)break;l++}
// after
for(;++A<n;){if(!(268435456&i.getBg(A)))break;const e=i._extendedAttrs[A];if(!e||e.imageId!==a||e.tileId!==t+l)break;l++}
```

**This is a mitigation, not the correct fix**, and it must not be proposed
upstream: it trades away the intended "images survive text overwrites" behaviour.
The proper fix belongs in PR #6098, keeping the renderer lax and making deletion
cover the same cell set, so that no cell can outlive its image. Making
`getVisibleImageStorageIds()` lax on its own was tried and is *not* sufficient —
clearing ultimately runs through `_clearImageCells()` and the `_imageCells` index.

Measured by dragging a KgpDemo window around the dashboard terminal and counting
buffer cells whose `imageId` is absent from `_images` (these are the cells drawn
grey), after opening one window and four title-bar drags:

| build | dangling cells |
|-------|----------------|
| PR build as vendored | 0 → 96 |
| with this patch | 0 → 0 |
| `getVisibleImageStorageIds()` made lax instead | still dirty (24) |

Normal image display is unaffected by the patch.


## Kitty graphics constraints

`TerminalView.razor.js` loads the addon with Sixel and iTerm inline images
disabled and Kitty enabled, because Hex1b only emits Kitty sequences.

The addon decodes payloads through a WebAssembly module, so the dashboard's
Content Security Policy includes `'wasm-unsafe-eval'` in `script-src` (see
`Model/BrowserSecurityHeadersMiddleware.cs`). Under a bare `script-src 'self'`
the browser blocks wasm instantiation, the addon throws while handling the APC
sequence, and the terminal stops advancing until the next reconnect — the image
never appears and live output appears frozen.

Other known limits of this path:

- Only direct transmission (`t=d`) works. File, temp-file, and shared-memory
  transmission name backend resources a browser cannot reach.
- Unicode placeholders, relative placement, animation, and the rarer deletion
  selectors are unsupported by the addon.
- HMP1's `StateSync` snapshot carries ANSI cell state, not image payloads, so a
  viewer that attaches after an image was drawn will not see it until the
  workload repaints.
