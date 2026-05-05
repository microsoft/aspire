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
    const state = { id, ws: null, term, fitAddon, element };

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
    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'arraybuffer';

    ws.onopen = () => {
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
        if (event.data instanceof ArrayBuffer) {
            state.term.write(new Uint8Array(event.data));
        } else {
            state.term.write(event.data);
        }
    };

    ws.onclose = () => {
        state.term.write('\r\n\x1b[1;33m[Terminal disconnected]\x1b[0m\r\n');
    };

    ws.onerror = () => {
        state.term.write('\r\n\x1b[1;31m[Terminal connection error]\x1b[0m\r\n');
    };

    state.ws = ws;
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state) return;

    if (state.ws) {
        state.ws.onclose = null;
        state.ws.close();
    }
    state.term.clear();
    connectWebSocket(state, wsUrl);
}

export function disposeTerminal(id) {
    const state = terminals.get(id);
    if (!state) return;

    if (state._resizeObserver) {
        state._resizeObserver.disconnect();
    }
    if (state.ws) {
        state.ws.onclose = null;
        state.ws.close();
    }
    if (state.term) {
        state.term.dispose();
    }
    terminals.delete(id);
}
