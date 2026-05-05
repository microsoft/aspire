// xterm.js terminal integration for the Aspire Dashboard.
// Connects to the Dashboard's WebSocket proxy which bridges to the
// resource's UDS using the Aspire Terminal Protocol.

// xterm.js is loaded via script tags (not ES module import) because
// the minified bundle uses UMD format, not ESM exports.

const terminals = new Map();
let nextId = 1;
const textEncoder = new TextEncoder();

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

// Auto-reconnect configuration. The server may close the WebSocket for many
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
// open a new WebSocket the generation bumps; ws.on* callbacks compare
// against the captured generation and bail if a newer connect has
// superseded them. This prevents two failure modes:
//   1. A late onclose from socket N firing AFTER socket N+1 has connected
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
        // Surface a single hint; reconnectTerminal() (called when the user
        // re-selects the resource/replica or navigates back) will reset
        // attempts to 0 and try again.
        try {
            state.term.write('\r\n\x1b[33m[terminal disconnected — reload the page or re-select the resource to retry]\x1b[0m\r\n');
        } catch { /* ignore */ }
        return;
    }
    const delay = pickReconnectDelay(state.reconnect.attempts);
    state.reconnect.attempts++;
    state.reconnect.timer = setTimeout(() => {
        state.reconnect.timer = null;
        if (!state.reconnect.enabled) {
            return;
        }
        connectWebSocket(state, state.wsUrl);
    }, delay);
}

function cancelPendingReconnect(state) {
    if (state.reconnect.timer !== null) {
        clearTimeout(state.reconnect.timer);
        state.reconnect.timer = null;
    }
}

export async function initTerminal(element, wsUrl) {
    await ensureXtermLoaded();

    const FitAddon = window.FitAddon.FitAddon;
    const fitAddon = new FitAddon();
    const term = new window.Terminal({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: '"Cascadia Code", "Cascadia Mono", Menlo, Monaco, "Courier New", monospace',
        theme: {
            background: '#1e1e1e',
            foreground: '#d4d4d4',
            cursor: '#d4d4d4',
        },
    });

    term.loadAddon(fitAddon);
    term.open(element);

    // Small delay to let the DOM settle before fitting
    await new Promise(r => setTimeout(r, 50));
    fitAddon.fit();

    const id = nextId++;
    const state = {
        id,
        ws: null,
        term,
        fitAddon,
        element,
        wsUrl,
        // Stateful UTF-8 decoder. WebSocket message boundaries are arbitrary —
        // a 3-byte box-drawing codepoint (U+2500..U+259F, common in TUIs that
        // draw windows/borders) can be split across consecutive binary frames.
        // Decoding on this side with { stream: true } buffers any incomplete
        // tail bytes inside the decoder so xterm.js only ever sees complete
        // UTF-16 strings, eliminating partial-codepoint glitches that would
        // otherwise show up as replacement characters or stray boxes.
        // Recreated on every new connection so partial bytes from a closed
        // socket never bleed into a fresh stream.
        utf8Decoder: new TextDecoder('utf-8', { fatal: false }),
        reconnect: {
            enabled: true,
            attempts: 0,
            timer: null,
            generation: 0,
        },
    };

    // Connect WebSocket
    connectWebSocket(state, wsUrl);

    // Handle terminal resize
    const resizeObserver = new ResizeObserver(() => {
        try { fitAddon.fit(); } catch { /* ignore */ }
    });
    resizeObserver.observe(element);

    term.onResize(() => sendResize(state));

    // Handle user input. Send keystrokes as binary frames so the server can
    // distinguish them from text-mode JSON control frames (resize, etc.) by
    // WebSocket frame type rather than by content sniffing.
    term.onData((data) => {
        if (state.ws && state.ws.readyState === WebSocket.OPEN) {
            state.ws.send(textEncoder.encode(data));
        }
    });

    state._resizeObserver = resizeObserver;
    terminals.set(id, state);
    return id;
}

function sendResize(state) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) {
        // Use xterm's authoritative dimensions (post-fit) rather than the
        // values reported by the onResize event, so this helper can be
        // shared between explicit "send current size" calls and the
        // onResize-triggered path.
        state.ws.send(JSON.stringify({ type: 'resize', cols: state.term.cols, rows: state.term.rows }));
    }
}

function connectWebSocket(state, wsUrl) {
    // Cancel any pending reconnect timer and bump the generation so that
    // late callbacks from any prior socket no-op rather than racing with
    // this new connection.
    cancelPendingReconnect(state);
    state.reconnect.generation++;
    const myGeneration = state.reconnect.generation;
    state.wsUrl = wsUrl;

    // Tear down any in-flight socket without firing its onclose (we don't
    // want it to schedule its own reconnect on top of ours).
    if (state.ws) {
        try {
            state.ws.onopen = null;
            state.ws.onmessage = null;
            state.ws.onerror = null;
            state.ws.onclose = null;
            state.ws.close();
        } catch { /* ignore */ }
    }

    // Reset the UTF-8 decoder so any tail bytes from the previous stream
    // don't bleed into the next one. Reinitialising via "" + final flush
    // would be enough but constructing a fresh decoder is clearer.
    state.utf8Decoder = new TextDecoder('utf-8', { fatal: false });

    // Clear xterm so the host's StateSync (sent at the start of every new
    // HMP1 session) repaints into a clean buffer instead of layering on
    // top of stale content from the prior connection. Wrapped because
    // xterm.js can throw if its renderer is in a transitional state
    // (e.g. element detached during navigation); in that case we just
    // skip the clear — the next StateSync will overwrite the buffer
    // anyway.
    try { state.term.clear(); } catch { /* ignore */ }

    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'arraybuffer';

    ws.onopen = () => {
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        // Connection is healthy. Reset the backoff so the next disconnect
        // gets a snappy first retry rather than picking up where the prior
        // attempt left off.
        state.reconnect.attempts = 0;

        // Re-fit in case the container size changed between init and connect,
        // then proactively tell the host our dimensions BEFORE any output is
        // rendered. The host sends its initial StateSync at its own producer
        // dimensions as soon as the consumer connects; this resize lets the
        // host re-emit a StateSync in the viewer's coordinate system before
        // anything user-visible relies on the wrong-size first frame.
        try { state.fitAddon.fit(); } catch { /* ignore */ }
        sendResize(state);
    };

    ws.onmessage = (event) => {
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        if (event.data instanceof ArrayBuffer) {
            // stream:true buffers partial multi-byte sequences across calls
            // so a codepoint split across WS messages still decodes correctly.
            const text = state.utf8Decoder.decode(event.data, { stream: true });
            if (text.length > 0) {
                state.term.write(text);
            }
        } else {
            state.term.write(event.data);
        }
    };

    ws.onclose = () => {
        if (myGeneration !== state.reconnect.generation) {
            return;
        }
        if (!state.reconnect.enabled) {
            return;
        }
        // Don't write a banner into the buffer — the next successful connect
        // will clear and repaint via StateSync. Spamming "[disconnected]"
        // lines into the scrollback every cycle is more disruptive than the
        // brief blank-then-repaint that the user sees instead.
        scheduleReconnect(state);
    };

    ws.onerror = () => {
        // Errors are followed by a close in browser WebSockets; defer to
        // onclose to drive the reconnect logic.
    };

    state.ws = ws;
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state) return;

    // Explicit reconnect (e.g. user navigated to a different replica).
    // Reset the backoff so we connect immediately rather than waiting for
    // the next pending auto-reconnect timer slot.
    state.reconnect.attempts = 0;
    connectWebSocket(state, wsUrl);
}

export function disposeTerminal(id) {
    const state = terminals.get(id);
    if (!state) return;

    // Make absolutely sure no late callback resurrects the terminal.
    state.reconnect.enabled = false;
    cancelPendingReconnect(state);
    state.reconnect.generation++;

    if (state._resizeObserver) {
        state._resizeObserver.disconnect();
    }
    if (state.ws) {
        try {
            state.ws.onopen = null;
            state.ws.onmessage = null;
            state.ws.onerror = null;
            state.ws.onclose = null;
            state.ws.close();
        } catch { /* ignore */ }
    }
    if (state.term) {
        try { state.term.dispose(); } catch { /* ignore */ }
    }
    terminals.delete(id);
}
