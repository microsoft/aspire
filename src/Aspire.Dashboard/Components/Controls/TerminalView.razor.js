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
//   "font"   (Auto)  : user controls font size with +/- buttons; FitAddon
//                      picks cols×rows to fill the available stage at that
//                      font. Window resize → fit → new cols×rows broadcast.
//
//   "fixed"  (preset): user picks a grid (e.g. 80×24) from the dropdown;
//                      we compute the largest font that makes that grid
//                      fill the stage and lock cols×rows. Window resize →
//                      recompute font, cols×rows stay fixed (no broadcast).
//
// In secondary mode (someone else is primary), both control groups hide
// (.read-only) and we lock our xterm grid to the producer's cols×rows
// then CSS-scale .xterm to fit our viewport (letterboxing on whichever
// axis has spare room).
const MIN_FONT_PX = 4;
const MAX_FONT_PX = 72;
const DEFAULT_FONT_PX = 13;
const SIZE_PRESETS = [
    // NOTE: The "Auto" label is overridden on the .NET side in
    // ConsoleLogs.razor.cs (OnTerminalToolbarStateChangedAsync) using the
    // dashboard's localized resource (ConsoleLogs.resx →
    // TerminalToolbarGridSizeAuto). The English string here is only a
    // fallback for the rare case where someone consumes the SIZE_PRESETS
    // list directly from JS without going through GetSizePresetsAsync —
    // we never bind it to the UI as-is.
    { value: "auto",   label: "Auto",   cols: 0,   rows: 0  },
    { value: "80x24",  label: "80×24",  cols: 80,  rows: 24 },
    { value: "80x30",  label: "80×30",  cols: 80,  rows: 30 },
    { value: "100x30", label: "100×30", cols: 100, rows: 30 },
    { value: "132x30", label: "132×30", cols: 132, rows: 30 },
    { value: "132x50", label: "132×50", cols: 132, rows: 50 },
];

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
   * extra padding to give shadow blur space to extend.
   */
  padding: 8px;
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

.aspire-terminal-host #terminal-body {
  flex: 0 0 auto;
  position: relative;
  overflow: hidden;
  background: #0d1117;
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
//
// The status badge, "Take control" button, font controls, size dropdown
// and live dims readout that used to sit inside the chrome have been
// hoisted into the page's toolbar — see ConsoleLogs.razor for the host.
// State snapshots flow up to .NET via `state.dotNetRef` (registered at
// init time) and commands flow back in via the exported wrappers
// `takePrimary`, `setFontSize`, `setSizeModeAuto`, `setSizeModeFixed`.
//
// All lookup roots are scoped to state.host so the layout helpers can
// run in pages that might (in the future) host multiple terminals.
function buildChrome(state) {
    ensureTerminalStyles();

    const blazorElement = state.element;
    if (!blazorElement) return;

    // The Blazor element already has inline width/height: 100%. Wrap
    // it with our own host so we can apply our flex column layout
    // without disturbing whatever else the parent has set on it.
    const host = document.createElement('div');
    host.className = 'aspire-terminal-host';
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

    const titlebar = document.createElement('div');
    titlebar.id = 'terminal-titlebar';
    const titleText = document.createElement('span');
    titleText.id = 'terminal-title';
    titleText.textContent = 'terminal';
    titlebar.appendChild(titleText);

    const body = document.createElement('div');
    body.id = 'terminal-body';

    frame.append(titlebar, body);
    terminalContainer.appendChild(frame);
    host.append(pane);

    state.host = host;
    state.terminalContainer = terminalContainer;
    state.terminalFrame = frame;
    state.terminalTitlebar = titlebar;
    state.titleText = titleText;
    state.terminalBody = body;
}

function safeFit(state) {
    try { state.fitAddon?.fit(); } catch { /* ignore — happens during teardown */ }
}

const FRAME_BORDER_PX = 2;
function getAvailableBodySpace(state) {
    const titlebarH = state.terminalTitlebar ? state.terminalTitlebar.offsetHeight : 0;
    const stageW = state.terminalContainer ? state.terminalContainer.clientWidth : 0;
    const stageH = state.terminalContainer ? state.terminalContainer.clientHeight : 0;
    return {
        width: Math.max(0, stageW - FRAME_BORDER_PX * 2),
        height: Math.max(0, stageH - titlebarH - FRAME_BORDER_PX * 2),
    };
}

// Sizes the xterm display based on the current role and (in primary
// mode) the current sizing mode. See docs/muxer-learnings.md §3.
//
//  - Secondary: lock the xterm grid to producer's cols×rows, then CSS
//    transform: scale() .xterm so the rendered grid fills available
//    space without distortion. Pin #terminal-body to the SCALED visible
//    bounds so the frame card hugs the content (no empty layout space
//    around the scaled grid). Letterboxing on whichever axis has spare
//    room (preserves aspect).
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

    // Bump generation: any RAF callbacks queued by prior layout calls
    // become stale and will bail when they run.
    const generation = ++state.layoutGeneration;

    const haveProducerDims = !!state.client && state.client.width > 0 && state.client.height > 0;
    const isSecondary = !!state.client && !state.client.isPrimary && haveProducerDims;
    const { width: availableW, height: availableH } = getAvailableBodySpace(state);

    if (!isSecondary) {
        // Primary, no-primary, or pre-handshake: clear any secondary
        // pinning on .xterm so it can flow naturally inside body.
        if (root.style.transform || root.style.width || root.style.height) {
            root.style.transform = '';
            root.style.transformOrigin = '';
            root.style.width = '';
            root.style.height = '';
        }

        if (state.sizeMode === 'fixed' && state.fixedDims) {
            const optFont = computeOptimalFont(state, state.fixedDims.cols, state.fixedDims.rows, availableW, availableH);
            if (term.options.fontSize !== optFont) {
                term.options.fontSize = optFont;
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
            });
        } else {
            // Font-driven: pin body to available, fit() picks cols×rows.
            const bodyW = `${availableW}px`;
            const bodyH = `${availableH}px`;
            if (body.style.width !== bodyW || body.style.height !== bodyH) {
                body.style.width = bodyW;
                body.style.height = bodyH;
            }
            if (term.options.fontSize !== state.currentFontPx) {
                term.options.fontSize = state.currentFontPx;
            }
            safeFit(state);
        }
        notifyToolbar(state);
        return;
    }

    // Secondary lock-and-scale.
    const needsResize = term.cols !== state.client.width || term.rows !== state.client.height;
    if (needsResize) {
        try { term.resize(state.client.width, state.client.height); } catch { /* ignore */ }
    }

    // If we just resized, defer measurement to next frame so the
    // renderer can write the new .xterm-screen dims first.
    if (needsResize) {
        requestAnimationFrame(() => {
            if (generation !== state.layoutGeneration) return;
            const fresh = getAvailableBodySpace(state);
            measureAndScale(state, fresh.width, fresh.height);
        });
    } else {
        measureAndScale(state, availableW, availableH);
    }
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
        const bodyW = `${w}px`;
        const bodyH = `${h}px`;
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
        state.cellWRatio = (w / term.cols) / fs;
        state.cellHRatio = (h / term.rows) / fs;
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

function setFontSize(state, newSize) {
    newSize = Math.max(MIN_FONT_PX, Math.min(MAX_FONT_PX, newSize));
    if (newSize === state.currentFontPx && state.sizeMode === 'font') return;
    state.currentFontPx = newSize;
    state.sizeMode = 'font';
    state.fixedDims = null;
    if (state.term) state.term.options.fontSize = state.currentFontPx;
    applyRoleAwareLayout(state);
}

function setSizeMode(state, mode, dims) {
    if (mode === state.sizeMode &&
        ((mode === 'font') ||
         (mode === 'fixed' && dims && state.fixedDims &&
          dims.cols === state.fixedDims.cols && dims.rows === state.fixedDims.rows))) {
        return;
    }
    state.sizeMode = mode;
    state.fixedDims = mode === 'fixed' ? dims : null;
    applyRoleAwareLayout(state);
}

// Computes the current toolbar state snapshot and (when changed) pushes
// it up to the Blazor host so the page-level toolbar can render the
// status badge, "Take control" button, font controls, size dropdown and
// dims readout. RAF-coalesced because callers include term.onResize,
// applyRoleAwareLayout's RAF callbacks and ResizeObserver — they can
// fire in rapid bursts during window/sidebar resize. Change-detected
// so a no-op call (e.g. layout pass that produced identical dims) does
// not round-trip to .NET.
function notifyToolbar(state) {
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
        // Font/size controls are enabled whenever this tab is primary or
        // could become primary on demand. If we're not primary yet, the
        // setFontSizeFromHost / setSizeModeFromHost entry points will
        // auto-promote before applying the change so the user doesn't have
        // to click "Take control" first — this is especially important
        // after a WS reconnect, which silently drops primary status.
        // Connecting state still gates these off via canTakeControl=false.
        fontControlsEnabled: (isPrimary && state.sizeMode === 'font') || canTakeControl,
        sizeSelectEnabled: isPrimary || canTakeControl,
        cols: term && term.cols ? term.cols : 0,
        rows: term && term.rows ? term.rows : 0,
    };
}

function measureAndScale(state, availableW, availableH) {
    const term = state.term;
    if (!term || !state.client) return;
    const root = term.element;
    if (!root) return;
    const body = root.parentElement;
    if (!body) return;

    const screenEl =
        root.querySelector('.xterm-screen') ||
        root.querySelector('canvas.xterm-text-layer') ||
        root;
    const naturalWidth = screenEl.offsetWidth;
    const naturalHeight = screenEl.offsetHeight;

    if (naturalWidth <= 0 || naturalHeight <= 0 ||
        availableW <= 0 || availableH <= 0) {
        return;
    }

    const scale = Math.min(
        availableW / naturalWidth,
        availableH / naturalHeight);

    if (scale <= 0) return;

    const xtermTransform = `scale(${scale})`;
    const xtermW = `${naturalWidth}px`;
    const xtermH = `${naturalHeight}px`;
    if (root.style.transform !== xtermTransform ||
        root.style.width !== xtermW ||
        root.style.height !== xtermH) {
        root.style.transformOrigin = 'top left';
        root.style.transform = xtermTransform;
        root.style.width = xtermW;
        root.style.height = xtermH;
    }

    // Math.floor + clamp to availableW/H so we never produce a body 1px
    // wider than the stage from sub-pixel accumulation — a 1px overflow
    // re-triggers ResizeObserver in a tight loop and looks like the
    // terminal is bouncing.
    const bodyW = `${Math.min(availableW, Math.floor(naturalWidth * scale))}px`;
    const bodyH = `${Math.min(availableH, Math.floor(naturalHeight * scale))}px`;
    if (body.style.width !== bodyW || body.style.height !== bodyH) {
        body.style.width = bodyW;
        body.style.height = bodyH;
    }
}

// "Take control" handler. Clears any secondary lock-and-scale styling
// then RequestPrimary at our current grid dims so the producer resizes
// the PTY to match what we just laid out.
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

export async function initTerminal(element, wsUrl, dotNetRef) {
    await ensureXtermLoaded();

    const id = nextId++;
    const state = {
        id,
        client: null,
        term: null,
        fitAddon: null,
        element,
        wsUrl,
        // Blazor host (TerminalView) — the JS side pushes state snapshots
        // into [JSInvokable] OnTerminalStateChanged so the page-level
        // toolbar can render the status badge / take-control button /
        // font ± / size dropdown / dims readout. May be null if the
        // host opted not to receive notifications.
        dotNetRef: dotNetRef || null,
        utf8Decoder: new TextDecoder('utf-8', { fatal: false }),
        reconnect: {
            enabled: true,
            attempts: 0,
            timer: null,
            generation: 0,
        },
        // Layout / sizing state (per-instance — we never use globals).
        sizeMode: 'font',
        fixedDims: null,
        currentFontPx: DEFAULT_FONT_PX,
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
        terminalBody: null,
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

    state.term = term;
    state.fitAddon = fitAddon;

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
    // viewers' fit() calls don't disturb the producer. Push fresh dims to
    // the toolbar and recalibrate ratios so future fixed-mode font calcs
    // stay accurate.
    term.onResize(({ cols, rows }) => {
        if (state.client) state.client.sendResize(cols, rows);
        calibrateRatios(state);
        notifyToolbar(state);
    });

    term.onData((data) => {
        if (state.client) {
            state.client.sendInput(textEncoder.encode(data));
        }
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
        notifyToolbar(state);
        // Now that we know producer dims + role, apply layout (fits the
        // role-aware path: secondary locks-and-scales to producer dims;
        // primary fits/computes-font into the available stage).
        applyRoleAwareLayout(state);
    };

    client.onRoleChange = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onRoleChange', payload);
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
        // Producer's grid changed (only happens via primary's Resize).
        // For secondaries this is the trigger to re-lock-and-scale to
        // the new dims.
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
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state) return;

    dbg(state, 'reconnectTerminal (Razor explicit)', { wsUrl });

    // Explicit reconnect (e.g. user navigated to a different replica).
    // Reset the backoff so we connect immediately rather than waiting
    // for the next pending auto-reconnect timer slot.
    state.reconnect.attempts = 0;
    connectClient(state, wsUrl);
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

// --- Toolbar commands ----------------------------------------------------
//
// These wrappers let the page-level toolbar (ConsoleLogs.razor) drive the
// same actions that used to live inside the terminal's own chrome. Each
// is idempotent and silently no-ops if the terminal id is unknown or the
// underlying client/term isn't ready — JS remains authoritative, so a
// stale toolbar click can't put us into a bad state. Mode/role guards
// match the disabled-state logic in flushToolbarState; we still re-check
// here in case the .NET disabled flag hasn't reached the user's click yet.

export function getSizePresets() {
    // Return a copy so .NET-side callers can't accidentally mutate the
    // module-level array.
    return SIZE_PRESETS.map((p) => ({ value: p.value, label: p.label, cols: p.cols, rows: p.rows }));
}

export function takePrimaryFromHost(id) {
    const state = terminals.get(id);
    if (!state) return;
    takePrimary(state);
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
