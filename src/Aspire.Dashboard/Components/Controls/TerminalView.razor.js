// xterm.js terminal integration for the Aspire Dashboard. The browser
// speaks HMP v1 directly to the dashboard's /api/terminal WebSocket
// endpoint, which is a dumb byte pipe to the upstream Aspire.TerminalHost
// over the resource's per-replica consumer UDS. From the upstream's
// perspective this tab is a regular HMP v1 peer in the multi-head
// roster, so take-control / role-change / state-replay all flow
// through end-to-end without any dashboard-side translation.
//
// xterm.js is loaded via script tags (not ES module import) because
// the minified bundle uses UMD format, not ESM exports.

import { Hmp1Client } from "/js/hmp1-client.js";

const terminals = new Map();
let nextId = 1;
const textEncoder = new TextEncoder();

// Diagnostics gate. Set window.__aspireTerminalDebug = true in DevTools
// before loading the page (or before the first terminal is opened) to
// emit a structured trace of every lifecycle event. Default off so the
// console is quiet for end users.
function dbg(state, event, extra) {
    if (!window.__aspireTerminalDebug) return;
    const id = state ? state.id : '-';
    const t = performance.now().toFixed(1);
    const tag = `[term#${id} +${t}ms]`;
    if (extra !== undefined) {
        console.log(tag, event, extra);
    } else {
        console.log(tag, event);
    }
}

function ensureXtermLoaded() {
    return new Promise((resolve, reject) => {
        if (window.Terminal) {
            resolve();
            return;
        }

        // Load CSS
        if (!document.querySelector('link[href*="xterm.min.css"]')) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = '/js/xterm/xterm.min.css';
            document.head.appendChild(link);
        }

        // Load xterm.js
        const xtermScript = document.createElement('script');
        xtermScript.src = '/js/xterm/xterm.min.js';
        xtermScript.onload = () => {
            // Load fit addon
            const fitScript = document.createElement('script');
            fitScript.src = '/js/xterm/addon-fit.min.js';
            fitScript.onload = () => resolve();
            fitScript.onerror = (e) => reject(new Error('Failed to load xterm fit addon'));
            document.head.appendChild(fitScript);
        };
        xtermScript.onerror = (e) => reject(new Error('Failed to load xterm.js'));
        document.head.appendChild(xtermScript);
    });
}

// Auto-reconnect configuration. The dashboard WS may close for many
// reasons during normal operation: the underlying process exits and DCP
// relaunches it (the terminal host's TerminalReplica recycle loop rebinds
// its UDS in between), the user restarts the resource from the dashboard,
// or transient network/IPC issues. We treat ALL closes as transient and
// retry with exponential backoff up to MAX_RECONNECT_ATTEMPTS, after which
// we give up and write a one-line "[disconnected]" hint into the terminal
// so a stopped/removed resource doesn't leave the JS hammering the server
// at 1-attempt-every-5-seconds forever and the user understands why the
// terminal is no longer updating.
//
// Each state has a single reconnect "generation" counter. Every time we
// open a new client the generation bumps; client.on* callbacks compare
// against the captured generation and bail if a newer connect has
// superseded them. This prevents two failure modes:
//   1. A late onClose from client N firing AFTER client N+1 has connected
//      and scheduling a redundant reconnect.
//   2. An explicit reconnectTerminal() call colliding with a pending
//      auto-reconnect timer (the new connect bumps the generation, so
//      the timer's callback no-ops when it fires).
const RECONNECT_BACKOFF_MS = [500, 1000, 2000, 4000, 5000];
const MAX_RECONNECT_ATTEMPTS = 30; // ≈ 5*4 + 26*5 ≈ 150s of trying

function pickReconnectDelay(attempt) {
    const idx = Math.min(attempt, RECONNECT_BACKOFF_MS.length - 1);
    return RECONNECT_BACKOFF_MS[idx];
}

function scheduleReconnect(state) {
    if (!state.reconnect.enabled) {
        return;
    }
    if (state.reconnect.timer !== null) {
        return;
    }
    if (state.reconnect.attempts >= MAX_RECONNECT_ATTEMPTS) {
        try {
            state.term.write('\r\n\x1b[33m[terminal disconnected — reload the page or re-select the resource to retry]\x1b[0m\r\n');
        } catch { /* ignore */ }
        dbg(state, 'scheduleReconnect: gave up', { attempts: state.reconnect.attempts });
        return;
    }
    const delay = pickReconnectDelay(state.reconnect.attempts);
    state.reconnect.attempts++;
    dbg(state, 'scheduleReconnect: scheduled', { attempt: state.reconnect.attempts, delayMs: delay });
    state.reconnect.timer = setTimeout(() => {
        state.reconnect.timer = null;
        if (!state.reconnect.enabled) {
            return;
        }
        connectClient(state, state.wsUrl);
    }, delay);
}

function cancelPendingReconnect(state) {
    if (state.reconnect.timer !== null) {
        clearTimeout(state.reconnect.timer);
        state.reconnect.timer = null;
    }
}

// --- Primary-mode sizing controls ----------------------------------------
//
// Lifted from samples/WebMuxerDemo/wwwroot/js/app.js (Hex1b 0.147.0). See
// docs/muxer-learnings.md sections 3 (the three render modes) and 4
// (state sync, mode-transition triggers) for the design contract.
//
// In primary mode we drive the producer's PTY dims, so we expose a footer
// with two mutually-exclusive sizing modes:
//
//   "font"   (Fit)   : user controls font size with +/- buttons; FitAddon
//                      picks cols×rows to fill the available stage at that
//                      font. Window resize → fit → new cols×rows broadcast.
//
//   "fixed"  (preset): user picks a grid (e.g. 80×24) from the dropdown;
//                      we compute the largest font that makes that grid
//                      fill the stage and lock cols×rows. Window resize →
//                      recompute font, cols×rows stay fixed (no broadcast).
//
// In secondary mode (someone else is primary), changing either control
// promotes this peer to primary. Until then we lock our xterm grid to the
// producer's cols×rows, then pick the largest integer font size whose
// rendered grid fits the viewport (letterboxing on whichever axis has spare
// room). This mirrors primary fixed-mode; we deliberately avoid CSS
// transform: scale() here because xterm.js computes mouse-to-cell coordinates
// from getBoundingClientRect (which returns transformed dims) divided by its
// internally-measured cell width (which is untransformed), so any scale != 1
// offsets text selection by roughly the scale factor.
const MIN_FONT_PX = 4;
const MAX_FONT_PX = 72;
const DEFAULT_FONT_PX = 13;
const DEFAULT_TERMINAL_COLS = 132;
const DEFAULT_TERMINAL_ROWS = 50;
const SIZE_PRESETS = [
    { value: "auto",   label: "Fit",    cols: 0,   rows: 0  },
    { value: "80x24",  label: "80×24",  cols: 80,  rows: 24 },
    { value: "80x30",  label: "80×30",  cols: 80,  rows: 30 },
    { value: "100x30", label: "100×30", cols: 100, rows: 30 },
    { value: "132x30", label: "132×30", cols: 132, rows: 30 },
    { value: "132x50", label: "132×50", cols: 132, rows: 50 },
];
const DEFAULT_CONTROL_LABELS = {
    decreaseFontSize: "Decrease font size",
    increaseFontSize: "Increase font size",
    terminalDimensions: "Terminal dimensions",
    fit: "Fit",
    focusControlsHint: "F6: Focus terminal controls",
};

// Inject the WebMuxerDemo terminal-frame styles into <head> exactly once
// per page load. Lifted near-verbatim from samples/WebMuxerDemo/wwwroot/
// css/styles.css with the page-level (header/aside/body) selectors
// dropped — only the .terminal-pane / #terminal-frame / titlebar / body
// / footer / scrollbar rules remain. Selectors are scoped to
// .aspire-terminal-host (the root we add to the Blazor element) so they
// can never bleed into the rest of the dashboard. IDs are kept as the
// WebMuxer source uses them since we instantiate at most one chrome per
// host element.
function ensureTerminalStyles() {
    if (document.getElementById('aspire-terminal-styles')) return;
    const css = `
/*
 * Bundled Nerd Font for the terminal view. Cascadia Mono NF is
 * Microsoft's official patched build of Cascadia Mono (no ligatures —
 * preferred for terminal output) with the Nerd Font glyph set, so
 * Powerline separators, devicons, weather icons, k9s/lazygit/htop
 * glyphs, etc. all render correctly instead of as tofu boxes. The
 * font ships as a single variable woff2 (~950 KB) covering all
 * weights. License: SIL OFL 1.1 — see
 * wwwroot/fonts/cascadia-mono-nf/LICENSE.txt.
 *
 * font-display: swap so the terminal renders immediately with the
 * fallback monospace stack and silently upgrades to Cascadia once
 * the woff2 lands. xterm.js measures cell width from
 * .xterm-char-measure-element which is re-measured on every theme/
 * options change; if we ever need to force a re-measure after the
 * font swap we can listen for document.fonts.ready, but in practice
 * the first measurement happens after the font has loaded for
 * already-cached fetches and the visual glitch on cold load is a
 * one-frame reflow.
 */
@font-face {
  font-family: 'Cascadia Mono NF';
  src: url('/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2') format('woff2-variations'),
       url('/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2') format('woff2');
  font-weight: 200 700;
  font-style: normal;
  font-display: swap;
}

.aspire-terminal-host {
  /*
   * --aspire-term-bg is the chrome around the framed terminal (the
   * "stage"). Track the dashboard theme via FluentUI's neutral layer
   * token so dark/light theme switches keep the surround in step with
   * the rest of the page. The actual xterm canvas inside #terminal-body
   * stays dark on purpose — terminals are conventionally dark and the
   * frame is its own card.
   */
  --aspire-term-bg: var(--neutral-layer-2);
  --aspire-term-fg: #c9d1d9;
  --aspire-term-fg-muted: #8b949e;
  --aspire-term-accent: #58a6ff;
  --aspire-term-accent-2: #56d364;
  --aspire-term-warn: #f0883e;
  --aspire-term-panel: #161b22;
  --aspire-term-border: #30363d;
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--aspire-term-bg);
  color: var(--aspire-term-fg);
  font: 14px system-ui, -apple-system, "Segoe UI", sans-serif;
  overflow: hidden;
  box-sizing: border-box;
}
.aspire-terminal-host * { box-sizing: border-box; }

.aspire-terminal-host .terminal-pane {
  flex: 1;
  /*
   * min-width: 0 overrides the flex default of min-width: auto. Without
   * it, the flex item refuses to shrink below the intrinsic width of
   * its contents — including #terminal-body's pinned inline width — so
   * horizontal window resize can't shrink the pane and applyRoleAwareLayout
   * never sees the narrower viewport.
   */
  min-width: 0;
  /*
   * Stage for the terminal — themed backdrop with a small breathing margin
   * around the .xterm frame. No drop-shadow on the frame, so we don't need
   * extra padding to give shadow blur space to extend. Top padding is 0 so the
   * framed terminal sits flush with the top of the pane, matching the console
   * logs view (which has no padding above its content).
   */
  padding: 0 8px 8px;
  overflow: hidden;
  display: flex;
  background: var(--neutral-layer-2);
}

.aspire-terminal-host #terminal {
  /*
   * Bare host for xterm.js. Fills the inner stage area, centres its
   * single .xterm child horizontally, and pins it to the top so the
   * terminal prompt starts at the natural reading position rather than
   * floating in the middle of the available space. Secondary peers
   * (which lock the grid to producer dims and apply a CSS scale
   * transform) still get horizontal letterboxing when narrower than
   * the stage.
   */
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  align-items: flex-start;
  justify-content: center;
}

/*
 * Terminal "card" — non-transformed wrapper around the xterm so the
 * border stays at fixed CSS pixel sizes regardless of any CSS scale
 * transform applied to the .xterm in secondary mode.
 */
.aspire-terminal-host #terminal-frame {
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
  background: #0d1117;
  border: 2px solid #3a4250;
  border-radius: 6px;
  overflow: hidden;
}

.aspire-terminal-host #terminal-titlebar {
  flex: 0 0 auto;
  min-width: 0;
  height: 30px;
  padding: 0 14px;
  background: linear-gradient(180deg, #1a2029 0%, #161b22 100%);
  border-bottom: 1px solid #30363d;
  color: var(--aspire-term-fg-muted);
  font: 12px ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;
  display: flex;
  align-items: center;
  user-select: none;
}

.aspire-terminal-host #terminal-title {
  min-width: 0;
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  letter-spacing: 0.2px;
}

/* Live grid size and preset selector in the terminal footer. */
.aspire-terminal-host #terminal-dims {
  flex: 0 0 auto;
  margin-left: 6px;
  padding: 2px 22px 2px 8px;
  background: #21262d;
  border: 1px solid var(--aspire-term-border);
  border-radius: 3px;
  color: var(--aspire-term-fg);
  font: inherit;
  font-variant-numeric: tabular-nums;
  letter-spacing: 0.2px;
  white-space: nowrap;
  cursor: pointer;
}
.aspire-terminal-host #terminal-dims:hover:not(:disabled) {
  background: #30363d;
  border-color: #484f58;
}
.aspire-terminal-host #terminal-dims:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}

.aspire-terminal-host #terminal-body {
  flex: 0 0 auto;
  position: relative;
  overflow: hidden;
  background: #0d1117;
  /*
   * Breathing room between the frame border and xterm's text so the
   * output isn't flush against the edge (matches native terminal UX).
   * Combined with box-sizing: border-box (inherited from the wildcard
   * rule above), the padding shrinks the content area xterm renders
   * into — the JS layout math in layoutTerminal / pinBodyToNatural adds
   * TERMINAL_BODY_PADDING_PX * 2 back when pinning the body to the
   * natural rendered dims so the frame keeps hugging the grid.
   */
  padding: 6px;
}

/*
 * Chromeless mode — used by the terminal dock, where the surrounding tab
 * strip already provides the framing and a title. Everything that makes
 * the terminal look like a standalone card is removed (stage padding,
 * frame border/radius, titlebar, body padding) so the xterm grid is the
 * only thing visible, and the frame stretches to fill the dock pane
 * instead of hugging the grid. The JS layout math reads the same values
 * from state.frameBorderPx / state.bodyPaddingPx, which are zeroed for
 * chromeless terminals — keep the two in sync.
 */
.aspire-terminal-host.chromeless .terminal-pane {
  padding: 0;
  background: #0d1117;
}
.aspire-terminal-host.chromeless #terminal {
  align-items: stretch;
}
.aspire-terminal-host.chromeless #terminal-frame {
  flex: 1;
  border: none;
  border-radius: 0;
}
.aspire-terminal-host.chromeless #terminal-body {
  padding: 0;
}
/*
 * Chromeless terminals auto-fit their grid to the available space at the
 * dashboard font size, so the footer's fixed-size picker and font stepper
 * have nothing meaningful to control — and the dock/detached window asked
 * for the xterm grid alone. Hide the footer rather than special-casing the
 * control wiring, which stays identical for both modes.
 */
.aspire-terminal-host.chromeless #terminal-footer {
  display: none;
}

.aspire-terminal-host #terminal-footer {
  flex: 0 0 auto;
  min-width: 0;
  height: 30px;
  padding: 0 14px;
  background: linear-gradient(180deg, #1a2029 0%, #161b22 100%);
  border-top: 1px solid #30363d;
  color: var(--aspire-term-fg-muted);
  font: 12px ui-monospace, "SFMono-Regular", Menlo, Consolas, monospace;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  user-select: none;
}
.aspire-terminal-host #terminal-focus-hint {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.aspire-terminal-host #terminal-controls {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 6px;
}
.aspire-terminal-host #terminal-footer button {
  width: 22px;
  height: 22px;
  padding: 0;
  background: #21262d;
  border: 1px solid var(--aspire-term-border);
  border-radius: 3px;
  color: var(--aspire-term-fg);
  font: 14px/1 ui-monospace, monospace;
  cursor: pointer;
}
.aspire-terminal-host #terminal-footer button:hover:not(:disabled) {
  background: #30363d;
  border-color: #484f58;
}
.aspire-terminal-host #terminal-footer button:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}
.aspire-terminal-host #font-display {
  min-width: 34px;
  color: var(--aspire-term-fg);
  text-align: center;
  font-variant-numeric: tabular-nums;
}

.aspire-terminal-host .xterm:focus,
.aspire-terminal-host .xterm:focus-visible {
  outline: none;
}

/*
 * xterm.js scrollbar: overlay-style, only visible on hover.
 */
.aspire-terminal-host .xterm-viewport {
  scrollbar-width: none;
  -ms-overflow-style: none;
}
.aspire-terminal-host .xterm-viewport::-webkit-scrollbar {
  width: 0;
  background: transparent;
}
.aspire-terminal-host #terminal-frame:hover .xterm-viewport,
.aspire-terminal-host .xterm:hover .xterm-viewport,
.aspire-terminal-host .xterm-viewport:hover,
.aspire-terminal-host .xterm-viewport:focus-within {
  scrollbar-width: thin;
  scrollbar-color: rgba(139, 148, 158, 0.55) transparent;
}
.aspire-terminal-host #terminal-frame:hover .xterm-viewport::-webkit-scrollbar,
.aspire-terminal-host .xterm:hover .xterm-viewport::-webkit-scrollbar,
.aspire-terminal-host .xterm-viewport:hover::-webkit-scrollbar,
.aspire-terminal-host .xterm-viewport:focus-within::-webkit-scrollbar {
  width: 10px;
}
.aspire-terminal-host #terminal-frame:hover .xterm-viewport::-webkit-scrollbar-thumb,
.aspire-terminal-host .xterm:hover .xterm-viewport::-webkit-scrollbar-thumb,
.aspire-terminal-host .xterm-viewport:hover::-webkit-scrollbar-thumb,
.aspire-terminal-host .xterm-viewport:focus-within::-webkit-scrollbar-thumb {
  background: rgba(139, 148, 158, 0.55);
  border-radius: 5px;
  border: 2px solid transparent;
  background-clip: padding-box;
}
`;
    const style = document.createElement('style');
    style.id = 'aspire-terminal-styles';
    style.textContent = css;
    document.head.appendChild(style);
}

// Builds the terminal chrome inside the Blazor host element:
//
//   .aspire-terminal-host           (root with theme vars + flex column)
//     .terminal-pane                (the gradient stage; flex 1)
//       #terminal                   (xterm centring host)
//         #terminal-frame           (the bordered/shadowed card)
//           #terminal-titlebar      (title text from OSC 0/2)
//           #terminal-body          (xterm host; sized by layout)
//           #terminal-footer        (font size + dimensions controls)
//
// State snapshots can still flow up to .NET via `state.dotNetRef` when a
// host subscribes, but the frequently used sizing controls stay beside the
// terminal so they remain accessible without opening the page options menu.
//
// All lookup roots are scoped to state.host so the layout helpers can
// run in pages that might (in the future) host multiple terminals.
function buildChrome(state) {
    ensureTerminalStyles();

    const blazorElement = state.element;
    if (!blazorElement) return;

    // Defense in depth: never leave a previous terminal's chrome attached to
    // the Blazor container element. The .NET-side OnAfterRenderAsync guard
    // is the primary protection against re-entrant initialization, but if
    // anything ever calls initTerminal twice against the same element
    // (resource stop+restart bursts, lifecycle bugs, future hot-reload, …)
    // appending another host on top of an existing one leaves multiple
    // stacked xterm instances all wired to the same WebSocket — input
    // echoes everywhere and the terminals can render at different sizes.
    // Clearing the element first means worst-case we drop the previous
    // (now-orphaned) chrome instead of duplicating it.
    while (blazorElement.firstChild) {
        blazorElement.removeChild(blazorElement.firstChild);
    }

    // The Blazor element already has inline width/height: 100%. Wrap
    // it with our own host so we can apply our flex column layout
    // without disturbing whatever else the parent has set on it.
    const host = document.createElement('div');
    host.className = state.chromeless ? 'aspire-terminal-host chromeless' : 'aspire-terminal-host';
    blazorElement.appendChild(host);

    // Terminal stage.
    const pane = document.createElement('div');
    pane.className = 'terminal-pane';
    const terminalContainer = document.createElement('div');
    terminalContainer.id = 'terminal';
    pane.appendChild(terminalContainer);

    // Card.
    const frame = document.createElement('div');
    frame.id = 'terminal-frame';

    // Chromeless hosts get no titlebar at all rather than a hidden one, so
    // getAvailableBodySpace measures zero for it and the OSC title handler
    // below simply has nothing to write to. The dock renders the title on
    // its tab instead.
    let titlebar = null;
    let titleText = null;
    if (!state.chromeless)
    {
        titlebar = document.createElement('div');
        titlebar.id = 'terminal-titlebar';
        titleText = document.createElement('span');
        titleText.id = 'terminal-title';
        titleText.textContent = 'terminal';
        titlebar.appendChild(titleText);
    }

    const body = document.createElement('div');
    body.id = 'terminal-body';

    // The footer is built even for chromeless hosts so every control
    // reference on `state` stays non-null; CSS hides it in that mode.
    const footer = buildFooter(state);

    if (titlebar) {
        frame.append(titlebar, body, footer);
    }
    else {
        frame.append(body, footer);
    }
    terminalContainer.appendChild(frame);
    host.append(pane);

    state.host = host;
    state.terminalContainer = terminalContainer;
    state.terminalFrame = frame;
    state.terminalTitlebar = titlebar;
    state.titleText = titleText;
    state.terminalBody = body;
    state.terminalFooter = footer;
}

function buildFooter(state) {
    const footer = document.createElement('div');
    footer.id = 'terminal-footer';
    footer.tabIndex = -1;

    const focusHint = document.createElement('span');
    focusHint.id = 'terminal-focus-hint';
    focusHint.textContent = state.labels.focusControlsHint;

    const fontMinus = document.createElement('button');
    fontMinus.id = 'font-minus';
    fontMinus.type = 'button';
    fontMinus.textContent = '-';
    fontMinus.title = state.labels.decreaseFontSize;
    fontMinus.setAttribute('aria-label', state.labels.decreaseFontSize);
    fontMinus.disabled = true;
    fontMinus.addEventListener('click', () => {
        if (fontMinus.disabled) return;
        setFontSize(state, state.currentFontPx - 1);
        maybeAutoPromote(state);
    });

    const fontDisplay = document.createElement('span');
    fontDisplay.id = 'font-display';
    fontDisplay.textContent = `${state.currentFontPx}px`;

    const fontPlus = document.createElement('button');
    fontPlus.id = 'font-plus';
    fontPlus.type = 'button';
    fontPlus.textContent = '+';
    fontPlus.title = state.labels.increaseFontSize;
    fontPlus.setAttribute('aria-label', state.labels.increaseFontSize);
    fontPlus.disabled = true;
    fontPlus.addEventListener('click', () => {
        if (fontPlus.disabled) return;
        setFontSize(state, state.currentFontPx + 1);
        maybeAutoPromote(state);
    });

    const sizeSelect = document.createElement('select');
    sizeSelect.id = 'terminal-dims';
    sizeSelect.title = state.labels.terminalDimensions;
    sizeSelect.setAttribute('aria-label', state.labels.terminalDimensions);
    for (const preset of SIZE_PRESETS) {
        const option = document.createElement('option');
        option.value = preset.value;
        option.textContent = preset.value === 'auto' ? state.labels.fit : preset.label;
        sizeSelect.appendChild(option);
    }
    sizeSelect.disabled = true;
    sizeSelect.addEventListener('change', () => {
        if (sizeSelect.disabled) return;
        const selected = SIZE_PRESETS.find((preset) => preset.value === sizeSelect.value);
        if (!selected) return;

        if (selected.value === 'auto') {
            setSizeMode(state, 'font', null);
        } else {
            setSizeMode(state, 'fixed', { cols: selected.cols, rows: selected.rows });
        }
        maybeAutoPromote(state);
    });

    const controls = document.createElement('div');
    controls.id = 'terminal-controls';
    controls.append(fontMinus, fontDisplay, fontPlus, sizeSelect);
    footer.append(focusHint, controls);
    state.terminalFocusHint = focusHint;
    state.fontMinusBtn = fontMinus;
    state.fontDisplay = fontDisplay;
    state.fontPlusBtn = fontPlus;
    state.sizeSelect = sizeSelect;

    return footer;
}

const FOCUSABLE_ELEMENT_SELECTOR = [
    'a[href]',
    'button:not([disabled])',
    'input:not([disabled])',
    'select:not([disabled])',
    'textarea:not([disabled])',
    '[tabindex]:not([tabindex="-1"])',
].join(',');

function moveFocusFromTerminal(state, reverse) {
    if (!reverse) {
        const firstControl = [state.fontMinusBtn, state.fontPlusBtn, state.sizeSelect]
            .find((element) => element && !element.disabled);
        (firstControl || state.terminalFooter)?.focus();
        return true;
    }

    const focusableElements = Array.from(document.querySelectorAll(FOCUSABLE_ELEMENT_SELECTOR))
        .filter((element) => element.getClientRects().length > 0);
    const activeIndex = focusableElements.indexOf(document.activeElement);
    for (let index = activeIndex - 1; index >= 0; index--) {
        const candidate = focusableElements[index];
        if (!state.host?.contains(candidate)) {
            candidate.focus();
            return true;
        }
    }

    return false;
}

function attachTerminalFocusNavigation(state, term) {
    term.attachCustomKeyEventHandler((event) => {
        if (event.key !== 'F6') {
            return true;
        }

        if (event.type === 'keydown' && moveFocusFromTerminal(state, event.shiftKey)) {
            event.preventDefault();
            event.stopPropagation();
        }

        // Returning false prevents xterm from forwarding F6 to the PTY. When
        // there is no previous dashboard control, leaving the event's default
        // action intact lets the browser apply its own Shift+F6 navigation.
        return false;
    });
}

function safeFit(state) {
    const term = state.term;
    const before = term ? { cols: term.cols, rows: term.rows, fontSize: term.options?.fontSize } : null;
    try { state.fitAddon?.fit(); } catch { /* ignore — happens during teardown */ }
    if (window.__aspireTerminalDebug) {
        const after = term ? { cols: term.cols, rows: term.rows, fontSize: term.options?.fontSize } : null;
        console.log('[TERMDIAG] safeFit', {
            before, after,
            currentFontPx: state.currentFontPx,
            fitFontPx: state.fitFontPx,
            sizeMode: state.sizeMode,
            avail: getAvailableBodySpace(state),
            isPrimary: !!state.client?.isPrimary,
            producerDims: state.client ? { w: state.client.width, h: state.client.height } : null,
        });
    }
}

function updateTerminalControls(state) {
    const snapshot = buildToolbarSnapshot(state);

    if (state.fontDisplay) {
        state.fontDisplay.textContent = `${state.currentFontPx}px`;
    }
    if (state.fontMinusBtn) {
        state.fontMinusBtn.disabled = !snapshot.fontControlsEnabled || state.currentFontPx <= MIN_FONT_PX;
    }
    if (state.fontPlusBtn) {
        state.fontPlusBtn.disabled = !snapshot.fontControlsEnabled || state.currentFontPx >= MAX_FONT_PX;
    }

    if (!state.sizeSelect) return;

    const cols = state.term?.cols | 0;
    const rows = state.term?.rows | 0;
    const previousCurrentOption = state.sizeSelect.querySelector('option[data-current-dimensions]');
    if (previousCurrentOption) {
        previousCurrentOption.remove();
    }
    if (snapshot.sizeKey !== 'auto' &&
        !state.sizeSelect.querySelector(`option[value="${snapshot.sizeKey}"]`)) {
        const currentOption = document.createElement('option');
        currentOption.value = snapshot.sizeKey;
        currentOption.textContent = `${state.fixedDims.cols}×${state.fixedDims.rows}`;
        currentOption.dataset.currentDimensions = '';
        state.sizeSelect.appendChild(currentOption);
    }
    const fitOption = state.sizeSelect.querySelector('option[value="auto"]');
    if (fitOption) {
        fitOption.textContent = cols > 0 && rows > 0 && snapshot.sizeKey === 'auto'
            ? `${state.labels.fit} (${cols} × ${rows})`
            : state.labels.fit;
    }
    state.sizeSelect.value = snapshot.sizeKey;
    state.sizeSelect.disabled = !snapshot.sizeSelectEnabled;
}

const FRAME_BORDER_PX = 2;
// CSS `padding` on #terminal-body — kept in sync with the value in the
// injected stylesheet. box-sizing is border-box, so the content area
// xterm actually renders into is smaller than the outer body box by
// TERMINAL_BODY_PADDING_PX * 2 on each axis. getAvailableBodySpace
// returns the xterm-content area (padding subtracted) so callers can
// pass it straight to computeOptimalFont / fit(); fit-mode's body-pin
// and pinBodyToNatural add the padding back when they set the outer
// body dimensions.
//
// Chromeless terminals (the dock) drop both the border and the padding
// in CSS, so they carry zeroed copies on state — always read the metrics
// through these helpers rather than the constants directly.
const TERMINAL_BODY_PADDING_PX = 6;
function frameBorderPx(state) {
    return state.chromeless ? 0 : FRAME_BORDER_PX;
}
function bodyPaddingPx(state) {
    return state.chromeless ? 0 : TERMINAL_BODY_PADDING_PX;
}
function getAvailableBodySpace(state) {
    const titlebarH = state.terminalTitlebar ? state.terminalTitlebar.offsetHeight : 0;
    const border = frameBorderPx(state);
    const padding = bodyPaddingPx(state);
    const footerH = state.terminalFooter ? state.terminalFooter.offsetHeight : 0;
    const stageW = state.terminalContainer ? state.terminalContainer.clientWidth : 0;
    const stageH = state.terminalContainer ? state.terminalContainer.clientHeight : 0;
    const outerW = Math.max(0, stageW - border * 2);
    const outerH = Math.max(0, stageH - titlebarH - footerH - border * 2);
    return {
        width: Math.max(0, outerW - padding * 2),
        height: Math.max(0, outerH - padding * 2),
    };
}

// A secondary peer displays the producer's grid rather than choosing its own.
// Record that grid as fixed sizing state so a later keyboard-driven promotion
// keeps the existing resolution. Only an explicit footer action switches back
// to Fit or selects another preset.
function adoptProducerDimensions(state) {
    const client = state.client;
    if (!client || client.isPrimary || client.width <= 0 || client.height <= 0) {
        return;
    }

    state.sizeMode = 'fixed';
    state.fixedDims = { cols: client.width, rows: client.height };
}

// Sizes the xterm display based on the current role and (in primary
// mode) the current sizing mode. See docs/muxer-learnings.md §3.
//
//  - Secondary: lock the xterm grid to producer's cols×rows and pick
//    the largest integer font whose rendered grid fits the available
//    stage. Pin #terminal-body to the natural rendered dims so the
//    frame card hugs the grid (letterboxing appears in the stage on
//    whichever axis has spare room). This is structurally the same as
//    primary fixed-mode with fixedDims == producer dims, minus the
//    resize broadcast — see the header comment for why we don't use
//    CSS transform: scale() here.
//
//  - Primary, font-driven: pin #terminal-body to available stage, run
//    fitAddon.fit() — grid grows/shrinks to fill at the user's chosen
//    font size. term.onResize → client.sendResize broadcasts to producer.
//
//  - Primary, fixed: cols×rows locked to user's preset; compute the
//    largest font that lets that grid fit, set fontSize, term.resize
//    back to the chosen dims, pin #terminal-body to the natural rendered
//    dims so the frame card hugs the chosen grid (grey gradient stage
//    shows around it as letterboxing).
function applyRoleAwareLayout(state) {
    const term = state.term;
    const fitAddon = state.fitAddon;
    if (!term || !fitAddon) return;

    const root = term.element;
    if (!root) return;
    const body = root.parentElement;
    if (!body) return;

    // Bail when the terminal container has been laid out to zero — most
    // commonly because ConsoleLogs flipped this view to display:none while
    // Console is active. Running the layout at zero would pin body.style
    // width/height to 0px (fixed mode) or resize the xterm grid to 1x1
    // (fit mode), and neither necessarily gets reversed when the browser
    // relayouts the container back to a real size. ConsoleLogs re-invokes
    // refreshLayout on the way back to Terminal view, so we recover with
    // a real size then.
    const { width: probeW, height: probeH } = getAvailableBodySpace(state);
    if (probeW <= 0 || probeH <= 0) return;

    // Bump generation: any RAF callbacks queued by prior layout calls
    // become stale and will bail when they run.
    const generation = ++state.layoutGeneration;

    const haveProducerDims = !!state.client && state.client.width > 0 && state.client.height > 0;
    // Chromeless terminals always take the font-driven path. The secondary
    // branch below locks the grid to the producer's dims and shrinks the
    // font until that grid fits, which in a short, wide dock pane collapses
    // an 80x24 producer down to ~8px text letterboxed into a fraction of
    // the width. The dock wants the opposite: dashboard-sized text and a
    // grid that fills the pane, so it fits locally and (once primary)
    // pushes the resulting dims back to the producer.
    const isSecondary = !state.chromeless && !!state.client && !state.client.isPrimary && haveProducerDims;
    const availableW = probeW;
    const availableH = probeH;

    if (!isSecondary) {
        // Primary, no-primary, or pre-handshake: clear any leftover
        // .xterm inline styling so it flows naturally inside body.
        if (root.style.transform || root.style.width || root.style.height) {
            root.style.transform = '';
            root.style.transformOrigin = '';
            root.style.width = '';
            root.style.height = '';
        }

        if (state.sizeMode === 'font') {
            // Secondary layout temporarily replaces currentFontPx with the
            // font that fits the producer grid. Restore the user's Fit-mode
            // font when an explicit sizing action promotes this peer.
            state.currentFontPx = state.fitFontPx;
        }

        if (state.sizeMode === 'fixed' && state.fixedDims) {
            const optFont = computeOptimalFont(state, state.fixedDims.cols, state.fixedDims.rows, availableW, availableH);
            if (term.options.fontSize !== optFont) {
                term.options.fontSize = optFont;
                forceFontRemeasure(term);
            }
            state.currentFontPx = optFont;
            if (term.cols !== state.fixedDims.cols || term.rows !== state.fixedDims.rows) {
                try { term.resize(state.fixedDims.cols, state.fixedDims.rows); } catch { /* ignore */ }
            }
            const expectedCols = state.fixedDims.cols;
            const expectedRows = state.fixedDims.rows;
            requestAnimationFrame(() => {
                if (generation !== state.layoutGeneration) return;
                if (state.sizeMode !== 'fixed' || !state.fixedDims) return;
                if (state.fixedDims.cols !== expectedCols || state.fixedDims.rows !== expectedRows) return;
                pinBodyToNatural(state, root, body);
                refineFontAfterCalibration(state, generation, expectedCols, expectedRows,
                    () => state.sizeMode === 'fixed' && state.fixedDims &&
                          state.fixedDims.cols === expectedCols && state.fixedDims.rows === expectedRows);
            });
        } else {
            // Font-driven: pin body to fill the pane (content + padding on
            // each side, since body is border-box); fit() picks cols×rows
            // for the padded content area.
            const pad = bodyPaddingPx(state);
            const bodyW = `${availableW + pad * 2}px`;
            const bodyH = `${availableH + pad * 2}px`;
            if (body.style.width !== bodyW || body.style.height !== bodyH) {
                body.style.width = bodyW;
                body.style.height = bodyH;
            }
            if (term.options.fontSize !== state.currentFontPx) {
                term.options.fontSize = state.currentFontPx;
                forceFontRemeasure(term);
            }
            safeFit(state);
        }
        notifyToolbar(state);
        return;
    }

    // Secondary: lock grid to producer dims, pick the largest integer
    // font whose rendered grid fits, then hug the frame to the natural
    // rendered size. No CSS transform — see the header comment for why.
    // This is intentionally the same shape as primary fixed-mode above,
    // minus the resize broadcast (secondary never drives the PTY).
    const producerCols = state.client.width;
    const producerRows = state.client.height;
    const optFont = computeOptimalFont(state, producerCols, producerRows, availableW, availableH);
    if (term.options.fontSize !== optFont) {
        term.options.fontSize = optFont;
        forceFontRemeasure(term);
    }
    state.currentFontPx = optFont;
    if (term.cols !== producerCols || term.rows !== producerRows) {
        try { term.resize(producerCols, producerRows); } catch { /* ignore */ }
    }
    requestAnimationFrame(() => {
        if (generation !== state.layoutGeneration) return;
        // Bail if role/producer dims changed while we were queued.
        if (!state.client || state.client.isPrimary) return;
        if (state.client.width !== producerCols || state.client.height !== producerRows) return;
        pinBodyToNatural(state, root, body);
        refineFontAfterCalibration(state, generation, producerCols, producerRows,
            () => !!state.client && !state.client.isPrimary &&
                  state.client.width === producerCols && state.client.height === producerRows);
    });
    notifyToolbar(state);
}

// On the very first calibrated render, computeOptimalFont bails out with
// state.currentFontPx (the default 13px) because cellWRatio/cellHRatio
// are still zero — those get seeded by calibrateRatios inside
// pinBodyToNatural, which runs one RAF *after* the initial layout pass.
// Result: the terminal opens at default font and only snaps to the
// right size when a ResizeObserver tick (window resize, sidebar collapse)
// re-drives layout.
//
// Once pinBodyToNatural has run, re-measure and recompute. If the
// optimal font moved (typical on first open), adjust fontSize in place
// and re-pin. We don't call applyRoleAwareLayout recursively because
// that would bump generation and could stack under fast triggers; a
// direct in-place adjustment converges in a single extra frame because
// xterm's cell metrics per font-px are stable across small font deltas.
function refineFontAfterCalibration(state, generation, cols, rows, stillApplicable) {
    const term = state.term;
    if (!term || !term.element) return;
    const root = term.element;
    const body = root.parentElement;
    if (!body) return;
    const fresh = getAvailableBodySpace(state);
    if (fresh.width <= 0 || fresh.height <= 0) return;
    const refined = computeOptimalFont(state, cols, rows, fresh.width, fresh.height);
    if (refined === term.options.fontSize) return;
    term.options.fontSize = refined;
    forceFontRemeasure(term);
    state.currentFontPx = refined;
    requestAnimationFrame(() => {
        if (generation !== state.layoutGeneration) return;
        if (!stillApplicable()) return;
        pinBodyToNatural(state, root, body);
        notifyToolbar(state);
    });
}

function pinBodyToNatural(state, root, body) {
    if (!root || !body) return;
    const screenEl =
        root.querySelector('.xterm-screen') ||
        root.querySelector('canvas.xterm-text-layer') ||
        root;
    const w = screenEl.offsetWidth;
    const h = screenEl.offsetHeight;
    if (w > 0 && h > 0) {
        // body is border-box with padding, so pin the outer size to
        // (screen dims + padding on each side) — the content area then
        // matches the xterm-screen dims exactly.
        const pad = bodyPaddingPx(state);
        const bodyW = `${w + pad * 2}px`;
        const bodyH = `${h + pad * 2}px`;
        if (body.style.width !== bodyW || body.style.height !== bodyH) {
            body.style.width = bodyW;
            body.style.height = bodyH;
        }
    }
    calibrateRatios(state);
}

// Stores cell width/height per CSS px of font size, derived from the
// currently rendered .xterm-screen. Refreshed after every render so
// fixed-mode font calculations stay accurate as xterm rounds cell
// sizes to integer pixels per font px.
function calibrateRatios(state) {
    const term = state.term;
    if (!term || !term.element) return;
    const screenEl = term.element.querySelector('.xterm-screen');
    if (!screenEl) return;
    const w = screenEl.offsetWidth;
    const h = screenEl.offsetHeight;
    const fs = term.options.fontSize || state.currentFontPx;
    if (w > 0 && h > 0 && term.cols > 0 && term.rows > 0 && fs > 0) {
        const newW = (w / term.cols) / fs;
        const newH = (h / term.rows) / fs;
        // Guard against transient stale readings. When fontSize was just
        // changed (e.g. fit→fixed switch that jumped 13→26), xterm's DOM
        // may not have re-rendered yet, so .xterm-screen still reflects
        // the *old* fontSize's cell metrics. Dividing that stale pixel
        // width by the new fontSize yields a ratio ~half of the true
        // value. That corrupt ratio then feeds computeOptimalFont, which
        // picks a wildly wrong font for the target grid. See the
        // term.onResize handler in initTerminal for the matching
        // RAF-deferred calibration guard.
        //
        // Heuristic: once we have a plausible baseline, reject any new
        // sample that swings by more than 40% in either direction. Real
        // xterm cell metrics per fontSize are stable across small font
        // deltas (that's the whole reason we cache a ratio) so a 40%
        // jump is diagnostic of a stale-render sample, not a real change.
        const CALIBRATION_JUMP_TOLERANCE = 0.4;
        const withinTolerance = (oldV, newV) => {
            if (oldV <= 0) return true;
            const delta = Math.abs(newV - oldV) / oldV;
            return delta <= CALIBRATION_JUMP_TOLERANCE;
        };
        if (withinTolerance(state.cellWRatio, newW) && withinTolerance(state.cellHRatio, newH)) {
            state.cellWRatio = newW;
            state.cellHRatio = newH;
        }
    }
}

function computeOptimalFont(state, cols, rows, availW, availH) {
    if (state.cellWRatio <= 0 || state.cellHRatio <= 0) return state.currentFontPx;
    if (cols <= 0 || rows <= 0 || availW <= 0 || availH <= 0) return state.currentFontPx;
    const fsW = availW / (cols * state.cellWRatio);
    const fsH = availH / (rows * state.cellHRatio);
    const fs = Math.floor(Math.min(fsW, fsH));
    return Math.max(MIN_FONT_PX, Math.min(MAX_FONT_PX, fs));
}

// xterm 5.5.0 only reliably re-measures cell metrics on fontFamily
// *change* — setting term.options.fontSize alone can leave stale cell
// dimensions in the renderer, so a subsequent fitAddon.fit() divides
// the available space by the old cell size and picks the wrong grid.
// Bouncing fontFamily forces the renderer to re-measure with the
// current fontSize. See the document.fonts.ready handler in
// initTerminal for the same trick applied to late font loads.
function forceFontRemeasure(term) {
    if (!term) return;
    try {
        const family = term.options.fontFamily;
        term.options.fontFamily = 'monospace';
        term.options.fontFamily = family;
    } catch { /* ignore — term may be disposed */ }
}

function setFontSize(state, newSize) {
    newSize = Math.max(MIN_FONT_PX, Math.min(MAX_FONT_PX, newSize));
    if (newSize === state.currentFontPx && state.sizeMode === 'font') return;
    state.currentFontPx = newSize;
    // Preserve the caller's requested size as the "Fit-mode font" so the
    // toolbar can show what Fit would produce even after a later fixed
    // preset overwrites currentFontPx with an auto-calculated size.
    state.fitFontPx = newSize;
    state.sizeMode = 'font';
    state.fixedDims = null;
    if (state.term) {
        state.term.options.fontSize = state.currentFontPx;
        forceFontRemeasure(state.term);
    }
    applyRoleAwareLayout(state);
}

function setSizeMode(state, mode, dims) {
    if (window.__aspireTerminalDebug) {
        console.log('[TERMDIAG] setSizeMode', {
            requested: { mode, dims },
            currentSizeMode: state.sizeMode,
            currentFontPx: state.currentFontPx,
            fitFontPx: state.fitFontPx,
            termFontSize: state.term?.options?.fontSize,
            termCols: state.term?.cols,
            termRows: state.term?.rows,
            cellWRatio: state.cellWRatio,
            cellHRatio: state.cellHRatio,
            isPrimary: !!state.client?.isPrimary,
            producer: state.client ? { w: state.client.width, h: state.client.height } : null,
        });
    }
    if (mode === state.sizeMode &&
        ((mode === 'font') ||
         (mode === 'fixed' && dims && state.fixedDims &&
          dims.cols === state.fixedDims.cols && dims.rows === state.fixedDims.rows))) {
        if (window.__aspireTerminalDebug) {
            console.log('[TERMDIAG] setSizeMode early-return');
        }
        return;
    }
    state.sizeMode = mode;
    state.fixedDims = mode === 'fixed' ? dims : null;
    if (mode === 'font') {
        state.currentFontPx = state.fitFontPx;
    }
    applyRoleAwareLayout(state);
    if (window.__aspireTerminalDebug) {
        console.log('[TERMDIAG] setSizeMode after layout', {
            currentFontPx: state.currentFontPx,
            termFontSize: state.term?.options?.fontSize,
            termCols: state.term?.cols,
            termRows: state.term?.rows,
        });
    }
}

// Updates the in-frame controls and, when a host observer is registered,
// pushes a state snapshot to .NET. Observer notifications are RAF-coalesced
// and change-detected because layout callbacks can fire in rapid bursts.
function notifyToolbar(state) {
    updateTerminalControls(state);
    if (state._toolbarFlushPending) return;
    state._toolbarFlushPending = true;
    requestAnimationFrame(() => {
        state._toolbarFlushPending = false;
        flushToolbarState(state);
    });
}

function flushToolbarState(state) {
    if (!state.dotNetRef) return;

    const snapshot = buildToolbarSnapshot(state);

    // Skip the .NET round trip if nothing meaningful changed. Cheap
    // shallow stringify is fine — snapshot is small and flat.
    const serialized = JSON.stringify(snapshot);
    if (serialized === state._lastToolbarJson) return;
    state._lastToolbarJson = serialized;

    try {
        state.dotNetRef.invokeMethodAsync('OnTerminalStateChanged', snapshot);
    } catch (e) {
        dbg(state, 'notifyToolbar: invoke failed', { error: e?.message });
    }
}

function buildToolbarSnapshot(state) {
    const client = state.client;
    const term = state.term;

    let status;
    let canTakeControl = false;
    let isPrimary = false;

    if (!client || client.peerId === null) {
        status = 'connecting';
    } else if (client.isPrimary) {
        status = 'primary';
        isPrimary = true;
    } else if (client.primaryPeerId === null) {
        status = 'no-primary';
        canTakeControl = true;
    } else {
        status = 'viewer';
        canTakeControl = true;
    }

    const sizeKey = state.sizeMode === 'fixed' && state.fixedDims
        ? `${state.fixedDims.cols}x${state.fixedDims.rows}`
        : 'auto';

    return {
        terminalId: state.id,
        // Generation lets the .NET side discard stale snapshots that arrive
        // after the JS terminal was disposed / replaced by another resource.
        generation: state.reconnect.generation,
        status,
        connected: !!client && client.peerId !== null,
        isPrimary,
        canTakeControl,
        sizeMode: state.sizeMode,
        sizeKey,
        fontPx: state.currentFontPx,
        // Sizing controls are enabled whenever this tab is primary or could
        // become primary on demand. A font action explicitly switches fixed
        // sizing to Fit mode before applying the requested font size.
        fontControlsEnabled: isPrimary || canTakeControl,
        sizeSelectEnabled: isPrimary || canTakeControl,
        cols: term && term.cols ? term.cols : 0,
        rows: term && term.rows ? term.rows : 0,
    };
}

// "Take control" handler. RequestPrimary at our current grid dims so
// the producer resizes the PTY to match what we just laid out.
function takePrimary(state) {
    const client = state.client;
    const term = state.term;
    if (!client || !term || !state.fitAddon) return;

    if (term.element) {
        term.element.style.transform = '';
        term.element.style.transformOrigin = '';
        term.element.style.width = '';
        term.element.style.height = '';
        const body = term.element.parentElement;
        if (body) {
            body.style.width = '';
            body.style.height = '';
        }
    }
    applyRoleAwareLayout(state);
    dbg(state, 'takePrimary', { cols: term.cols, rows: term.rows });
    try {
        client.requestPrimary(term.cols, term.rows);
    } catch (e) {
        dbg(state, 'takePrimary: failed', { error: e?.message });
    }
}

// Reads the dashboard's base type-ramp size so a chromeless terminal renders
// at the same scale as the rest of the UI instead of auto-shrinking to fit a
// producer grid. Fluent exports --type-ramp-base-font-size on the document
// root; the clamp keeps a nonsense token value from producing an unreadable
// or absurd grid.
function resolveDashboardFontPx() {
    try {
        const raw = getComputedStyle(document.documentElement).getPropertyValue('--type-ramp-base-font-size');
        const parsed = Number.parseFloat(raw);
        if (Number.isFinite(parsed) && parsed > 0) {
            return Math.min(24, Math.max(9, Math.round(parsed)));
        }
    } catch { /* ignore — fall through to the default */ }
    return DEFAULT_FONT_PX;
}

// `options` is optional: { chromeless: bool, ...control labels }. Chromeless
// drops the frame, titlebar, footer and padding so only the xterm grid shows
// (used by the terminal dock and detached terminal windows, which supply their
// own chrome).
export async function initTerminal(element, wsUrl, dotNetRef, options) {
    await ensureXtermLoaded();

    const chromeless = !!options?.chromeless;
    const initialFontPx = chromeless ? resolveDashboardFontPx() : DEFAULT_FONT_PX;

    const id = nextId++;
    const state = {
        id,
        client: null,
        term: null,
        fitAddon: null,
        element,
        wsUrl,
        labels: { ...DEFAULT_CONTROL_LABELS, ...(options || {}) },
        // Optional Blazor host observer for consumers that need terminal
        // state beyond the controls rendered directly in the frame.
        dotNetRef: dotNetRef || null,
        utf8Decoder: new TextDecoder('utf-8', { fatal: false }),
        reconnect: {
            enabled: true,
            attempts: 0,
            timer: null,
            generation: 0,
        },
        // Layout / sizing state (per-instance — we never use globals).
        chromeless,
        // Chromeless terminals fill whatever space the dock pane or detached
        // window gives them at the dashboard's font size, so they start in Fit
        // mode. Everything else starts at the default fixed resolution and only
        // leaves it when the user explicitly picks another preset.
        sizeMode: chromeless ? 'font' : 'fixed',
        fixedDims: chromeless ? null : { cols: DEFAULT_TERMINAL_COLS, rows: DEFAULT_TERMINAL_ROWS },
        currentFontPx: initialFontPx,
        // Font size that "Fit" mode uses, tracked separately from
        // currentFontPx because fixed-preset layout overwrites the latter
        // with the auto-calculated optimal font. Preserving the user's last
        // font-mode font here lets setSizeMode('font') restore it when the
        // user flips back to Fit.
        fitFontPx: initialFontPx,
        cellWRatio: 0,
        cellHRatio: 0,
        layoutGeneration: 0,
        // Toolbar push state. _toolbarFlushPending coalesces bursts via RAF;
        // _lastToolbarJson lets us short-circuit no-op snapshots so we don't
        // round-trip to .NET on every layout/resize tick.
        _toolbarFlushPending: false,
        _lastToolbarJson: null,
        // DOM refs filled in by buildChrome.
        host: null,
        terminalContainer: null,
        terminalFrame: null,
        terminalTitlebar: null,
        titleText: null,
        sizeSelect: null,
        terminalBody: null,
        terminalFooter: null,
        terminalFocusHint: null,
        fontMinusBtn: null,
        fontDisplay: null,
        fontPlusBtn: null,
    };

    // Build the chrome BEFORE creating the xterm — term.open(body)
    // needs the body element to exist.
    buildChrome(state);

    // Preload Cascadia Mono NF BEFORE constructing the Terminal. xterm
    // measures cell metrics (width and height in CSS px) exactly once at
    // construction time via its hidden .xterm-char-measure-element. Those
    // metrics back not just rendering but also mouse → cell hit-testing
    // (selection, click reporting). If the woff2 hasn't entered the
    // FontFace cache by the time `new Terminal()` runs, xterm calibrates
    // against the fallback (Menlo/Consolas) and the entire grid — visuals
    // AND mouse mapping — stays anchored to those slightly-different
    // metrics. Awaiting document.fonts.load with the actual font-size we
    // are about to use forces the woff2 to be ready before construction.
    // We still have the post-load bounce below as a defense in depth for
    // the case where preload fails (offline, asset 404).
    if (document.fonts && typeof document.fonts.load === 'function') {
        try {
            await document.fonts.load(`${state.currentFontPx}px "Cascadia Mono NF"`);
        } catch { /* ignore — fallback stack continues to render */ }
    }

    const FitAddon = window.FitAddon.FitAddon;
    const fitAddon = new FitAddon();
    const term = new window.Terminal({
        cursorBlink: true,
        fontSize: state.currentFontPx,
        fontFamily: '"Cascadia Mono NF", "Cascadia Mono", Menlo, Consolas, "DejaVu Sans Mono", monospace',
        // HMP1 does not currently synchronize scrollback across consumer
        // reconnects — the producer's StateSync only repaints the visible
        // viewport. The reconnect path below calls term.reset() on every
        // new HMP1 session so the StateSync repaints into a clean buffer
        // with default modes; that also resets this scrollback.
        scrollback: 10000,
        theme: {
            background: '#0d1117',
            foreground: '#c9d1d9',
            cursor: '#58a6ff',
            selectionBackground: '#1f6feb55',
        },
        allowProposedApi: true,
    });

    term.loadAddon(fitAddon);
    term.open(state.terminalBody);

    // Let Ctrl+` reach the document so the global keydown listener can toggle the terminal dock. Returning false
    // tells xterm not to handle the event; without this xterm swallows it and the dock cannot be closed from a
    // focused terminal. Everything else is still handled by xterm as usual.
    term.attachCustomKeyEventHandler((e) => {
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === '`' || e.key === '~' || e.code === 'Backquote')) {
            return false;
        }
        return true;
    });

    state.term = term;
    state.fitAddon = fitAddon;
    attachTerminalFocusNavigation(state, term);

    const helperTextArea = state.terminalBody.querySelector('.xterm-helper-textarea');
    if (helperTextArea && state.terminalFocusHint) {
        helperTextArea.setAttribute('aria-keyshortcuts', 'F6 Shift+F6');
        helperTextArea.setAttribute('aria-describedby', state.terminalFocusHint.id);
    }

    // Defense in depth: if Cascadia hadn't entered the FontFace cache
    // by the time we constructed Terminal (preload above failed/timed
    // out, or the browser deferred the load), force xterm to re-measure
    // when the font finally lands. xterm only re-measures on fontFamily
    // *change*, so bounce through 'monospace' and back. Then refit and
    // recalibrate so cols/rows AND the mouse hit map agree with the
    // new cell metrics — without the fit the renderer repaints with
    // the new glyphs but pointer events still map to the old grid.
    if (document.fonts && typeof document.fonts.ready?.then === 'function') {
        document.fonts.ready
            .then(() => {
                if (state.term !== term) return;
                try {
                    term.options.fontFamily = 'monospace';
                    term.options.fontFamily = '"Cascadia Mono NF", "Cascadia Mono", Menlo, Consolas, "DejaVu Sans Mono", monospace';
                    try { fitAddon.fit(); } catch { /* container not laid out yet */ }
                    calibrateRatios(state);
                    applyRoleAwareLayout(state);
                } catch { /* ignore — xterm disposed mid-flight */ }
            })
            .catch(() => { /* font load failed; fallback stack continues to render */ });
    }

    // Defer the initial layout one frame so xterm has rendered the cell
    // grid — calibrateRatios needs the rendered .xterm-screen.
    requestAnimationFrame(() => {
        calibrateRatios(state);
        applyRoleAwareLayout(state);
        updateTerminalControls(state);
    });

    // OSC 0 / OSC 2 / OSC 1 — terminal apps push window/icon titles via
    // these escape sequences. xterm.js parses them and fires
    // onTitleChange with the new string.
    term.onTitleChange((newTitle) => {
        if (state.titleText) {
            state.titleText.textContent = newTitle || 'terminal';
        }
    });

    // term.onResize fires whenever fitAddon.fit() OR a manual term.resize()
    // changes the xterm grid. Forward to the producer via sendResize, but
    // Hmp1Client.sendResize() silently no-ops when we're not primary, so
    // viewers' fit() calls don't disturb the producer. Refresh the live
    // dimensions selector and recalibrate ratios so future fixed-mode font
    // calculations stay accurate.
    //
    // Recalibration is deferred one RAF because xterm dispatches onResize
    // *before* it re-renders .xterm-screen; measuring offsetWidth here
    // would divide the old rendered width by the new cols count and yield
    // a cellWRatio ~half of the true value. That in turn made the Fit
    // dimensions report roughly double the real cols×rows.
    term.onResize(({ cols, rows }) => {
        if (state.client) state.client.sendResize(cols, rows);
        updateTerminalControls(state);
        requestAnimationFrame(() => {
            if (state.term !== term) return;
            calibrateRatios(state);
            notifyToolbar(state);
        });
    });

    // User input auto-promotes to primary. There is no explicit "Take
    // control" button, so we rely on the same auto-promote path as font/size
    // changes: if the viewer types (or pastes, or hits Enter), they take
    // primary before the input goes out. Server drops non-primary input, so
    // promoting first ensures the keystroke lands. No-ops when we're already
    // primary or the client isn't connected yet.
    term.onData((data) => {
        if (!state.client) return;
        maybeAutoPromote(state);
        state.client.sendInput(textEncoder.encode(data));
    });

    // Re-layout on container size change (window resize, sidebar collapse,
    // dashboard layout changes, devtools opening, …). The role-aware
    // layout function handles primary fit + secondary scale uniformly.
    const resizeObserver = new ResizeObserver(() => applyRoleAwareLayout(state));
    resizeObserver.observe(state.terminalContainer);

    state._resizeObserver = resizeObserver;
    terminals.set(id, state);

    // Connect HMP1 client.
    connectClient(state, wsUrl);

    dbg(state, 'initTerminal: created', { wsUrl });
    return id;
}

function connectClient(state, wsUrl) {
    // Cancel any pending reconnect timer and bump the generation so that
    // late callbacks from any prior client no-op rather than racing with
    // this new connection.
    cancelPendingReconnect(state);
    state.reconnect.generation++;
    const myGeneration = state.reconnect.generation;
    state.wsUrl = wsUrl;

    dbg(state, 'connectClient', { generation: myGeneration, attempts: state.reconnect.attempts, hadPriorClient: !!state.client });

    // Tear down any in-flight client without firing its onClose (we don't
    // want it to schedule its own reconnect on top of ours). Null the
    // hooks first so an in-flight ws.onclose doesn't dispatch.
    if (state.client) {
        const stale = state.client;
        stale.onOpen = null;
        stale.onScreenBytes = null;
        stale.onHello = null;
        stale.onRoleChange = null;
        stale.onPeerJoin = null;
        stale.onPeerLeave = null;
        stale.onResize = null;
        stale.onExit = null;
        stale.onClose = null;
        try { stale.close(); } catch { /* ignore */ }
        state.client = null;
    }

    // Reset the UTF-8 decoder so any tail bytes from the previous stream
    // don't bleed into the next one.
    state.utf8Decoder = new TextDecoder('utf-8', { fatal: false });

    // Hard-reset xterm (RIS) before the new HMP1 handshake. We MUST use
    // term.reset() rather than term.clear(): clear() only wipes the
    // visible buffer, leaving DEC private mode state intact (alternate
    // screen ?1049, mouse tracking ?1000/?1002/?1003/?1006, focus events
    // ?1004, bracketed paste ?2004, app cursor keys, scroll region,
    // cursor shape, etc). If the prior connection had a TUI running and
    // the WS was reset (e.g. a slow-consumer eviction under load),
    // xterm.js would carry those modes into the next session — so when
    // the producer's StateSync paints a fresh snapshot the viewer ends
    // up wedged: cursor in alt-screen while the producer is on the
    // primary buffer, mouse events swallowed even after the TUI exited,
    // etc. reset() drops everything back to defaults so the StateSync
    // suffix can authoritatively re-enable only the modes that are
    // actually live on the producer.
    try { state.term.reset(); } catch { /* ignore */ }

    // Update toolbar to "connecting…" while the new handshake completes.
    notifyToolbar(state);

    const client = new Hmp1Client({
        url: wsUrl,
        // Friendly-name shown in upstream's roster. Includes a short
        // tab-id suffix so multiple browser tabs of the same resource are
        // distinguishable in CLI viewers connected to the same upstream.
        displayName: `aspire-dashboard-${state.id}`,
        // Don't auto-snatch primary just by opening a tab; the user
        // takes explicit action via the "Take control" button.
        defaultRole: 'secondary',
    });

    client.onOpen = () => {
        if (myGeneration !== state.reconnect.generation) {
            dbg(state, 'client.onOpen: stale generation, ignoring', { my: myGeneration, current: state.reconnect.generation });
            return;
        }
        dbg(state, 'client.onOpen', { generation: myGeneration });
        // Connection is healthy. Reset the backoff so the next disconnect
        // gets a snappy first retry rather than picking up where the prior
        // attempt left off.
        state.reconnect.attempts = 0;
    };

    client.onScreenBytes = (bytes) => {
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        // stream:true buffers partial multi-byte sequences across calls so
        // a codepoint split across HMP1 Output frames still decodes
        // correctly.
        const text = state.utf8Decoder.decode(bytes, { stream: true });
        if (text.length > 0) {
            state.term.write(text);
        }
    };

    client.onHello = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onHello', payload);
        adoptProducerDimensions(state);
        notifyToolbar(state);
        // Now that we know producer dims + role, apply layout (fits the
        // role-aware path: secondary locks-and-scales to producer dims;
        // primary fits/computes-font into the available stage).
        applyRoleAwareLayout(state);
        // Chromeless terminals size the grid from the pane rather than from
        // the producer, so those dims are only correct once we are primary
        // and can push them upstream. Unlike a resource terminal — which may
        // legitimately be driven by a CLI viewer elsewhere — a dock terminal
        // is owned by the AppHost purely to be shown here, so claiming
        // primary on attach is the expected behaviour rather than snatching
        // control from another user.
        if (state.chromeless) {
            maybeAutoPromote(state);
        }
    };

    client.onRoleChange = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onRoleChange', payload);
        adoptProducerDimensions(state);
        notifyToolbar(state);
        // Run layout FIRST so fixed-mode (if active) can resize the grid
        // to fixedDims; the resulting term.onResize will sendResize the
        // correct dims to the producer. Then send an explicit fallback
        // in case nothing changed (e.g. font-driven mode where local
        // dims already happen to match what we want broadcast).
        applyRoleAwareLayout(state);
        if (state.client && state.client.isPrimary && state.term) {
            state.client.sendResize(state.term.cols, state.term.rows);
        }
    };

    client.onPeerJoin = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onPeerJoin', payload);
    };

    client.onPeerLeave = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onPeerLeave', payload);
    };

    client.onResize = (cols, rows) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onResize', { cols, rows });
        adoptProducerDimensions(state);
        // Producer's grid changed (only happens via primary's Resize).
        // For secondaries this is the trigger to re-fit the frame to
        // the new producer dims.
        applyRoleAwareLayout(state);
    };

    client.onExit = (code) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onExit', { code });
        try {
            state.term?.write(`\r\n[workload exited with code ${code}]\r\n`);
        } catch { /* ignore */ }
    };

    client.onClose = (ev) => {
        // Always log close events — this is the key forensic signal for
        // periodic-reconnect investigations. code/reason/wasClean tell
        // us who hung up and why (1000 = normal, 1006 = abnormal/no-
        // close-frame, 1011 = server error, etc.).
        const closeInfo = {
            generation: myGeneration,
            currentGeneration: state.reconnect.generation,
            stale: myGeneration !== state.reconnect.generation,
            code: ev?.code,
            reason: ev?.reason,
            wasClean: ev?.wasClean,
        };
        dbg(state, 'client.onClose', closeInfo);
        // Abnormal close (1006 = no close frame, !wasClean) is highly
        // suggestive of a transport-level kill. Surface this at warn so
        // it shows up in the default browser console without needing the
        // aspire-terminal-debug flag. Normal close (1000) under stress
        // means the proxy gracefully closed after upstream EOF — also
        // worth a one-liner to correlate with server-side pump logs.
        if (ev && (ev.code !== 1000 || !ev.wasClean)) {
            try {
                console.warn('[aspire-terminal] WS closed abnormally', closeInfo);
            } catch { /* ignore */ }
        }
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        if (!state.reconnect.enabled) {
            return;
        }
        notifyToolbar(state); // back to "connecting"
        scheduleReconnect(state);
    };

    state.client = client;
    try {
        client.connect();
    } catch (e) {
        dbg(state, 'connectClient: connect threw', { error: e?.message });
        // Treat a synchronous connect failure (e.g. malformed URL) as a
        // close — drive the reconnect loop just like a runtime drop.
        if (state.reconnect.enabled && myGeneration === state.reconnect.generation) {
            scheduleReconnect(state);
        }
    }

    return myGeneration;
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state) return 0;

    dbg(state, 'reconnectTerminal (Razor explicit)', { wsUrl });

    // Explicit reconnect (e.g. user navigated to a different replica).
    // Reset the backoff so we connect immediately rather than waiting
    // for the next pending auto-reconnect timer slot.
    state.reconnect.attempts = 0;
    return connectClient(state, wsUrl);
}

export function disposeTerminal(id) {
    const state = terminals.get(id);
    if (!state) return;

    dbg(state, 'disposeTerminal (Blazor unmount)');

    // Make absolutely sure no late callback resurrects the terminal.
    state.reconnect.enabled = false;
    cancelPendingReconnect(state);
    state.reconnect.generation++;

    // Drop the Blazor callback before tearing down so any in-flight RAF
    // notifyToolbar callback no-ops instead of invoking a disposed
    // DotNetObjectReference. The .NET side owns disposing the ref
    // itself; we just clear our pointer to it.
    state.dotNetRef = null;

    if (state._resizeObserver) {
        state._resizeObserver.disconnect();
    }
    if (state.client) {
        const stale = state.client;
        stale.onOpen = null;
        stale.onScreenBytes = null;
        stale.onHello = null;
        stale.onRoleChange = null;
        stale.onPeerJoin = null;
        stale.onPeerLeave = null;
        stale.onResize = null;
        stale.onExit = null;
        stale.onClose = null;
        try { stale.close(); } catch { /* ignore */ }
        state.client = null;
    }
    if (state.host && state.host.parentNode) {
        try { state.host.parentNode.removeChild(state.host); } catch { /* ignore */ }
    }
    if (state.term) {
        try { state.term.dispose(); } catch { /* ignore */ }
    }
    terminals.delete(id);
}

// --- Host commands -------------------------------------------------------
//
// These wrappers let a .NET host drive the same actions as the terminal's
// in-frame controls. Each is idempotent and silently no-ops if the terminal
// id is unknown or the underlying client/term isn't ready.

export function getSizePresets() {
    // Return a copy so .NET-side callers can't accidentally mutate the
    // module-level array.
    return SIZE_PRESETS.map((p) => ({ value: p.value, label: p.label, cols: p.cols, rows: p.rows }));
}

export function setFontSizeFromHost(id, newSize) {
    const state = terminals.get(id);
    if (!state || typeof newSize !== 'number') return;
    // Order matters: apply the new font (which in font-driven mode will
    // refit and update term.cols/rows) BEFORE auto-promoting. takePrimary
    // sends RequestPrimary(cols,rows) using the current term grid, so if
    // we promoted first the server would grant primary at the OLD oversize
    // grid and the producer's PTY would keep emitting frames that overflow
    // the per-peer queue and re-trigger slow-consumer eviction. By
    // resizing locally first, the promotion request itself carries the
    // smaller dims and the producer shrinks the PTY on grant.
    setFontSize(state, newSize);
    maybeAutoPromote(state);
}

export function setSizeModeFromHost(id, sizeKey) {
    const state = terminals.get(id);
    if (!state) return;
    if (!sizeKey || sizeKey === 'auto') {
        setSizeMode(state, 'font', null);
    } else {
        const preset = SIZE_PRESETS.find((p) => p.value === sizeKey);
        if (preset) {
            setSizeMode(state, 'fixed', { cols: preset.cols, rows: preset.rows });
        }
    }
    // Promote AFTER applying local sizing so RequestPrimary carries the
    // new dims (see setFontSizeFromHost above for the rationale).
    maybeAutoPromote(state);
}

function maybeAutoPromote(state) {
    const client = state.client;
    if (!client || client.peerId === null) return;
    if (client.isPrimary) return;
    takePrimary(state);
}

// Lets the .NET host query the current snapshot on demand (e.g. when
// re-attaching after a re-render). Pure: does not push to the host.
export function getToolbarState(id) {
    const state = terminals.get(id);
    if (!state) return null;
    return buildToolbarSnapshot(state);
}

// Force-pushes the current toolbar snapshot to the .NET host, bypassing
// the change-detection cache. The host calls this when its own view of
// the toolbar state has been lost (e.g. a Blazor re-render dropped the
// cached snapshot field) but the JS terminal is still live, so the cached
// "last pushed JSON" wouldn't trigger a fresh push otherwise.
export function refreshToolbarState(id) {
    const state = terminals.get(id);
    if (!state) return;
    state._lastToolbarJson = null;
    flushToolbarState(state);
}

// Triggers a layout recompute on demand. Called by the .NET host after the
// terminal element becomes visible again following a Console/Terminal view
// flip — the wrapper goes from display:none to visible, which may or may
// not trigger ResizeObserver depending on the browser's box-tree timing.
// Forcing applyRoleAwareLayout here guarantees xterm rebinds to the new
// available space immediately rather than waiting for the next external
// resize event.
export function refreshLayout(id) {
    const state = terminals.get(id);
    if (!state) return;
    applyRoleAwareLayout(state);
}
