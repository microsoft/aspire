# Cascadia Mono NF

Cascadia Mono with the Nerd Font glyph set, used by the dashboard's
embedded terminal view (`TerminalView`) so modern terminal applications
(devbox prompts, lazygit, htop, k9s, etc.) render Powerline separators
and Nerd Font icons correctly rather than as missing-glyph boxes.

## Source

- Upstream: <https://github.com/microsoft/cascadia-code>
- Release: `v2407.24` (latest as of bundling)
- File: `woff2/CascadiaMonoNF.woff2` (variable font — covers all
  weights in one ~950 KB asset).

Cascadia Mono NF is the **mono** (no ligatures, ligatures are
problematic in terminal output) variant of Cascadia Code with the
official Nerd Font patch. It is built and published by Microsoft.

## License

SIL Open Font License, Version 1.1. See `LICENSE.txt` in this
directory for the full text and the reserved-font-name notice. OFL
section 2 permits bundling the font with any software provided the
copyright notice and license are included alongside the font file,
which is what `LICENSE.txt` is for.

## How it's used

The font is referenced via a `@font-face` rule injected by
`Components/Controls/TerminalView.razor.js` (`ensureTerminalStyles()`)
under the family name `"Cascadia Mono NF"`, which is then passed to
the xterm.js `Terminal` constructor's `fontFamily` option with system
monospace fallbacks for the brief moment before the woff2 finishes
loading and as a hard fallback if the asset is unavailable.

## Updating

1. Download the latest release zip from
   <https://github.com/microsoft/cascadia-code/releases>.
2. Replace `CascadiaMonoNF.woff2` with the new
   `woff2/CascadiaMonoNF.woff2`.
3. If upstream updates the LICENSE text (rare), refresh
   `LICENSE.txt`.
4. Bump the release tag at the top of this README so the source is
   traceable.
