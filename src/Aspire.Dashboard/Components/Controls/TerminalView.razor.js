// xterm.js terminal integration for the Aspire Dashboard.
// Connects to the Dashboard's WebSocket proxy which bridges to the
// resource's UDS using the Aspire Terminal Protocol.

let Terminal, FitAddon;

async function loadXterm() {
    if (!Terminal) {
        const xtermModule = await import('/js/xterm/xterm.min.js');
        Terminal = xtermModule.Terminal;
        const fitModule = await import('/js/xterm/addon-fit.min.js');
        FitAddon = fitModule.FitAddon;

        // Inject xterm.css if not already present
        if (!document.querySelector('link[href*="xterm.min.css"]')) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = '/js/xterm/xterm.min.css';
            document.head.appendChild(link);
        }
    }
}

export async function initTerminal(element, wsUrl) {
    await loadXterm();

    const fitAddon = new FitAddon();
    const term = new Terminal({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: '"Cascadia Code", "Cascadia Mono", Menlo, Monaco, "Courier New", monospace',
        theme: {
            background: '#1e1e1e',
            foreground: '#d4d4d4',
            cursor: '#d4d4d4',
        },
        allowProposedApi: true,
    });

    term.loadAddon(fitAddon);
    term.open(element);
    fitAddon.fit();

    // Connect WebSocket
    const state = { ws: null, term, fitAddon, element };
    connectWebSocket(state, wsUrl);

    // Handle terminal resize
    const resizeObserver = new ResizeObserver(() => {
        fitAddon.fit();
    });
    resizeObserver.observe(element);

    term.onResize(({ cols, rows }) => {
        if (state.ws && state.ws.readyState === WebSocket.OPEN) {
            // Send resize as JSON message (the proxy translates to protocol RESIZE frame)
            state.ws.send(JSON.stringify({ type: 'resize', cols, rows }));
        }
    });

    // Handle user input
    term.onData((data) => {
        if (state.ws && state.ws.readyState === WebSocket.OPEN) {
            state.ws.send(data);
        }
    });

    // Handle binary input
    term.onBinary((data) => {
        if (state.ws && state.ws.readyState === WebSocket.OPEN) {
            const buffer = new Uint8Array(data.length);
            for (let i = 0; i < data.length; i++) {
                buffer[i] = data.charCodeAt(i);
            }
            state.ws.send(buffer);
        }
    });

    state._resizeObserver = resizeObserver;
    return state;
}

function connectWebSocket(state, wsUrl) {
    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'arraybuffer';

    ws.onopen = () => {
        state.term.clear();
        state.fitAddon.fit();
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

export function reconnectTerminal(state, wsUrl) {
    if (state.ws) {
        state.ws.onclose = null; // prevent disconnect message
        state.ws.close();
    }
    state.term.clear();
    connectWebSocket(state, wsUrl);
}

export function disposeTerminal(state) {
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
}
