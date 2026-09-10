// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE, InputRoute } from "../../js/hex1b-web-terminal/dist/index.js";

const terminals = new Map();
const rememberedFontSizes = new Map();
let nextId = 1;
const DEFAULT_FONT_SIZE = 13;
const RECONNECT_BACKOFF_MS = [500, 1000, 2000, 4000, 5000];
const MAX_RECONNECT_ATTEMPTS = 30;
const COMPLETION_CHECK_TIMEOUT_MS = 5000;
const SIZE_PRESETS = [
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
    void finishConnectionFailure(state, generation, error);
}

async function finishConnectionFailure(state, generation, error) {
    // The package times out before its first frame even if the server intentionally
    // retains an ended view's socket. Consult the existing per-view registration,
    // through Blazor, rather than parsing private error strings or HWT payloads.
    const ended = await checkTerminalEnded(state);
    if (!isCurrent(state, generation)) {
        return;
    }
    state.failurePending = false;
    if (ended === true) {
        state.ended = true;
        state.error = null;
        state.waitingForVisibility = false;
        cancelReconnect(state);
        if (!state.client) {
            state.controller?.abort();
        }
        // A completed view is kept in place until closed. After an unsuccessful
        // first mount this can be empty; there is no promise of a final screen.
    } else {
        console.warn("Dashboard terminal connection failed.", error);
        state.error = ended === false ? "mount-failed" : "disconnected";
        if (ended === false) {
            releaseClient(state);
            scheduleReconnect(state, generation);
        }
        // If the Blazor circuit cannot confirm lifecycle state, preserve the view
        // and offer explicit retry instead of guessing and looping indefinitely.
    }
    notifyToolbar(state);
}

async function checkTerminalEnded(state) {
    if (!state.dotNetRef || !state.viewId) {
        return false;
    }
    const controller = new AbortController();
    state.completionCheck = controller;
    let timeout;
    const cancelled = new Promise(resolve => {
        controller.signal.addEventListener("abort", () => resolve(null), { once: true });
        timeout = setTimeout(() => resolve(null), COMPLETION_CHECK_TIMEOUT_MS);
    });
    try {
        return await Promise.race([
            Promise.resolve().then(() => state.dotNetRef.invokeMethodAsync("IsTerminalEnded", state.viewId))
                .then(value => typeof value === "boolean" ? value : null, () => null),
            cancelled,
        ]);
    } finally {
        clearTimeout(timeout);
        controller.abort();
        if (state.completionCheck === controller) {
            state.completionCheck = null;
        }
    }
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

function inputPolicy(state, input, context) {
    // Input interception is a public package hook and reaches shadow-root keyboard
    // input without querying the client's private textarea or swallowing F6 in the PTY.
    if (input.type === "key" && input.key === "F6" && !input.ctrl && !input.alt && !input.meta) {
        return focusControls(state, input.shift) ? InputRoute.Consume : InputRoute.Browser;
    }
    if (state.readOnly || state.ended) {
        // The server independently enforces this per-view restriction, including a
        // paste or drag that started before this flag changed. It is a presentation
        // policy, not user authorization. All inspection actions below are public.
        if (input.type === "key") {
            const key = input.key.toLowerCase();
            if ((input.ctrl || input.meta) && key === "c" && context.selection.status === "valid") {
                return { action: "copySelection" };
            }
            if (input.key === "Escape" && context.selection.active) {
                return { action: "clearSelection" };
            }
            if (input.shift && ["PageUp", "PageDown"].includes(input.key)) {
                return { action: "scrollLines", args: input.key === "PageUp" ? -20 : 20 };
            }
            return input.ctrl || input.meta ? InputRoute.Browser : InputRoute.Consume;
        }
        if (input.type === "text" || input.type === "paste") {
            return InputRoute.Consume;
        }
        if (input.type === "wheel") {
            return { action: "scrollLines", args: Math.sign(input.deltaY) * 3 };
        }
        if (input.type === "pointer") {
            if (input.button === "right") {
                return context.selection.status === "valid" ? { action: "copySelection" } : InputRoute.Consume;
            }
            if (input.button === "middle" || (context.mouseCaptured && !input.shift && !context.historical)) {
                return InputRoute.Consume;
            }
            // Ordinary selection, or Shift-drag in mouse-capturing applications, stays native.
            return InputRoute.Continue;
        }
    }
    return InputRoute.Continue;
}

function connectClient(state) {
    if (state.disposed || state.ended) {
        return;
    }
    cancelReconnect(state);
    state.completionCheck?.abort();
    state.failurePending = false;
    const generation = ++state.generation;
    releaseClient(state);
    state.peer = { id: null, primaryId: null, isPrimary: false };
    state.geometry = null;
    state.connected = false;
    state.pendingSizing = null;
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
            // The package's flag is mount-only. Keep it writable so the per-view
            // server policy and public input interceptor can change without reconnecting.
            readOnly: false,
            onInput: (input, context) => inputPolicy(state, input, context),
            onSelectionUI: createSelectionUI(state, current),
            // The package chooses WebGL2 on ordinary HTTP/unavailable WebGPU;
            // unexpected initialization and runtime rendering errors still surface.
            // https://github.com/mitchdenny/hex1b/pull/491
            renderer: "auto",
            onStatus(message, level) {
                if (!current() || level !== "error") {
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
                if (current()) {
                    inputFailed(state, error);
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
        // Only explicit sizing gestures request authority, never ordinary keyboard, paste or mouse input.
        requestPrimaryFromHost(state.id);
    }
}

export function initTerminal(element, wsUrl, dotNetRef, options, selectionTemplate, footer) {
    const id = nextId++;
    const fontSize = rememberedFontSizes.get(options.sizeMemoryKey) ?? DEFAULT_FONT_SIZE;
    const state = {
        id, element, wsUrl, dotNetRef, options, selectionTemplate, footer,
        viewId: new URL(wsUrl).searchParams.get("viewId") ?? options.viewId,
        readOnly: !!options.readOnly,
        client: null,
        controller: null,
        disposed: false,
        ended: false,
        connected: false,
        peer: { id: null, primaryId: null, isPrimary: false },
        geometry: null,
        sizing: { mode: "auto", fontSize },
        pendingSizing: null,
        error: null,
        generation: 0,
        attempts: 0,
        reconnectTimer: null,
        toolbarFrame: null,
        lastToolbarJson: null,
        waitingForVisibility: false,
        restoreFocus: false,
        completionCheck: null,
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
    state.viewId = new URL(wsUrl).searchParams.get("viewId");
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
    state.completionCheck?.abort();
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
    if (readOnly) {
        state.pendingSizing = null;
    }
    notifyToolbar(state);
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
    if (!state || !preset || (state.options.showDimensions === false && sizeKey !== "auto")) {
        return;
    }
    changeSizing(state, preset.value === "auto"
        ? { mode: "auto", fontSize: state.sizing.fontSize }
        : { mode: "fixed", columns: preset.cols, rows: preset.rows, fontSize: state.sizing.fontSize });
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
        sizeKey: state.sizing.mode === "auto" ? "auto" : `${state.sizing.columns}x${state.sizing.rows}`,
        fontPx: state.sizing.fontSize,
        fontControlsEnabled,
        canDecreaseFontSize: fontControlsEnabled && state.sizing.fontSize > MIN_FONT_SIZE,
        canIncreaseFontSize: fontControlsEnabled && state.sizing.fontSize < MAX_FONT_SIZE,
        sizeSelectEnabled: !state.readOnly && (isPrimary || canTakeControl),
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
        // The package observes this container; revealing a view must not reconnect or discard its history.
        state.client?.refreshSelectionUI();
    }
}
