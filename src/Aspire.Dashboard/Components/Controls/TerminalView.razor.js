// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE, InputRoute, createDefaultScrollbarRenderer, renderDefaultScrollbarTooltip, defaultDarkPalette, defaultLightPalette, linkAction } from "../../js/hex1b-web-terminal/dist/index.min.js";

const terminals = new Map();
const rememberedFontSizes = new Map();
let nextId = 1;
const DEFAULT_FONT_SIZE = 13;
const TERMINAL_PADDING = 3;
const TERMINAL_PALETTE_STORAGE_KEY = "Aspire.TerminalPalette";
// Retain Hex1b's neutral slots and selection colors. Chromatic slots increase OKLCH chroma
// by up to 25% (dark) / 10% (light), reducing chroma at the sRGB boundary rather than clipping.
// Lightness is adjusted where necessary for >=5:1 normal / >=6:1 bright text on these backgrounds.
// These are precomputed colors, so palette changes need no runtime color conversion.
const darkPalette = {
    ...defaultDarkPalette,
    background: "#312e3c",
    ansi: [
        "#242424", "#fb7d88", "#87b179", "#ca9f2c", "#6fabdc", "#c195cc", "#4cb4ba", "#b7b4ae",
        "#9a9a9a", "#ff99a0", "#94c384", "#dfaf2b", "#79bcf3", "#d6a3e2", "#4fc8cd", "#dedad3",
    ],
};
const lightPalette = {
    ...defaultLightPalette,
    background: "#d5d0df",
    ansi: [
        "#24262b", "#9e233a", "#365d28", "#6a4f00", "#165786", "#6d4478", "#005d61", "#b7b4ae",
        "#595959", "#93042d", "#295118", "#5b4500", "#004b7a", "#62376d", "#005054", "#e3dfd7",
    ],
};
const RECONNECT_BACKOFF_MS = [500, 1000, 2000, 4000, 5000];
const MAX_RECONNECT_ATTEMPTS = 30;
// Aspire's WebSocket endpoint sends this private-use code only after authoritative
// producer completion or removal. It is not an HWT message or a Hex1b-defined close code.
const TERMINAL_ENDED_CLOSE_CODE = 4000;
const SIZE_PRESETS = [
    { value: "80x24", label: "80×24", cols: 80, rows: 24 },
    { value: "80x30", label: "80×30", cols: 80, rows: 30 },
    { value: "100x30", label: "100×30", cols: 100, rows: 30 },
    { value: "132x30", label: "132×30", cols: 132, rows: 30 },
    { value: "132x50", label: "132×50", cols: 132, rows: 50 },
];

function scrollbarConfiguration(state) {
    // Resolve variables/system colors to concrete canvas colors before passing
    // them to the snapshotted built-in painter.
    const probe = document.createElement("span");
    probe.style.cssText = "position:absolute;visibility:hidden;pointer-events:none;forced-color-adjust:none";
    document.body.append(probe);
    try {
        const color = value => {
            probe.style.color = value;
            return getComputedStyle(probe).color;
        };
        const forced = state.forcedColors.matches;
        const palette = state.colorMode === "light" ? lightPalette : darkPalette;
        const tooltipStyle = {
            background: color(forced ? "Canvas" : "var(--aspire-popup-background)"),
            color: color(forced ? "CanvasText" : "var(--colorNeutralForeground1)"),
            borderColor: color(forced ? "CanvasText" : "var(--colorNeutralStroke1)"),
            borderRadius: "var(--aspire-popup-radius)",
            boxShadow: forced ? "none" : getComputedStyle(probe).getPropertyValue("--aspire-popup-shadow"),
            font: "var(--fontSizeBase200)/var(--lineHeightBase200) var(--fontFamilyBase)",
            padding: "8px 12px",
        };
        const renderScrollbar = createDefaultScrollbarRenderer({
            // The overlay shares the terminal palette rather than the surrounding page surface.
            track: {
                color: forced ? color("Canvas") : palette.background,
                opacity: forced || state.moreContrast.matches ? 1 : 0.35,
            },
            thumb: {
                color: forced ? color("CanvasText") : palette.foreground,
            },
            markers: {
                color: color(forced ? "Highlight" : "var(--colorBrandForeground1)"),
                errorColor: color(forced ? "LinkText" : "var(--error)"),
                opacity: forced || state.moreContrast.matches ? 1 : 0.65,
            },
        });
        return {
            placement: "overlay",
            markers: true,
            tooltip(context) {
                // Preserve upstream text escaping, async detail states and positioning.
                const element = renderDefaultScrollbarTooltip(context);
                Object.assign(element.style, tooltipStyle);
                return element;
            },
            render(frame) {
                // Hex1b paints a thumb outline after pointer release because the scrollbar
                // retains focus. Suppress only that paint; its DOM :focus-visible outline
                // still identifies keyboard focus, and real interaction state is unchanged.
                return renderScrollbar({
                    ...frame,
                    interaction: { ...frame.interaction, focused: false },
                });
            },
        };
    } finally {
        probe.remove();
    }
}

export function getTerminalPalette() {
    try {
        const stored = localStorage.getItem(TERMINAL_PALETTE_STORAGE_KEY);
        if (stored === null) {
            return "dark";
        }
        // This non-sensitive browser preference is stored as JSON, e.g. "dark".
        const value = JSON.parse(stored);
        if (value === "dark" || value === "light") {
            return value;
        }
        // The former theme-following preference now resolves to the default explicit palette.
        if (value === "dashboard") {
            return "dark";
        }
        console.warn("Invalid Dashboard terminal palette preference; using Dark.");
    } catch (error) {
        console.warn("Could not read Dashboard terminal palette preference; using Dark.", error);
    }
    return "dark";
}

export function setTerminalPalette(value) {
    if (value !== "dark" && value !== "light") {
        throw new TypeError("Invalid terminal palette preference.");
    }
    // Persist before applying so a failed write can be reported without a false success.
    localStorage.setItem(TERMINAL_PALETTE_STORAGE_KEY, JSON.stringify(value));
    for (const state of terminals.values()) {
        updateAppearance(state);
    }
}

function updateAppearance(state) {
    if (state.disposed) {
        return;
    }
    const preference = getTerminalPalette();
    state.palette = preference;
    state.colorMode = preference;
    const palette = state.colorMode === "light" ? lightPalette : darkPalette;
    state.viewElement.style.setProperty("--terminal-background", palette.background);
    state.viewElement.style.setProperty("--terminal-foreground", palette.foreground);
    state.client?.setColorMode(state.colorMode);
    state.scrollbar = scrollbarConfiguration(state);
    // setScrollbar replaces, rather than merges, the configuration.
    state.client?.setScrollbar(state.scrollbar);
    notifyToolbar(state);
}

export function setPaletteFromHost(id, value) {
    const state = terminals.get(id);
    if (!state || state.disposed) {
        return false;
    }
    let saved = false;
    try {
        // Palette is a local presentation preference, even for read-only or disconnected terminals.
        setTerminalPalette(value);
        saved = true;
        if (state.error === "palette-failed") {
            state.error = null;
        }
    } catch (error) {
        console.warn("Dashboard terminal palette save failed.", error);
        state.error = "palette-failed";
    }
    // Restore the authoritative selection after a failed save, even if the rest of the state is unchanged.
    refreshToolbarState(id);
    return saved;
}

function isCurrent(state, generation) {
    return !state.disposed && state.generation === generation;
}

function isVisible(state) {
    return state.element.clientWidth > 0 && state.element.clientHeight > 0;
}

function requestFocus(state) {
    state.focusPending = !state.readOnly && !state.ended;
    state.focusOrigin = document.activeElement;
}

function applyPendingFocus(state) {
    // Inactive dock panes retain their dimensions for rendering, but must not take keyboard focus.
    if (!state.focusPending || !state.client?.connected || state.readOnly || state.ended ||
        !isVisible(state) || state.element.closest("[inert]") ||
        getComputedStyle(state.element).visibility !== "visible") {
        return;
    }
    state.focusPending = false;
    const activeElement = document.activeElement;
    // Mounting can take time. Honor a user who moved to another control while awaiting the first frame.
    if (!activeElement || activeElement === document.body || activeElement === state.focusOrigin ||
        state.element.contains(activeElement)) {
        state.client.focus();
    }
    state.focusOrigin = null;
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
    state.inspectionObserver?.disconnect();
    state.inspectionObserver = null;
    if (state.client?.element.contains(document.activeElement)) {
        requestFocus(state);
    }
    const controller = state.controller;
    const client = state.client;
    state.controller = null;
    state.client = null;
    controller?.abort();
    client?.dispose();
}

function configureTerminalChrome(client) {
    // This pinned Hex1b release has no options/parts for its history chrome or
    // pointer focus outline, or unused-space styling. Keep keyboard focus, errors and selection feedback;
    // remove these overrides when upstream exposes controls for them.
    const shadow = client.element.shadowRoot;
    const status = shadow?.querySelector(".inspection-message");
    if (!status || !shadow.querySelector(".return-live")) {
        throw new Error("Hex1b history chrome could not be configured.");
    }
    const style = document.createElement("style");
    // Paint the edge inside the existing padding so it neither resizes nor overlaps terminal cells.
    // Keep transparent default cells solid; only the unused viewport is hatched.
    style.textContent = `
        .return-live { display: none !important; }
        .inspection-message[data-aspire-history-position] { display: none !important; }
        :host([data-aspire-pointer-input="true"]) .scrollbar-accessibility:focus-visible { outline: none; }
        .viewport {
            background-color: color-mix(in srgb, var(--terminal-background) 92%, black);
            background-image: repeating-linear-gradient(135deg,
                color-mix(in srgb, var(--terminal-foreground) 10%, transparent) 0 1px,
                transparent 1px 8px);
        }
        .surface {
            background: var(--terminal-background);
            box-shadow:
                0 0 0 ${TERMINAL_PADDING - 0.5}px var(--terminal-background),
                0 0 0 ${TERMINAL_PADDING}px color-mix(in srgb, var(--terminal-foreground) 10%, var(--terminal-background));
        }
        @media (forced-colors: active) {
            .viewport {
                background-color: Canvas;
                background-image: none;
            }
            .surface {
                outline: 1px solid CanvasText;
                outline-offset: ${TERMINAL_PADDING - 1}px;
            }
        }
    `;
    shadow.append(style);
    const update = () => {
        // Hex1b emits "42 rows above live"; other status text includes selection
        // feedback and navigation errors and must not be suppressed.
        status.toggleAttribute("data-aspire-history-position",
            status.dataset.level !== "error" && /^\d+ rows above live$/.test(status.textContent));
    };
    const observer = new MutationObserver(update);
    observer.observe(status, { childList: true, characterData: true, subtree: true, attributes: true, attributeFilter: ["data-level"] });
    update();
    return observer;
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
    // Selection resolution, clipboard permissions and focus changes can all reject a local action
    // without breaking the terminal. Record context, never clipboard/selection text or a reconnect banner.
    const policy = document.permissionsPolicy ?? document.featurePolicy;
    console.log("Dashboard terminal input failed.", error, {
        selectionStatus: state.client?.selection?.status ?? null,
        viewportPending: state.client?.viewport?.pending ?? null,
        secureContext: window.isSecureContext,
        documentFocused: document.hasFocus(),
        visibilityState: document.visibilityState,
        userActivation: navigator.userActivation?.isActive ?? null,
        clipboardReadAvailable: typeof navigator.clipboard?.readText === "function",
        clipboardWriteAvailable: typeof navigator.clipboard?.write === "function",
        clipboardReadAllowedByPolicy: policy?.allowsFeature("clipboard-read") ?? null,
        clipboardWriteAllowedByPolicy: policy?.allowsFeature("clipboard-write") ?? null,
    });
}

function clearInvalidatedSelection(state) {
    const client = state.client;
    if (client?.connected && client.selection.status === "invalidated") {
        // Resize, reflow and output changes can expire the producer's selection.
        // Clear through the public API before the package's expiry message is painted.
        client.clearSelection();
    }
}

function focusAfterMouseControl(state, event) {
    // Keyboard/assistive activation has detail 0; keep focus for repeated keyboard adjustments.
    // https://developer.mozilla.org/en-US/docs/Web/API/Element/click_event#usage_notes
    if (event.detail === 0 || event.button !== 0 || (event.pointerType && event.pointerType !== "mouse")) {
        return;
    }
    const path = event.composedPath();
    const control = path.find(element => element.matches?.(
        ".terminal-font-minus, .terminal-font-plus, .terminal-fit, .terminal-size-select"));
    if (!control || control.disabled ||
        (control.matches(".terminal-size-select") && !path.some(element => element.matches?.("fluent-option")))) {
        return;
    }
    const generation = state.generation;
    const focusOrigin = document.activeElement;
    // Let Fluent finish updating focus after selection. A picker trigger click alone never reaches here.
    requestAnimationFrame(() => {
        if (!isCurrent(state, generation) ||
            (document.activeElement !== focusOrigin && document.activeElement !== document.body &&
                !control.contains(document.activeElement))) {
            return;
        }
        requestFocus(state);
        applyPendingFocus(state);
    });
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
        // Ranges have exclusive end columns; text length differs for wide and combining characters.
        const selectedCells = detail.selection.ranges.reduce(
            (count, range) => count + range.endColumn - range.startColumn, 0);
        const selectable = detail.connected && ["valid", "pending"].includes(detail.selection.status) &&
            selectedCells > 1;
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
    const controls = Array.from(state.footer.querySelectorAll("fluent-button, fluent-select, fluent-dropdown button[role='combobox']"))
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
            colorMode: state.colorMode,
            lightModePalette: lightPalette,
            darkModePalette: darkPalette,
            scrollbar: state.scrollbar,
            padding: TERMINAL_PADDING,
            links: {
                detection: {
                    activation: "modifierClick",
                    rules: [{
                        id: "web",
                        builtin: "url",
                        action: linkAction((_context, activation) => {
                            // Detected destinations are untrusted workload output, not Dashboard URLs.
                            // Keep OSC 8 on Hex1b's default allowlist and authorize detected web URLs here.
                            const url = new URL(activation.target);
                            if (url.protocol !== "https:" && url.protocol !== "http:") {
                                throw new Error("Terminal web links must use HTTP or HTTPS.");
                            }
                            window.open(url.href, "_blank", "noopener,noreferrer");
                        }),
                    }],
                },
            },
            onTitleChange(title) {
                if (current()) {
                    state.title = title;
                    notifyToolbar(state);
                }
            },
            onWorkingDirectoryChange(directory) {
                if (current()) {
                    state.workingDirectory = directory.path;
                    state.workingDirectoryUri = directory.uri;
                    notifyToolbar(state);
                }
            },
            onProgressChange(progress) {
                if (current()) {
                    state.progress = progress;
                    notifyToolbar(state);
                }
            },
            onInput: input => inputPolicy(state, input),
            onClose(details) {
                if (current()) {
                    connectionClosed(state, generation, details);
                }
            },
            onSelectionUI: createSelectionUI(state, current),
            onSelectionChange() {
                if (current()) {
                    clearInvalidatedSelection(state);
                }
            },
            // Force WebGL2 until Firefox's WebGPU terminal performance issue is resolved.
            // https://bugzilla.mozilla.org/show_bug.cgi?id=1870699
            // Match Firefox/142.0 even when WebGPU is available; iOS FxiOS/142.0 uses WebKit, not Gecko.
            // Other browsers let the package choose WebGL2 on ordinary HTTP/unavailable WebGPU;
            // unexpected initialization and runtime rendering errors still surface.
            // https://github.com/mitchdenny/hex1b/pull/491
            renderer: /\bFirefox\//.test(navigator.userAgent) ? "webgl2" : "auto",
            onStatus(message, level) {
                if (!current() || state.ended || level !== "error") {
                    return;
                }
                if (state.client?.connected) {
                    console.warn("Dashboard terminal status error.", message);
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
        state.inspectionObserver = configureTerminalChrome(client);
        // Theme/accessibility settings can change while awaiting the first frame.
        client.setColorMode(state.colorMode);
        client.setScrollbar(state.scrollbar);
        // Selection notifications can precede mount completion, before the handle is available.
        clearInvalidatedSelection(state);
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
        applyPendingFocus(state);
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
        viewElement: element.closest(".terminal-view"),
        readOnly: !!options.readOnly,
        autoFit: !!options.autoFit,
        client: null,
        inspectionObserver: null,
        controller: null,
        disposed: false,
        ended: false,
        connected: false,
        title: "",
        workingDirectory: null,
        workingDirectoryUri: null,
        progress: { state: "none", percentage: null },
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
        focusPending: !options.readOnly,
        focusOrigin: document.activeElement,
        failurePending: false,
        listeners: new AbortController(),
        forcedColors: window.matchMedia("(forced-colors: active)"),
        moreContrast: window.matchMedia("(prefers-contrast: more)"),
    };
    updateAppearance(state);
    state.themeObserver = new MutationObserver(() => updateAppearance(state));
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    // FluentSelect places AriaLabel on the dropdown host, not its generated combobox button.
    // Forward it until Fluent propagates this attribute; observe child creation because upgrade is async.
    // https://github.com/microsoft/fluentui-blazor/blob/dev/src/Core/Components/List/FluentSelect.razor
    const labelPaletteControl = () => {
        const dropdown = footer.querySelector(".terminal-palette-select fluent-dropdown");
        const label = dropdown?.getAttribute("aria-label");
        if (label) {
            dropdown.querySelector("button[role='combobox']")?.setAttribute("aria-label", label);
        }
    };
    state.footerObserver = new MutationObserver(labelPaletteControl);
    state.footerObserver.observe(footer, { childList: true, subtree: true });
    labelPaletteControl();
    // Other windows (including detached terminals) receive storage events; the writer updates its views above.
    window.addEventListener("storage", event => {
        if (event.storageArea === localStorage && (event.key === TERMINAL_PALETTE_STORAGE_KEY || event.key === null)) {
            updateAppearance(state);
        }
    }, { signal: state.listeners.signal });
    for (const media of [state.forcedColors, state.moreContrast]) {
        media.addEventListener("change", () => updateAppearance(state), { signal: state.listeners.signal });
    }
    // A programmatically focused scrollbar can remain :focus-visible after a mouse
    // drag. Track modality before Hex1b consumes events, without moving actual focus.
    element.addEventListener("pointerdown", () => {
        if (state.client) {
            state.client.element.dataset.aspirePointerInput = "true";
        }
    }, { capture: true, signal: state.listeners.signal });
    element.addEventListener("keydown", () => {
        if (state.client) {
            delete state.client.element.dataset.aspirePointerInput;
        }
    }, { capture: true, signal: state.listeners.signal });
    footer.addEventListener("click", event => focusAfterMouseControl(state, event),
        { signal: state.listeners.signal });
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
            applyPendingFocus(state);
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
    if (state.wsUrl !== wsUrl) {
        state.title = "";
        state.workingDirectory = null;
        state.workingDirectoryUri = null;
        state.progress = { state: "none", percentage: null };
    }
    state.wsUrl = wsUrl;
    state.ended = false;
    state.attempts = 0;
    state.error = null;
    requestFocus(state);
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
    state.themeObserver.disconnect();
    state.footerObserver.disconnect();
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
        state.focusPending = false;
    }
    applyAutoFit(state);
    applyPendingFocus(state);
}

export function dismissError(id) {
    const state = terminals.get(id);
    if (!state || !["input-failed", "sizing-failed", "palette-failed"].includes(state.error)) {
        return;
    }
    // Clipboard/input, sizing and palette failures are local actions, not transport failures.
    state.error = null;
    requestFocus(state);
    applyPendingFocus(state);
    notifyToolbar(state);
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
        title: state.title,
        workingDirectory: state.workingDirectory,
        workingDirectoryUri: state.workingDirectoryUri,
        progressState: state.progress.state,
        progressPercentage: state.progress.percentage,
        sizeMode: state.sizing.mode === "auto" ? "font" : "fixed",
        palette: state.palette,
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
    requestFocus(state);
    if (state.waitingForVisibility) {
        connectClient(state);
    } else {
        applyAutoFit(state);
        // The package observes this container; revealing a view must not reconnect or discard its history.
        state.client?.refreshSelectionUI();
        applyPendingFocus(state);
    }
}
