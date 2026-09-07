// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE } from "../../js/hex1b-web-terminal/dist/index.js";

const terminals = new Map();
let nextId = 1;
const DEFAULT_FONT_SIZE = 13;
const RECONNECT_BACKOFF_MS = [500, 1000, 2000, 4000, 5000];
const MAX_RECONNECT_ATTEMPTS = 30;
const SIZE_PRESETS = [
    // The host replaces Auto with the localized TerminalToolbarGridSizeAuto.
    { value: "auto", label: "Auto", cols: 0, rows: 0 },
    { value: "80x24", label: "80×24", cols: 80, rows: 24 },
    { value: "80x30", label: "80×30", cols: 80, rows: 30 },
    { value: "100x30", label: "100×30", cols: 100, rows: 30 },
    { value: "132x30", label: "132×30", cols: 132, rows: 30 },
    { value: "132x50", label: "132×50", cols: 132, rows: 50 },
];

function isCurrent(state, generation) {
    return !state.disposed && state.generation === generation;
}

function isVisible(state) {
    return state.element.clientWidth > 0 && state.element.clientHeight > 0;
}

function notifyToolbar(state) {
    if (state.disposed || state.toolbarFrame !== null) {
        return;
    }
    // Geometry and role notifications can arrive together on every frame.
    // Coalesce them before crossing the Blazor interop boundary.
    state.toolbarFrame = requestAnimationFrame(() => {
        state.toolbarFrame = null;
        flushToolbar(state);
    });
}

function flushToolbar(state) {
    if (state.disposed || !state.dotNetRef) {
        return;
    }
    const snapshot = getToolbarState(state.id);
    const json = JSON.stringify(snapshot);
    if (json === state.lastToolbarJson) {
        return;
    }
    state.lastToolbarJson = json;
    // The circuit may disappear while a worker notification is in flight.
    // Handle asynchronous rejection too, not just synchronous interop errors.
    Promise.resolve().then(() =>
        state.dotNetRef?.invokeMethodAsync("OnTerminalStateChanged", snapshot)
    ).catch(() => {
        if (!state.disposed) {
            state.lastToolbarJson = null;
        }
    });
}

function cancelReconnect(state) {
    if (state.reconnectTimer !== null) {
        clearTimeout(state.reconnectTimer);
        state.reconnectTimer = null;
    }
}

function releaseClient(state) {
    state.restoreFocus ||= !!state.client?.element.contains(document.activeElement);
    const controller = state.controller;
    const client = state.client;
    state.controller = null;
    state.client = null;
    controller?.abort();
    client?.dispose();
}

function scheduleReconnect(state, generation) {
    if (!isCurrent(state, generation) || state.reconnectTimer !== null) {
        return;
    }
    if (state.attempts >= MAX_RECONNECT_ATTEMPTS) {
        state.error = "disconnected";
        notifyToolbar(state);
        return;
    }
    const delay = RECONNECT_BACKOFF_MS[Math.min(state.attempts++, RECONNECT_BACKOFF_MS.length - 1)];
    state.reconnectTimer = setTimeout(() => {
        state.reconnectTimer = null;
        if (isCurrent(state, generation)) {
            connectClient(state);
        }
    }, delay);
}

function connectionFailed(state, generation, error) {
    if (!isCurrent(state, generation)) {
        return;
    }
    console.warn("Dashboard terminal connection failed.", error);
    state.error = "mount-failed";
    state.connected = false;
    state.peer = { id: null, primaryId: null, isPrimary: false };
    state.pendingSizing = null;
    releaseClient(state);
    notifyToolbar(state);
    scheduleReconnect(state, generation);
}

function connectClient(state) {
    cancelReconnect(state);
    const generation = ++state.generation;
    releaseClient(state);
    state.peer = { id: null, primaryId: null, isPrimary: false };
    state.geometry = null;
    state.connected = false;
    state.pendingSizing = null;
    state.waitingForVisibility = false;
    notifyToolbar(state);

    // Do not spend the client's 30-second first-frame timeout while the
    // Console view has this component hidden. The same mounted view is kept
    // alive on subsequent hide/show transitions so its history is preserved.
    if (!isVisible(state)) {
        state.waitingForVisibility = true;
        return;
    }
    if (!window.isSecureContext || !navigator.gpu) {
        state.error = "unsupported";
        notifyToolbar(state);
        return;
    }

    const controller = new AbortController();
    state.controller = controller;
    // Return the terminal id before awaiting mount: Blazor must be able to
    // cancel a pending first frame when the resource changes or is disposed.
    void mountClient(state, generation, controller);
}

async function mountClient(state, generation, controller) {
    const current = () => isCurrent(state, generation) && !controller.signal.aborted;
    try {
        const client = await WebTerminal.mount(state.element, {
            url: state.wsUrl,
            signal: controller.signal,
            label: state.label,
            sizing: state.sizing,
            onStatus(message, level) {
                if (!current() || level !== "error") {
                    return;
                }
                // In this paired client, worker/transport errors disconnect
                // before onStatus. Selection-UI errors can also report "error"
                // without disconnecting; do not discard history for those.
                if (state.client?.connected) {
                    state.error = "input-failed";
                    notifyToolbar(state);
                } else {
                    connectionFailed(state, generation, message);
                }
            },
            onGeometry(geometry) {
                if (current()) {
                    state.geometry = geometry;
                    notifyToolbar(state);
                }
            },
            onRoleChange(peer) {
                if (current()) {
                    state.peer = peer;
                    // requestPrimary is only a request. Do not apply sizing
                    // or advertise ownership until the producer confirms it.
                    applyPendingSizing(state);
                    notifyToolbar(state);
                }
            },
            onSizingChange(sizing) {
                if (current()) {
                    state.sizing = sizing;
                    notifyToolbar(state);
                }
            },
            onInputError(error) {
                if (current()) {
                    console.warn("Dashboard terminal input failed.", error);
                    state.error = "input-failed";
                    notifyToolbar(state);
                }
            },
        });
        if (!current()) {
            client.dispose();
            return;
        }
        state.client = client;
        state.connected = client.connected;
        state.peer = client.peer;
        state.geometry = client.geometry;
        state.sizing = client.sizing;
        state.error = null;
        state.attempts = 0;
        if (state.restoreFocus && isVisible(state) &&
            (!document.activeElement || document.activeElement === document.body || state.element.contains(document.activeElement))) {
            client.focus();
        }
        state.restoreFocus = false;
        applyPendingSizing(state);
        notifyToolbar(state);
    } catch (error) {
        if (current()) {
            connectionFailed(state, generation, error);
        }
    }
}

function applyPendingSizing(state) {
    if (!state.client?.connected || !state.peer.isPrimary || !state.pendingSizing) {
        return;
    }
    const sizing = state.pendingSizing;
    state.pendingSizing = null;
    try {
        state.client.setSizing(sizing);
        state.error = null;
    } catch (error) {
        console.warn("Dashboard terminal sizing failed.", error);
        state.error = "sizing-failed";
    }
    notifyToolbar(state);
}

function changeSizing(state, sizing) {
    if (!state.client?.connected || !isVisible(state)) {
        return;
    }
    state.pendingSizing = sizing;
    if (state.peer.isPrimary) {
        applyPendingSizing(state);
    } else {
        // A toolbar sizing gesture explicitly asks for resize authority.
        // Ordinary keyboard, mouse and paste input never claims primary.
        requestPrimaryFromHost(state.id);
    }
}

export function initTerminal(element, wsUrl, dotNetRef, label) {
    const id = nextId++;
    const state = {
        id, element, wsUrl, dotNetRef, label,
        client: null,
        controller: null,
        disposed: false,
        connected: false,
        peer: { id: null, primaryId: null, isPrimary: false },
        geometry: null,
        sizing: { mode: "auto", fontSize: DEFAULT_FONT_SIZE },
        pendingSizing: null,
        error: null,
        generation: 0,
        attempts: 0,
        reconnectTimer: null,
        toolbarFrame: null,
        lastToolbarJson: null,
        waitingForVisibility: false,
        restoreFocus: false,
    };
    state.observer = new ResizeObserver(() => {
        if (state.waitingForVisibility && !state.disposed && isVisible(state)) {
            connectClient(state);
        }
    });
    state.observer.observe(element);
    terminals.set(id, state);
    connectClient(state);
    return id;
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state) {
        return 0;
    }
    state.wsUrl = wsUrl;
    state.attempts = 0;
    state.error = null;
    connectClient(state);
    return state.generation;
}

export function disposeTerminal(id) {
    const state = terminals.get(id);
    if (!state) {
        return;
    }
    state.disposed = true;
    ++state.generation;
    cancelReconnect(state);
    if (state.toolbarFrame !== null) {
        cancelAnimationFrame(state.toolbarFrame);
    }
    state.observer.disconnect();
    releaseClient(state);
    state.dotNetRef = null;
    terminals.delete(id);
}

export function getSizePresets() {
    return SIZE_PRESETS;
}

export function setFontSizeFromHost(id, fontSize) {
    const state = terminals.get(id);
    if (!state || !Number.isFinite(fontSize)) {
        return;
    }
    changeSizing(state, { mode: "auto", fontSize: Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, Math.round(fontSize))) });
}

export function setSizeModeFromHost(id, sizeKey) {
    const state = terminals.get(id);
    const preset = SIZE_PRESETS.find(p => p.value === sizeKey);
    if (!state || !preset) {
        return;
    }
    changeSizing(state, preset.value === "auto"
        ? { mode: "auto", fontSize: state.sizing.fontSize }
        : { mode: "fixed", columns: preset.cols, rows: preset.rows, fontSize: state.sizing.fontSize });
}

export function requestPrimaryFromHost(id) {
    const state = terminals.get(id);
    if (!state?.client?.connected || !isVisible(state)) {
        return;
    }
    try {
        state.client.requestPrimary();
    } catch (error) {
        console.warn("Dashboard terminal primary request failed.", error);
        state.pendingSizing = null;
        state.error = "sizing-failed";
        notifyToolbar(state);
    }
}

export function getToolbarState(id) {
    const state = terminals.get(id);
    if (!state) {
        return null;
    }
    const connected = state.connected && !!state.client?.connected;
    const isPrimary = connected && state.peer.isPrimary;
    const canTakeControl = connected && !isPrimary && state.peer.id !== null;
    return {
        terminalId: id,
        generation: state.generation,
        status: !connected ? "connecting" : isPrimary ? "primary" : state.peer.primaryId === null ? "no-primary" : "viewer",
        connected, isPrimary, canTakeControl,
        sizeMode: state.sizing.mode === "auto" ? "font" : "fixed",
        sizeKey: state.sizing.mode === "auto" ? "auto" : `${state.sizing.columns}x${state.sizing.rows}`,
        fontPx: state.sizing.fontSize,
        fontControlsEnabled: (isPrimary && state.sizing.mode === "auto") || canTakeControl,
        sizeSelectEnabled: isPrimary || canTakeControl,
        cols: state.geometry?.columns ?? 0,
        rows: state.geometry?.rows ?? 0,
        error: state.error,
    };
}

export function refreshToolbarState(id) {
    const state = terminals.get(id);
    if (state) {
        state.lastToolbarJson = null;
        flushToolbar(state);
    }
}

export function refreshLayout(id) {
    const state = terminals.get(id);
    if (!state || !isVisible(state)) {
        return;
    }
    if (state.waitingForVisibility) {
        connectClient(state);
    } else {
        // The public client observes this same container and owns fitting for
        // both primary and viewer roles. Never resize the producer or remount
        // simply to reveal the view; either would disturb geometry/history.
        state.client?.refreshSelectionUI();
    }
}
