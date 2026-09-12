// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE, InputRoute } from "../../js/hex1b-web-terminal/dist/index.js";

const terminals = new Map();
const rememberedFontSizes = new Map();
let nextId = 1;
const DEFAULT_FONT_SIZE = 13;
const RECONNECT_BACKOFF_MS = [500, 1000, 2000, 4000, 5000];
const MAX_RECONNECT_ATTEMPTS = 30;
// Aspire's WebSocket endpoint sends this private-use code only after authoritative
// producer completion. It is not an HWT message or a Hex1b-defined close code.
const TERMINAL_ENDED_CLOSE_CODE = 4000;
const SIZE_PRESETS = [
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
    if (!isCurrent(state, generation) || state.ended || state.reconnectTimer !== null) {
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
    if (!isCurrent(state, generation) || state.ended || state.failurePending) {
        return;
    }
    state.failurePending = true;
    state.connected = false;
    state.peer = { id: null, primaryId: null, isPrimary: false };
    state.pendingSizing = null;
    console.warn("Dashboard terminal connection failed.", error);
    state.error = "mount-failed";
    releaseClient(state);
    scheduleReconnect(state, generation);
    notifyToolbar(state);
}

function connectionClosed(state, generation, details) {
    if (!isCurrent(state, generation) || state.ended) {
        return;
    }
    if (details.code !== TERMINAL_ENDED_CLOSE_CODE) {
        // Normal closure (1000), missing close frames (1006), reasons and wasClean
        // describe transport state, never whether the producer has completed.
        connectionFailed(state, generation, new Error(`Terminal WebSocket closed (${details.code}).`));
        return;
    }
    state.ended = true;
    state.connected = false;
    state.pendingSizing = null;
    state.autoFitPending = false;
    state.waitingForVisibility = false;
    state.error = null;
    cancelReconnect(state);
    state.client?.setReadOnly(true);
    // Keep an already mounted projection until the user dismisses the view.
    // A close before the first frame leaves an empty container, not a fake frame.
    notifyToolbar(state);
}

function inputFailed(state, error) {
    console.warn("Dashboard terminal input failed.", error);
    state.error = "input-failed";
    notifyToolbar(state);
}

function selectionCopyPosition(rects, canvasSize, width, height) {
    let anchor = null;
    // Public rectangles are overlay-local CSS pixels. Clip before choosing the
    // last visible selected line, including reverse and multiline selections.
    for (const rect of rects) {
        const left = Math.max(0, rect.left);
        const top = Math.max(0, rect.top);
        const right = Math.min(canvasSize.width, rect.left + rect.width);
        const bottom = Math.min(canvasSize.height, rect.top + rect.height);
        if (right > left && bottom > top &&
            (!anchor || bottom > anchor.bottom || (bottom === anchor.bottom && right > anchor.right))) {
            anchor = { top, right, bottom };
        }
    }
    if (!anchor) {
        return null;
    }
    const gap = 6;
    const below = anchor.bottom + gap;
    const top = below + height <= canvasSize.height ? below : anchor.top - gap - height;
    return {
        left: Math.max(0, Math.min(anchor.right + gap, canvasSize.width - width)),
        top: Math.max(0, Math.min(top, canvasSize.height - height)),
    };
}

function createSelectionUI(state, current) {
    let actions;
    let button;
    let detail;
    let copying = false;

    function updateButtonState() {
        const busy = copying || detail.selection.copying;
        button.disabled = !detail.connected || detail.selection.status !== "valid" || detail.viewport.pending;
        // Native disabling blurs a focused Fluent button. Preserve keyboard
        // copy focus while busy, and guard its click with aria-disabled.
        button.setAttribute("aria-disabled", String(button.disabled || busy));
        button.setAttribute("aria-busy", String(busy));
    }

    return event => {
        // Claim only Copy, not the package's highlights, clipboard or Return to live.
        event.preventDefault();
        if (!current() || event.detail.signal.aborted) {
            return;
        }
        if (!actions) {
            // Clone inert Fluent markup rather than moving Blazor-owned nodes.
            // Clipboard actions must retain browser user activation in JS.
            actions = state.selectionTemplate.firstElementChild.cloneNode(true);
            button = actions.querySelector("fluent-button");
            button.removeAttribute("id");
            const signal = event.detail.signal;
            actions.addEventListener("pointerdown", e => e.preventDefault(), { signal });
            button.addEventListener("click", () => {
                if (!current() || signal.aborted || button.disabled || copying || detail.selection.copying) {
                    return;
                }
                const requestId = detail.selection.requestId;
                copying = true;
                updateButtonState();
                void detail.runAction("copySelection").then(() => {
                    if (!current() || signal.aborted || detail.selection.status !== "valid" ||
                        detail.selection.requestId !== requestId) {
                        return;
                    }
                    actions.hidden = true;
                    state.client.clearSelection();
                    state.client.focus();
                    if (state.error === "input-failed") {
                        state.error = null;
                        notifyToolbar(state);
                    }
                }).catch(error => {
                    if (current() && !signal.aborted && detail.selection.requestId === requestId) {
                        inputFailed(state, error);
                    }
                }).finally(() => {
                    copying = false;
                    if (current() && !signal.aborted) {
                        updateButtonState();
                    }
                });
            }, { signal });
            signal.addEventListener("abort", () => actions.remove(), { once: true });
            event.detail.overlay.append(actions);
        }
        detail = event.detail;
        const selectable = detail.connected && ["valid", "pending"].includes(detail.selection.status);
        const hadFocus = actions.contains(document.activeElement);
        actions.hidden = !selectable;
        const position = selectable
            ? selectionCopyPosition(detail.rects, detail.canvasSize, actions.offsetWidth, actions.offsetHeight)
            : null;
        actions.hidden = !position;
        updateButtonState();
        if (position) {
            actions.style.left = `${position.left}px`;
            actions.style.top = `${position.top}px`;
        } else if (hadFocus) {
            state.client?.focus();
        }
    };
}

function focusControls(state, reverse) {
    if (reverse) {
        const host = state.element.closest(".terminal-view") ?? state.element;
        // Shadow-root input is not in document.querySelectorAll. Find the last
        // visible control before this view, excluding inactive dock panes.
        const previous = Array.from(document.querySelectorAll(
            'a[href], button, input, select, textarea, [tabindex]'))
            .filter(element => element.tabIndex >= 0 && !element.disabled &&
                !element.closest("[inert]") && element.getClientRects().length > 0 &&
                getComputedStyle(element).visibility === "visible" &&
                !host.contains(element) &&
                (element.compareDocumentPosition(host) & Node.DOCUMENT_POSITION_FOLLOWING))
            .at(-1);
        previous?.focus();
        return !!previous;
    }
    const controls = Array.from(state.footer.querySelectorAll("fluent-button, fluent-select"))
        .filter(element => !element.disabled && element.tabIndex >= 0);
    controls[0]?.focus();
    if (controls.length === 0) {
        state.footer.focus();
    }
    return true;
}

function inputPolicy(state, input) {
    // Input interception is a public package hook and reaches shadow-root keyboard
    // input without querying the client's private textarea or swallowing F6 in the PTY.
    if (input.type === "key" && input.key === "F6" && !input.ctrl && !input.alt && !input.meta) {
        return focusControls(state, input.shift) ? InputRoute.Consume : InputRoute.Browser;
    }
    // Hex1b's live read-only policy owns keyboard, IME, pointer, paste and sizing
    // gating, including queued gestures and direct clipboard/action API calls.
    return InputRoute.Continue;
}

function connectClient(state) {
    if (state.disposed || state.ended) {
        return;
    }
    cancelReconnect(state);
    state.failurePending = false;
    const generation = ++state.generation;
    releaseClient(state);
    state.peer = { id: null, primaryId: null, isPrimary: false };
    state.geometry = null;
    state.connected = false;
    state.pendingSizing = null;
    state.autoFitPending = state.autoFit;
    state.waitingForVisibility = false;
    notifyToolbar(state);
    // A hidden Console view must not spend the package's first-frame timeout.
    if (!isVisible(state)) {
        state.waitingForVisibility = true;
        return;
    }
    const controller = new AbortController();
    state.controller = controller;
    // Return the id before waiting for the first frame so disposal/rebinding can cancel it.
    void mountClient(state, generation, controller);
}

async function mountClient(state, generation, controller) {
    const current = () => isCurrent(state, generation) && !controller.signal.aborted;
    try {
        const client = await WebTerminal.mount(state.element, {
            url: state.wsUrl,
            signal: controller.signal,
            label: state.options.label,
            sizing: state.sizing,
            readOnly: state.readOnly,
            onInput: input => inputPolicy(state, input),
            onClose(details) {
                if (current()) {
                    connectionClosed(state, generation, details);
                }
            },
            onSelectionUI: createSelectionUI(state, current),
            // The package chooses WebGL2 on ordinary HTTP/unavailable WebGPU;
            // unexpected initialization and runtime rendering errors still surface.
            // https://github.com/mitchdenny/hex1b/pull/491
            renderer: "auto",
            onStatus(message, level) {
                if (!current() || state.ended || level !== "error") {
                    return;
                }
                if (state.client?.connected) {
                    inputFailed(state, message);
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
                    // Primary ownership is authoritative; a request alone doesn't grant sizing.
                    applyPendingSizing(state);
                    notifyToolbar(state);
                }
            },
            onSizingChange(sizing) {
                if (current()) {
                    state.sizing = sizing;
                    rememberFontSize(state);
                    notifyToolbar(state);
                }
            },
            onInputError(error) {
                if (current() && !state.ended) {
                    inputFailed(state, error);
                }
            },
        });
        if (!current()) {
            client.dispose();
            return;
        }
        state.client = client;
        // Policy can change while mount is waiting for its first frame.
        client.setReadOnly(state.readOnly || state.ended);
        if (state.ended) {
            notifyToolbar(state);
            return;
        }
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
        applyAutoFit(state);
        applyPendingSizing(state);
        notifyToolbar(state);
    } catch (error) {
        if (current()) {
            connectionFailed(state, generation, error);
        }
    }
}

function rememberFontSize(state) {
    if (state.options.sizeMemoryKey) {
        rememberedFontSizes.set(state.options.sizeMemoryKey, state.sizing.fontSize);
    }
}

function applyPendingSizing(state) {
    if (state.readOnly || state.ended || !state.client?.connected || !state.peer.isPrimary || !state.pendingSizing) {
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
    if (state.readOnly || state.ended || !state.client?.connected || !isVisible(state)) {
        return;
    }
    state.pendingSizing = sizing;
    if (state.peer.isPrimary) {
        applyPendingSizing(state);
    } else {
        // Opening an auto-fit surface or explicitly sizing it requests authority;
        // ordinary keyboard, paste and mouse input never do.
        requestPrimaryFromHost(state.id);
    }
}

function applyAutoFit(state) {
    if (!state.autoFitPending || state.readOnly || state.ended || !state.client?.connected || !isVisible(state)) {
        return;
    }
    // Request once on opening/activation, not on role notifications: another
    // viewer taking primary must not cause the two views to fight over the grid.
    state.autoFitPending = false;
    fitToContainer(state.id);
}

export function initTerminal(element, wsUrl, dotNetRef, options, selectionTemplate, footer) {
    const id = nextId++;
    const fontSize = rememberedFontSizes.get(options.sizeMemoryKey) ??
        (Number.isFinite(options.initialFontSize) ? clampFontSize(options.initialFontSize) : DEFAULT_FONT_SIZE);
    const state = {
        id, element, wsUrl, dotNetRef, options, selectionTemplate, footer,
        readOnly: !!options.readOnly,
        autoFit: !!options.autoFit,
        client: null,
        controller: null,
        disposed: false,
        ended: false,
        connected: false,
        peer: { id: null, primaryId: null, isPrimary: false },
        geometry: null,
        sizing: { mode: "auto", fontSize },
        pendingSizing: null,
        autoFitPending: false,
        error: null,
        generation: 0,
        attempts: 0,
        reconnectTimer: null,
        toolbarFrame: null,
        lastToolbarJson: null,
        waitingForVisibility: false,
        restoreFocus: false,
        failurePending: false,
        listeners: new AbortController(),
    };
    footer.addEventListener("keydown", event => {
        if (event.key === "F6" && !event.ctrlKey && !event.altKey && !event.metaKey) {
            event.preventDefault();
            event.stopPropagation();
            state.client?.focus();
        }
    }, { signal: state.listeners.signal });
    state.observer = new ResizeObserver(() => {
        if (state.waitingForVisibility && !state.disposed && !state.ended && isVisible(state)) {
            connectClient(state);
        } else if (!state.disposed) {
            applyAutoFit(state);
        }
    });
    state.observer.observe(element);
    terminals.set(id, state);
    connectClient(state);
    return id;
}

export function reconnectTerminal(id, wsUrl) {
    const state = terminals.get(id);
    if (!state || (state.ended && state.wsUrl === wsUrl)) {
        return state?.generation ?? 0;
    }
    state.wsUrl = wsUrl;
    state.ended = false;
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
    state.listeners.abort();
    releaseClient(state);
    state.dotNetRef = null;
    terminals.delete(id);
}

export function getSizePresets() {
    return SIZE_PRESETS.map(preset => ({ ...preset }));
}

export function setReadOnly(id, readOnly) {
    const state = terminals.get(id);
    if (!state || state.readOnly === readOnly) {
        return;
    }
    state.readOnly = readOnly;
    state.client?.setReadOnly(readOnly || state.ended);
    if (readOnly) {
        state.pendingSizing = null;
    } else {
        state.autoFitPending = state.autoFit;
        applyAutoFit(state);
    }
    notifyToolbar(state);
}

export function setAutoFit(id, autoFit) {
    const state = terminals.get(id);
    if (!state || state.autoFit === autoFit) {
        return;
    }
    state.autoFit = autoFit;
    state.autoFitPending = autoFit;
    if (!autoFit) {
        state.pendingSizing = null;
    }
    applyAutoFit(state);
}

export function fitToContainer(id) {
    const state = terminals.get(id);
    if (state) {
        changeSizing(state, { mode: "auto", fontSize: state.sizing.fontSize });
    }
}

function clampFontSize(fontSize) {
    return Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, Math.round(fontSize)));
}

export function setFontSizeFromHost(id, fontSize) {
    const state = terminals.get(id);
    if (!state || !Number.isFinite(fontSize)) {
        return;
    }
    changeSizing(state, { mode: "auto", fontSize: clampFontSize(fontSize) });
}

export function setSizeModeFromHost(id, sizeKey) {
    if (sizeKey === "auto") {
        fitToContainer(id);
        return;
    }
    const state = terminals.get(id);
    const preset = SIZE_PRESETS.find(p => p.value === sizeKey);
    if (!state || !preset || state.options.showDimensions === false) {
        return;
    }
    changeSizing(state, { mode: "fixed", columns: preset.cols, rows: preset.rows, fontSize: state.sizing.fontSize });
}

export function requestPrimaryFromHost(id) {
    const state = terminals.get(id);
    if (!state || state.readOnly || state.ended || !state.client?.connected || !isVisible(state)) {
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
    const connected = !state.ended && state.connected && !!state.client?.connected;
    const isPrimary = connected && state.peer.isPrimary;
    const canTakeControl = !state.readOnly && connected && !isPrimary && state.peer.id !== null;
    const fontControlsEnabled = !state.readOnly && ((isPrimary && state.sizing.mode === "auto") || canTakeControl);
    return {
        terminalId: id,
        generation: state.generation,
        status: !connected ? "connecting" : isPrimary ? "primary" : state.peer.primaryId === null ? "no-primary" : "viewer",
        connected, isPrimary, canTakeControl,
        sizeMode: state.sizing.mode === "auto" ? "font" : "fixed",
        sizeKey: state.sizing.mode === "auto"
            ? state.geometry ? `${state.geometry.columns}x${state.geometry.rows}` : ""
            : `${state.sizing.columns}x${state.sizing.rows}`,
        fontPx: state.sizing.fontSize,
        fontControlsEnabled,
        canDecreaseFontSize: fontControlsEnabled && state.sizing.fontSize > MIN_FONT_SIZE,
        canIncreaseFontSize: fontControlsEnabled && state.sizing.fontSize < MAX_FONT_SIZE,
        sizeSelectEnabled: !state.readOnly && (isPrimary || canTakeControl),
        fitEnabled: !state.readOnly && (isPrimary || canTakeControl) && !(isPrimary && state.sizing.mode === "auto"),
        cols: state.geometry?.columns ?? 0,
        rows: state.geometry?.rows ?? 0,
        error: state.error,
    };
}

export function getTerminalSnapshot(element) {
    for (const state of terminals.values()) {
        if (state.element === element) {
            // GPU cells are not DOM text. Expose the package's public logical snapshot
            // without coupling browser automation to renderer or shadow-root internals.
            return {
                ...getToolbarState(state.id),
                readOnly: state.readOnly || state.ended,
                ended: state.ended,
                screenText: state.client?.screenText ?? "",
                selection: state.client?.selection ?? null,
                viewport: state.client?.viewport ?? null,
            };
        }
    }
    return null;
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
    if (!state || state.ended || !isVisible(state)) {
        return;
    }
    if (state.waitingForVisibility) {
        connectClient(state);
    } else {
        applyAutoFit(state);
        // The package observes this container; revealing a view must not reconnect or discard its history.
        state.client?.refreshSelectionUI();
    }
}
