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
.aspire-terminal-host {
  --aspire-term-bg: #0d1117;
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

.aspire-terminal-host .controls {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 16px;
  background: var(--aspire-term-panel);
  border-bottom: 1px solid var(--aspire-term-border);
  flex: 0 0 auto;
}
.aspire-terminal-host .controls .spacer { flex: 1; }
.aspire-terminal-host .controls button {
  background: #21262d;
  color: var(--aspire-term-fg);
  border: 1px solid var(--aspire-term-border);
  border-radius: 4px;
  padding: 4px 10px;
  font: inherit;
  cursor: pointer;
}
.aspire-terminal-host .controls button:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.aspire-terminal-host .controls button:hover:not(:disabled) {
  background: #30363d;
  border-color: #484f58;
}

.aspire-terminal-host .badge {
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  font-weight: 600;
}
.aspire-terminal-host .badge.offline    { background: #21262d; color: var(--aspire-term-fg-muted); }
.aspire-terminal-host .badge.no-primary { background: #4c331a; color: var(--aspire-term-warn); }
.aspire-terminal-host .badge.viewer     { background: #1f2a3b; color: var(--aspire-term-accent); }
.aspire-terminal-host .badge.primary    { background: #1a3d2a; color: var(--aspire-term-accent-2); }

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
   * Stage for the terminal — provides the grey gradient backdrop and
   * breathing room around the .xterm frame so the uniform blue drop-
   * shadow has space to extend on every side without being clipped
   * (shadow blur radius ~28px; padding gives 36px clearance).
   */
  padding: 36px;
  overflow: hidden;
  display: flex;
  background: linear-gradient(180deg, #1c222a 0%, #161b22 100%);
}

.aspire-terminal-host #terminal {
  /*
   * Bare host for xterm.js. Fills the inner stage area and centres its
   * single .xterm child on both axes so secondary peers (which lock
   * the grid to producer dims and apply a CSS scale transform) get
   * symmetric letterboxing on whichever axis has spare room.
   */
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}

/*
 * Terminal "card" — non-transformed wrapper around the xterm so the
 * border and drop shadow stay at fixed CSS pixel sizes regardless of
 * any CSS scale transform applied to the .xterm in secondary mode.
 */
.aspire-terminal-host #terminal-frame {
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
  background: #0d1117;
  border: 2px solid #3a4250;
  border-radius: 6px;
  overflow: hidden;
  box-shadow:
    0 0 28px rgba(88, 166, 255, 0.5),
    0 0 12px rgba(0, 0, 0, 0.6);
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
  gap: 16px;
  user-select: none;
}

.aspire-terminal-host .footer-group {
  display: flex;
  align-items: center;
  gap: 6px;
}
.aspire-terminal-host .footer-label {
  color: var(--aspire-term-fg-muted);
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.aspire-terminal-host #terminal-footer button {
  background: #21262d;
  color: var(--aspire-term-fg);
  border: 1px solid var(--aspire-term-border);
  border-radius: 3px;
  padding: 0;
  width: 22px;
  height: 22px;
  font: 14px/1 ui-monospace, monospace;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
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
  color: var(--aspire-term-fg);
  min-width: 24px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}
.aspire-terminal-host #size-select {
  background: #21262d;
  color: var(--aspire-term-fg);
  border: 1px solid var(--aspire-term-border);
  border-radius: 3px;
  padding: 2px 6px;
  font: inherit;
  cursor: pointer;
}
.aspire-terminal-host #size-select:hover {
  background: #30363d;
  border-color: #484f58;
}
.aspire-terminal-host #footer-dims {
  margin-left: auto;
  color: var(--aspire-term-fg-muted);
  font-variant-numeric: tabular-nums;
}
.aspire-terminal-host #footer-dims .value {
  color: var(--aspire-term-fg);
}
.aspire-terminal-host #terminal-footer.read-only .footer-group {
  display: none;
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

// Builds the full terminal chrome inside the Blazor host element:
//
//   .aspire-terminal-host           (root with theme vars + flex column)
//     .controls                     (top bar: status badge + take-primary)
//     .terminal-pane                (the gradient stage; flex 1)
//       #terminal                   (xterm centring host)
//         #terminal-frame           (the bordered/shadowed card)
//           #terminal-titlebar      (title text from OSC 0/2)
//           #terminal-body          (xterm host; sized by layout)
//           #terminal-footer        (font ± + size dropdown + dims)
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

    // Controls bar: status badge + take-primary button.
    const controls = document.createElement('div');
    controls.className = 'controls';

    const badge = document.createElement('span');
    badge.className = 'badge offline';
    badge.id = 'status-badge';
    badge.textContent = 'connecting';

    const spacer = document.createElement('span');
    spacer.className = 'spacer';

    const takePrimaryBtn = document.createElement('button');
    takePrimaryBtn.id = 'take-primary';
    takePrimaryBtn.type = 'button';
    takePrimaryBtn.textContent = 'Take control';
    takePrimaryBtn.disabled = true;
    takePrimaryBtn.addEventListener('click', () => takePrimary(state));

    controls.append(badge, spacer, takePrimaryBtn);

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

    const footer = buildFooter(state);

    frame.append(titlebar, body, footer);
    terminalContainer.appendChild(frame);
    host.append(controls, pane);

    state.host = host;
    state.statusBadge = badge;
    state.takePrimaryBtn = takePrimaryBtn;
    state.terminalContainer = terminalContainer;
    state.terminalFrame = frame;
    state.terminalTitlebar = titlebar;
    state.titleText = titleText;
    state.terminalBody = body;
    state.terminalFooter = footer;
}

// Footer DOM. References are stored back on state for fast updates.
function buildFooter(state) {
    const footer = document.createElement('div');
    footer.id = 'terminal-footer';
    footer.style.display = 'none';

    // Font controls.
    const fontGroup = document.createElement('div');
    fontGroup.className = 'footer-group';
    const fontLabel = document.createElement('span');
    fontLabel.className = 'footer-label';
    fontLabel.textContent = 'Font';

    const fontMinus = document.createElement('button');
    fontMinus.id = 'font-minus';
    fontMinus.type = 'button';
    fontMinus.textContent = '−';
    fontMinus.title = 'Decrease font size';
    fontMinus.addEventListener('click', () => setFontSize(state, state.currentFontPx - 1));

    const fontDisplay = document.createElement('span');
    fontDisplay.id = 'font-display';
    fontDisplay.textContent = `${state.currentFontPx}`;

    const fontPlus = document.createElement('button');
    fontPlus.id = 'font-plus';
    fontPlus.type = 'button';
    fontPlus.textContent = '+';
    fontPlus.title = 'Increase font size';
    fontPlus.addEventListener('click', () => setFontSize(state, state.currentFontPx + 1));

    fontGroup.append(fontLabel, fontMinus, fontDisplay, fontPlus);

    // Size dropdown.
    const sizeGroup = document.createElement('div');
    sizeGroup.className = 'footer-group';
    const sizeLabel = document.createElement('span');
    sizeLabel.className = 'footer-label';
    sizeLabel.textContent = 'Size';

    const sizeSelect = document.createElement('select');
    sizeSelect.id = 'size-select';
    for (const p of SIZE_PRESETS) {
        const o = document.createElement('option');
        o.value = p.value;
        o.textContent = p.label;
        sizeSelect.appendChild(o);
    }
    sizeSelect.addEventListener('change', (e) => {
        const v = e.target.value;
        if (v === 'auto') {
            setSizeMode(state, 'font', null);
        } else {
            const preset = SIZE_PRESETS.find((p) => p.value === v);
            if (preset) setSizeMode(state, 'fixed', { cols: preset.cols, rows: preset.rows });
        }
    });

    sizeGroup.append(sizeLabel, sizeSelect);

    // Live dims readout (right-aligned via margin-left:auto in CSS).
    const dimsReadout = document.createElement('span');
    dimsReadout.id = 'footer-dims';
    dimsReadout.innerHTML = `<span class="value">—</span> × <span class="value">—</span>`;

    footer.append(fontGroup, sizeGroup, dimsReadout);

    state.fontMinusBtn = fontMinus;
    state.fontPlusBtn = fontPlus;
    state.fontDisplay = fontDisplay;
    state.sizeSelect = sizeSelect;
    state.dimsReadout = dimsReadout;

    return footer;
}

function safeFit(state) {
    try { state.fitAddon?.fit(); } catch { /* ignore — happens during teardown */ }
}

const FRAME_BORDER_PX = 2;
function getAvailableBodySpace(state) {
    const titlebarH = state.terminalTitlebar ? state.terminalTitlebar.offsetHeight : 0;
    const footerH = state.terminalFooter ? state.terminalFooter.offsetHeight : 0;
    const stageW = state.terminalContainer ? state.terminalContainer.clientWidth : 0;
    const stageH = state.terminalContainer ? state.terminalContainer.clientHeight : 0;
    return {
        width: Math.max(0, stageW - FRAME_BORDER_PX * 2),
        height: Math.max(0, stageH - titlebarH - footerH - FRAME_BORDER_PX * 2),
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

    // Footer visibility depends on isPrimary, and getAvailableBodySpace
    // subtracts the footer height — so update controls FIRST.
    updateFooterControls(state);

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
        updateFooterControls(state);
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

// Refreshes status badge + take-primary button + footer state.
function updateChrome(state) {
    if (!state.statusBadge || !state.takePrimaryBtn) return;
    const client = state.client;
    if (!client || client.peerId === null) {
        state.statusBadge.className = 'badge offline';
        state.statusBadge.textContent = 'connecting';
        state.takePrimaryBtn.disabled = true;
        updateFooterControls(state);
        return;
    }

    if (client.isPrimary) {
        state.statusBadge.className = 'badge primary';
        state.statusBadge.textContent = 'PRIMARY';
        state.takePrimaryBtn.disabled = true;
    } else if (client.primaryPeerId === null) {
        state.statusBadge.className = 'badge no-primary';
        state.statusBadge.textContent = 'no primary';
        state.takePrimaryBtn.disabled = false;
    } else {
        state.statusBadge.className = 'badge viewer';
        state.statusBadge.textContent = 'viewer';
        state.takePrimaryBtn.disabled = false;
    }
    updateFooterControls(state);
}

function updateFooterControls(state) {
    const footer = state.terminalFooter;
    if (!footer) return;

    const connected = !!state.client && state.client.peerId !== null;
    const isPrimary = connected && state.client.isPrimary;
    footer.style.display = connected ? 'flex' : 'none';
    footer.classList.toggle('read-only', connected && !isPrimary);

    if (state.fontDisplay) state.fontDisplay.textContent = `${state.currentFontPx}`;

    const fontDisabled = state.sizeMode === 'fixed' || !isPrimary;
    if (state.fontMinusBtn) state.fontMinusBtn.disabled = fontDisabled;
    if (state.fontPlusBtn) state.fontPlusBtn.disabled = fontDisabled;

    if (state.sizeSelect) {
        const expected = state.sizeMode === 'fixed' && state.fixedDims
            ? `${state.fixedDims.cols}x${state.fixedDims.rows}`
            : 'auto';
        if (state.sizeSelect.value !== expected) state.sizeSelect.value = expected;
        state.sizeSelect.disabled = !isPrimary;
    }

    if (state.dimsReadout && state.term) {
        const c = state.term.cols ? state.term.cols : '—';
        const r = state.term.rows ? state.term.rows : '—';
        state.dimsReadout.innerHTML =
            `<span class="value">${c}</span> × <span class="value">${r}</span>`;
    }
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

export async function initTerminal(element, wsUrl) {
    await ensureXtermLoaded();

    const id = nextId++;
    const state = {
        id,
        client: null,
        term: null,
        fitAddon: null,
        element,
        wsUrl,
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
        // DOM refs filled in by buildChrome / buildFooter.
        host: null,
        statusBadge: null,
        takePrimaryBtn: null,
        terminalContainer: null,
        terminalFrame: null,
        terminalTitlebar: null,
        titleText: null,
        terminalBody: null,
        terminalFooter: null,
        fontMinusBtn: null,
        fontPlusBtn: null,
        fontDisplay: null,
        sizeSelect: null,
        dimsReadout: null,
    };

    // Build the chrome BEFORE creating the xterm — term.open(body)
    // needs the body element to exist.
    buildChrome(state);

    const FitAddon = window.FitAddon.FitAddon;
    const fitAddon = new FitAddon();
    const term = new window.Terminal({
        cursorBlink: true,
        fontSize: state.currentFontPx,
        fontFamily: 'Menlo, Consolas, "DejaVu Sans Mono", monospace',
        // HMP1 does not currently synchronize scrollback across consumer
        // reconnects — the producer's StateSync only repaints the visible
        // viewport. The reconnect path below calls term.clear() on every
        // new HMP1 session so the StateSync repaints into a clean buffer;
        // that also resets this scrollback.
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
    // viewers' fit() calls don't disturb the producer. Re-render footer
    // dims and recalibrate ratios so future fixed-mode font calcs stay
    // accurate.
    term.onResize(({ cols, rows }) => {
        if (state.client) state.client.sendResize(cols, rows);
        calibrateRatios(state);
        updateFooterControls(state);
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

    // Clear xterm so the host's StateSync (sent at the start of every new
    // HMP1 session) repaints into a clean buffer instead of layering on
    // top of stale content from the prior connection. Wrapped because
    // xterm.js can throw if its renderer is in a transitional state
    // (e.g. element detached during navigation); in that case we just
    // skip the clear — the next StateSync will overwrite the buffer
    // anyway.
    try { state.term.clear(); } catch { /* ignore */ }

    // Update toolbar to "connecting…" while the new handshake completes.
    updateChrome(state);

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
        updateChrome(state);
        // Now that we know producer dims + role, apply layout (fits the
        // role-aware path: secondary locks-and-scales to producer dims;
        // primary fits/computes-font into the available stage).
        applyRoleAwareLayout(state);
    };

    client.onRoleChange = (payload) => {
        if (myGeneration !== state.reconnect.generation) return;
        dbg(state, 'client.onRoleChange', payload);
        updateChrome(state);
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
        dbg(state, 'client.onClose', {
            generation: myGeneration,
            currentGeneration: state.reconnect.generation,
            stale: myGeneration !== state.reconnect.generation,
            code: ev?.code,
            reason: ev?.reason,
            wasClean: ev?.wasClean,
        });
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        if (!state.reconnect.enabled) {
            return;
        }
        updateChrome(state); // back to "connecting"
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
