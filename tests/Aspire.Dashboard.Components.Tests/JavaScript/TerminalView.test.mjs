// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, mock, test } from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const dashboard = new URL("../../../src/Aspire.Dashboard/", import.meta.url);
const assets = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE } = await import(new URL("dist/index.min.js", assets));
const source = await readFile(new URL("Components/Controls/TerminalView.razor.js", dashboard), "utf8");
// Remap the public browser asset import to its checked-in location for Node,
// without changing the adapter implementation under test.
const terminal = await import(`data:text/javascript;base64,${Buffer.from(source.replace(
    '"../../js/hex1b-web-terminal/dist/index.min.js"', JSON.stringify(new URL("dist/index.min.js", assets).href)
)).toString("base64")}`);

let attempts;
let observers;
let timers;
let frames;
let ids;
let snapshots;
let serial;
const globals = new Map();
let originalMount;
let themeObservers;
let mediaQueries;

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    mock.method(console, "warn", () => {});
    mock.method(console, "log", () => {});
    attempts = [];
    observers = [];
    timers = new Map();
    frames = new Map();
    ids = [];
    snapshots = [];
    serial = 0;
    themeObservers = [];
    mediaQueries = new Map();
    setGlobal("window", { isSecureContext: true, matchMedia(query) {
        if (!mediaQueries.has(query)) {
            mediaQueries.set(query, Object.assign(new EventTarget(), { matches: false }));
        }
        return mediaQueries.get(query);
    } });
    setGlobal("navigator", { gpu: {} });
    setGlobal("document", {
        activeElement: null,
        body: { append() {} },
        documentElement: {},
        createElement: tag => tag === "canvas"
            ? { getContext: () => ({ fillStyle: "" }) }
            : { style: {}, remove() {} },
        hasFocus: () => true,
        visibilityState: "visible",
    });
    setGlobal("getComputedStyle", element => ({
        visibility: element.visibility ?? "visible", color: "rgb(100, 100, 100)",
        getPropertyValue: name => ({
            "--terminal-background": "#0d1117",
            "--colorNeutralForeground1": "#ffffff",
        })[name] ?? "none",
    }));
    setGlobal("MutationObserver", class {
        constructor(callback) {
            this.callback = callback;
            this.disconnected = false;
            themeObservers.push(this);
        }
        observe(element, options) { this.element = element; this.options = options; }
        disconnect() { this.disconnected = true; }
    });
    setGlobal("requestAnimationFrame", callback => {
        frames.set(++serial, callback);
        return serial;
    });
    setGlobal("cancelAnimationFrame", id => frames.delete(id));
    setGlobal("setTimeout", (callback, delay) => {
        timers.set(++serial, { callback, delay });
        return serial;
    });
    setGlobal("clearTimeout", id => timers.delete(id));
    setGlobal("ResizeObserver", class {
        constructor(callback) {
            this.callback = callback;
            this.disconnected = false;
            observers.push(this);
        }
        observe(element) { this.element = element; }
        disconnect() { this.disconnected = true; }
    });
    originalMount = WebTerminal.mount;
    WebTerminal.mount = (element, options) => {
        const ready = Promise.withResolvers();
        const status = {
            textContent: "", dataset: { level: "info" }, attributes: new Set(),
            toggleAttribute(name, enabled) {
                if (enabled) this.attributes.add(name);
                else this.attributes.delete(name);
            },
        };
        const shadow = {
            styles: [],
            querySelector: selector => selector === ".inspection-message" ? status : {},
            append(style) { this.styles.push(style.textContent); },
        };
        const client = {
            element: { parentElement: element, shadowRoot: shadow, dataset: {}, contains: value => value === client.element },
            connected: true,
            peer: { id: "browser-1", primaryId: "cli-1", isPrimary: false },
            geometry: { columns: 100, rows: 30 },
            sizing: { ...options.sizing },
            readOnly: options.readOnly,
            readOnlyCalls: [],
            scrollbar: options.scrollbar,
            scrollbarCalls: [],
            sizingCalls: [],
            primaryRequests: 0,
            focusCalls: 0,
            selection: { status: "none" },
            selectionClears: 0,
            selectionRefreshes: 0,
            disposed: false,
            dispose() { this.disposed = true; },
            requestPrimary() { this.primaryRequests++; },
            setSizing(sizing) {
                assert.equal(this.peer.isPrimary, true, "Sizing must wait for confirmed primary");
                this.sizing = sizing;
                this.sizingCalls.push(sizing);
                options.onSizingChange(sizing);
            },
            setReadOnly(readOnly) {
                this.readOnly = readOnly;
                this.readOnlyCalls.push(readOnly);
            },
            setScrollbar(scrollbar) {
                this.scrollbar = scrollbar;
                this.scrollbarCalls.push(scrollbar);
            },
            focus() { this.focusCalls++; document.activeElement = this.element; },
            clearSelection() {
                this.selectionClears++;
                this.selection = { status: "pending", ranges: [], canExtend: false };
                options.onSelectionChange(this.selection);
            },
            refreshSelectionUI() { this.selectionRefreshes++; },
        };
        const attempt = {
            element, options, client,
            resolve() { ready.resolve(client); },
            reject(error = new Error("No first frame")) { ready.reject(error); },
            close(code, reason = "", wasClean = true) {
                client.connected = false;
                options.onClose({ code, reason, wasClean });
            },
            role(primary) {
                client.peer = { ...client.peer, primaryId: primary ? client.peer.id : "cli-1", isPrimary: primary };
                options.onRoleChange(client.peer);
            },
        };
        // Deliberately allow completion after abort to exercise stale async
        // cleanup independently of the package's own cancellation safeguards.
        attempts.push(attempt);
        return ready.promise;
    };
});

afterEach(async () => {
    for (const id of ids) {
        terminal.disposeTerminal(id);
    }
    for (const attempt of attempts) {
        attempt.reject();
    }
    await settle();
    WebTerminal.mount = originalMount;
    mock.restoreAll();
    for (const [name, descriptor] of globals) {
        if (descriptor) {
            Object.defineProperty(globalThis, name, descriptor);
        } else {
            delete globalThis[name];
        }
    }
    globals.clear();
});

function selectionControl() {
    const button = Object.assign(new EventTarget(), {
        attributes: new Map([["id", "template-button"]]),
        setAttribute(name, value) { this.attributes.set(name, value); },
        removeAttribute(name) { this.attributes.delete(name); },
    });
    const nodes = { "fluent-button": button };
    const actions = Object.assign(new EventTarget(), {
        style: {}, offsetWidth: 32, offsetHeight: 32, removed: false,
        querySelector(selector) { return nodes[selector]; },
        contains(element) { return element === button; },
        remove() { this.removed = true; },
    });
    return { actions, button };
}

function selectionEvent(attempt, overrides = {}) {
    const event = new Event("selectionui", { cancelable: true });
    attempt.selectionChildren ??= [];
    Object.defineProperty(event, "detail", { value: {
        connected: true, readOnly: true,
        rects: [{ left: 20, top: 10, width: 60, height: 20 }],
        canvasSize: { width: 800, height: 600 },
        viewport: { pending: false },
        signal: attempt.options.signal,
        overlay: { append: actions => attempt.selectionChildren.push(actions) },
        runAction: () => Promise.resolve("authoritative selection"),
        ...overrides,
        selection: { status: "valid", requestId: 1, text: "authoritative selection", copying: false,
            ranges: [{ row: 0, startColumn: 0, endColumn: 6 }],
            ...overrides.selection },
    } });
    assert.equal(attempt.options.onSelectionUI(event), undefined, "UI ownership must be synchronous");
    assert.equal(event.defaultPrevented, true);
    return event;
}

function mount({ visible = true, dotNetRef, options = {} } = {}) {
    const element = Object.assign(new EventTarget(), {
        clientWidth: visible ? 800 : 0,
        clientHeight: visible ? 600 : 0,
        contains: value => value === element || value?.parentElement === element,
        closest: () => null,
    });
    const controls = [];
    const template = { firstElementChild: { cloneNode() {
        const control = selectionControl();
        controls.push(control);
        return control.actions;
    } } };
    const footerControls = ["terminal-font-minus", "terminal-font-plus", "terminal-fit", "terminal-size-select"].map(className => ({
        disabled: false, tabIndex: 0,
        matches: selector => selector.split(", ").includes(`.${className}`),
        contains(element) { return element === this; },
        focus() { document.activeElement = this; },
    }));
    const footer = Object.assign(new EventTarget(), {
        querySelectorAll: () => footerControls,
        focus() { document.activeElement = this; },
    });
    const viewId = options.viewId ?? `view-${++serial}`;
    const id = terminal.initTerminal(element, "wss://dashboard/api/terminal?resource=app&replica=1",
        dotNetRef ?? { invokeMethodAsync: (name, value) => {
            assert.equal(name, "OnTerminalStateChanged");
            snapshots.push(value);
        } },
        { label: "Localized terminal input", ...options, viewId }, template, footer);
    ids.push(id);
    return { id, element, controls, footer, footerControls, viewId };
}

async function settle() {
    for (let i = 0; i < 10; i++) {
        await Promise.resolve();
        const pending = [...frames.values()];
        frames.clear();
        for (const callback of pending) {
            callback();
        }
    }
}

function retry() {
    assert.equal(timers.size, 1);
    const [id, { callback, delay }] = timers.entries().next().value;
    timers.delete(id);
    callback();
    return delay;
}

test("pointer focus styling ends on keyboard input without changing terminal focus", async () => {
    const { element } = mount();
    const { client } = attempts[0];
    attempts[0].resolve();
    await settle();
    const focusCalls = client.focusCalls;
    const pointer = new Event("pointerdown", { cancelable: true });
    element.dispatchEvent(pointer);
    assert.equal(client.element.dataset.aspirePointerInput, "true");
    assert.equal(pointer.defaultPrevented, false);

    const keyboard = new Event("keydown", { cancelable: true });
    element.dispatchEvent(keyboard);
    assert.equal(client.element.dataset.aspirePointerInput, undefined);
    assert.equal(keyboard.defaultPrevented, false);
    assert.equal(client.focusCalls, focusCalls);
});

test("pointer focus listeners follow replacement clients and are removed on disposal", async () => {
    const { id, element } = mount();
    attempts[0].resolve();
    await settle();
    const original = attempts[0].client;
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other&replica=1");
    attempts[1].resolve();
    await settle();
    const replacement = attempts[1].client;
    element.dispatchEvent(new Event("pointerdown"));
    assert.equal(original.element.dataset.aspirePointerInput, undefined);
    assert.equal(replacement.element.dataset.aspirePointerInput, "true");

    terminal.disposeTerminal(id);
    element.dispatchEvent(new Event("keydown"));
    assert.equal(replacement.element.dataset.aspirePointerInput, "true");
});

for (const [name, rects, position] of [
    ["single line", [{ left: 20, top: 10, width: 60, height: 20 }], { left: "86px", top: "36px" }],
    ["last line rather than bounding box", [
        { left: 10, top: 40, width: 30, height: 20 }, { left: 10, top: 20, width: 300, height: 20 },
    ], { left: "46px", top: "66px" }],
    ["bottom edge", [{ left: 100, top: 580, width: 100, height: 20 }], { left: "206px", top: "542px" }],
    ["right edge", [{ left: 790, top: 10, width: 20, height: 20 }], { left: "768px", top: "36px" }],
    ["clipped history", [
        { left: 20, top: -30, width: 600, height: 20 },
        { left: 20, top: -10, width: 60, height: 20 },
        { left: 20, top: 610, width: 300, height: 20 },
    ], { left: "86px", top: "16px" }],
]) {
    test(`selection copy control anchors to ${name}`, () => {
        const { controls } = mount();
        selectionEvent(attempts[0], { rects });
        const { actions, button } = controls[0];
        assert.deepEqual(actions.style, position);
        assert.equal(actions.hidden, false);
        assert.equal(button.disabled, false);
        assert.equal(button.attributes.has("id"), false, "Cloning must not duplicate the template's id");
        assert.deepEqual(attempts[0].selectionChildren, [actions]);
    });
}

for (const [name, ranges, text, visible] of [
    ["no cells", [], "", false],
    ["one cell", [{ row: 0, startColumn: 3, endColumn: 4 }], "a", false],
    ["one cell with combining characters", [{ row: 0, startColumn: 3, endColumn: 4 }], "e\u0301", false],
    ["two cells", [{ row: 0, startColumn: 3, endColumn: 5 }], "ab", true],
    ["a wide character", [{ row: 0, startColumn: 3, endColumn: 5 }], "\u754c", true],
    ["one cell on each of two lines", [
        { row: 0, startColumn: 99, endColumn: 100 }, { row: 1, startColumn: 0, endColumn: 1 },
    ], "a\nb", true],
]) {
    test(`selection copy control visibility counts ${name}`, () => {
        const { controls } = mount();
        selectionEvent(attempts[0], { selection: { ranges, text } });
        assert.equal(controls[0].actions.hidden, !visible);
        assert.equal(attempts[0].client.selectionClears, 0);
    });
}

test("theme and contrast changes replace the complete overlay configuration without reconnecting", async () => {
    const { id } = mount();
    const attempt = attempts[0];
    const initial = attempt.options.scrollbar;
    assert.equal(initial.placement, "overlay");
    assert.equal(initial.markers, true);
    assert.equal(typeof initial.render, "function");
    assert.equal(themeObservers[0].element, document.documentElement);
    assert.deepEqual(themeObservers[0].options, { attributes: true, attributeFilter: ["data-theme"] });

    // Include a theme change before the asynchronous mount has returned its handle.
    themeObservers[0].callback();
    attempt.resolve();
    await settle();
    assert.notEqual(attempt.client.scrollbar.render, initial.render);
    for (const query of ["(forced-colors: active)", "(prefers-contrast: more)"]) {
        const previous = attempt.client.scrollbar;
        const media = mediaQueries.get(query);
        media.matches = true;
        media.dispatchEvent(new Event("change"));
        assert.notEqual(attempt.client.scrollbar.render, previous.render);
        assert.equal(attempt.client.scrollbar.placement, "overlay");
        assert.equal(attempt.client.scrollbar.markers, true);
    }
    assert.equal(attempts.length, 1);
    assert.deepEqual(attempt.client.sizingCalls, []);
    assert.equal(attempt.client.selectionClears, 0);
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=app&replica=1");
    assert.equal(attempts[1].options.scrollbar, attempt.client.scrollbar);
});

test("theme observers and accessibility listeners are released on disposal", async () => {
    const { id } = mount();
    const attempt = attempts[0];
    attempt.resolve();
    await settle();
    terminal.disposeTerminal(id);
    const calls = attempt.client.scrollbarCalls.length;
    assert.equal(themeObservers[0].disconnected, true);
    themeObservers[0].callback();
    for (const media of mediaQueries.values()) {
        media.dispatchEvent(new Event("change"));
    }
    assert.equal(attempt.client.scrollbarCalls.length, calls);
    assert.equal(attempt.client.disposed, true);
});

test("selection copy control follows the single-cell threshold in both directions", () => {
    const { controls } = mount();
    for (const cells of [1, 2, 1, 0, 3]) {
        selectionEvent(attempts[0], { selection: {
            status: "pending", text: null,
            ranges: [{ row: 0, startColumn: 0, endColumn: cells }],
        } });
        assert.equal(controls[0].actions.hidden, cells <= 1);
        assert.equal(controls[0].button.disabled, true);
    }
    assert.equal(controls.length, 1);
});

test("invalidated selections are cleared without taking focus or reconnecting", async () => {
    const { id } = mount();
    const { client, options } = attempts[0];
    attempts[0].resolve();
    await settle();
    const focusCalls = client.focusCalls;
    for (const status of ["none", "pending", "valid", "unavailable"]) {
        client.selection = { status };
        options.onSelectionChange(client.selection);
        assert.equal(client.selectionClears, 0);
    }
    client.selection = { status: "invalidated" };
    options.onSelectionChange(client.selection);
    assert.equal(client.selectionClears, 1);
    assert.equal(client.selection.status, "pending");
    assert.equal(client.focusCalls, focusCalls);
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(attempts.length, 1);
    assert.equal(timers.size, 0);
});

test("selections invalidated before mount completion are cleared when the handle is available", async () => {
    mount();
    const { client, options } = attempts[0];
    client.selection = { status: "invalidated" };
    options.onSelectionChange(client.selection);
    assert.equal(client.selectionClears, 0);
    attempts[0].resolve();
    await settle();
    assert.equal(client.selectionClears, 1);
});

test("disconnected, stale and disposed selection notifications cannot clear a selection", async () => {
    const { id } = mount();
    const first = attempts[0];
    first.resolve();
    await settle();
    first.client.connected = false;
    first.client.selection = { status: "invalidated" };
    first.options.onSelectionChange(first.client.selection);
    assert.equal(first.client.selectionClears, 0);

    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=next");
    const next = attempts[1];
    next.resolve();
    await settle();
    next.client.selection = { status: "invalidated" };
    first.options.onSelectionChange(first.client.selection);
    assert.equal(next.client.selectionClears, 0);
    terminal.disposeTerminal(id);
    next.options.onSelectionChange(next.client.selection);
    assert.equal(next.client.selectionClears, 0);
});

test("selection controls update in place and hide when no selected text is visible", async () => {
    const { controls } = mount();
    attempts[0].resolve();
    await settle();
    selectionEvent(attempts[0]);
    const { actions, button } = controls[0];
    selectionEvent(attempts[0], { selection: { status: "pending", text: null } });
    assert.equal(actions.hidden, false);
    assert.equal(button.disabled, true);
    selectionEvent(attempts[0], { viewport: { pending: true } });
    assert.equal(button.disabled, true);
    for (const change of [
        { selection: { status: "none" } },
        { selection: { status: "invalidated" } },
        { connected: false },
        { rects: [{ left: 0, top: 700, width: 80, height: 20 }] },
        { canvasSize: { width: 0, height: 0 } },
    ]) {
        selectionEvent(attempts[0], change);
        assert.equal(actions.hidden, true);
    }
    selectionEvent(attempts[0]);
    assert.equal(actions.hidden, false);
    document.activeElement = button;
    Object.defineProperty(actions, "hidden", {
        set(value) {
            if (value) {
                document.activeElement = document.body;
            }
        },
    });
    selectionEvent(attempts[0], { selection: { status: "none" } });
    assert.equal(attempts[0].client.focusCalls, 2);
    assert.equal(controls.length, 1);
});

test("copy dismisses the copied selection and returns focus for immediate terminal paste", async () => {
    const { controls } = mount();
    attempts[0].resolve();
    await settle();
    const copy = Promise.withResolvers();
    const calls = [];
    selectionEvent(attempts[0], { runAction: (...args) => { calls.push(args); return copy.promise; } });
    const { actions, button } = controls[0];
    const pointer = new Event("pointerdown", { cancelable: true });
    actions.dispatchEvent(pointer);
    assert.equal(pointer.defaultPrevented, true);
    document.activeElement = button;
    button.dispatchEvent(new Event("click"));
    button.dispatchEvent(new Event("click"));
    assert.deepEqual(calls, [["copySelection"]]);
    assert.equal(button.disabled, false, "Busy copying must not blur keyboard focus");
    assert.equal(button.attributes.get("aria-disabled"), "true");
    assert.equal(button.attributes.get("aria-busy"), "true");
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.equal(attempts[0].client.selectionClears, 0);
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(actions.hidden, false);
    copy.resolve("<untrusted selected text>");
    await settle();
    assert.equal(button.disabled, false);
    assert.equal(button.attributes.get("aria-disabled"), "false");
    assert.equal(button.attributes.get("aria-busy"), "false");
    assert.equal(actions.hidden, true);
    assert.equal(attempts[0].client.selectionClears, 1);
    assert.equal(attempts[0].client.focusCalls, 2);
    assert.equal(document.activeElement, attempts[0].client.element);
    selectionEvent(attempts[0], { selection: { requestId: 2 } });
    assert.equal(actions.hidden, false);
});

test("selection controls clamp within a small canvas and follow updated CSS-pixel geometry", () => {
    const { controls } = mount();
    selectionEvent(attempts[0], {
        rects: [{ left: 0, top: 20, width: 48, height: 20 }],
        canvasSize: { width: 48, height: 40 },
    });
    assert.deepEqual(controls[0].actions.style, { left: "16px", top: "0px" });
    selectionEvent(attempts[0], {
        rects: [{ left: 0, top: 0, width: 145.25, height: 32.5 }],
        canvasSize: { width: 1291.5, height: 775 },
    });
    assert.deepEqual(controls[0].actions.style, { left: "151.25px", top: "38.5px" });
    assert.equal(controls.length, 1);
});

test("copy failures are console-only and leave the selection available for retry", async () => {
    const { id, controls } = mount();
    attempts[0].resolve();
    await settle();
    const error = new Error("Clipboard unavailable");
    selectionEvent(attempts[0], { runAction: () => Promise.reject(error) });
    controls[0].button.dispatchEvent(new Event("click"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(console.log.mock.calls.at(-1).arguments[1], error);
    assert.equal(controls[0].button.disabled, false);
    assert.equal(controls[0].actions.hidden, false);
    assert.equal(attempts[0].client.selectionClears, 0);
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(attempts.length, 1);
    assert.equal(timers.size, 0);
    selectionEvent(attempts[0]);
    controls[0].button.dispatchEvent(new Event("click"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(controls[0].actions.hidden, true);
    assert.equal(attempts[0].client.selectionClears, 1);
    assert.equal(attempts[0].client.focusCalls, 2);
});

test("changing selection while copying does not dismiss the new selection or steal focus", async () => {
    const { controls } = mount();
    const copy = Promise.withResolvers();
    selectionEvent(attempts[0], { runAction: () => copy.promise });
    controls[0].button.dispatchEvent(new Event("click"));
    selectionEvent(attempts[0], { selection: { requestId: 2, text: "new selection" } });
    copy.resolve("old selection");
    await settle();
    assert.equal(controls[0].actions.hidden, false);
    assert.equal(attempts[0].client.selectionClears, 0);
    assert.equal(attempts[0].client.focusCalls, 0);
});

test("reconnect removes selection controls, listeners and stale clipboard callbacks", async () => {
    const { id, controls } = mount();
    const copy = Promise.withResolvers();
    let calls = 0;
    selectionEvent(attempts[0], { runAction: () => { calls++; return copy.promise; } });
    controls[0].button.dispatchEvent(new Event("click"));
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=next");
    assert.equal(controls[0].actions.removed, true);
    controls[0].button.disabled = false;
    controls[0].button.dispatchEvent(new Event("click"));
    assert.equal(calls, 1);
    copy.reject(new Error("Old connection"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, null);
    selectionEvent(attempts[1]);
    assert.equal(controls.length, 2);
    assert.equal(controls[1].actions.removed, false);
    terminal.disposeTerminal(id);
    assert.equal(controls[1].actions.removed, true);
});

test("metadata before mount completion is coalesced and updates read-only views", async () => {
    const { id } = mount({ options: { readOnly: true } });
    const { options } = attempts[0];
    options.onTitleChange("build <app>");
    options.onWorkingDirectoryChange({ uri: "file://host/work/my%20app", host: "host", path: "/work/my app" });
    options.onProgressChange({ state: "normal", percentage: 42 });
    attempts[0].resolve();
    await settle();
    assert.equal(snapshots.length, 1);
    assert.equal(snapshots[0].title, "build <app>");
    assert.equal(snapshots[0].workingDirectory, "/work/my app");
    assert.equal(snapshots[0].workingDirectoryUri, "file://host/work/my%20app");
    assert.equal(snapshots[0].progressState, "normal");
    assert.equal(snapshots[0].progressPercentage, 42);
    for (const progress of [
        { state: "indeterminate", percentage: null },
        { state: "error", percentage: 30 },
        { state: "warning", percentage: 50 },
        { state: "none", percentage: null },
    ]) {
        options.onProgressChange(progress);
        await settle();
        assert.equal(snapshots.at(-1).progressState, progress.state);
        assert.equal(snapshots.at(-1).progressPercentage, progress.percentage);
    }
    options.onTitleChange("");
    options.onWorkingDirectoryChange({ uri: null, host: null, path: null });
    await settle();
    assert.equal(terminal.getToolbarState(id).title, "");
    assert.equal(terminal.getToolbarState(id).workingDirectory, null);
    assert.equal(attempts[0].client.primaryRequests, 0);
});

test("metadata survives transport loss but not endpoint replacement or stale callbacks", async () => {
    const { id } = mount();
    const old = attempts[0];
    old.options.onTitleChange("old title");
    old.options.onWorkingDirectoryChange({ uri: "file:///old", host: "", path: "/old" });
    old.options.onProgressChange({ state: "normal", percentage: 20 });
    old.resolve();
    await settle();
    old.close(1006);
    await settle();
    assert.equal(terminal.getToolbarState(id).title, "old title");
    assert.equal(terminal.getToolbarState(id).connected, false);
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=new");
    const expected = terminal.getToolbarState(id);
    assert.equal(expected.title, "");
    assert.equal(expected.workingDirectory, null);
    assert.equal(expected.progressState, "none");
    old.options.onTitleChange("stale title");
    old.options.onWorkingDirectoryChange({ uri: "file:///stale", host: "", path: "/stale" });
    old.options.onProgressChange({ state: "error", percentage: 99 });
    assert.deepEqual(terminal.getToolbarState(id), expected);
    terminal.disposeTerminal(id);
    attempts[1].options.onTitleChange("disposed");
    assert.equal(terminal.getToolbarState(id), null);
});

test("history chrome suppression preserves errors and selection feedback and disconnects on release", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    const shadow = attempts[0].client.element.shadowRoot;
    const status = shadow.querySelector(".inspection-message");
    const observer = themeObservers.find(observer => observer.element === status);
    assert.ok(observer);
    assert.match(shadow.styles[0], /\.return-live \{ display: none !important; \}/);
    for (const [text, level, hidden] of [
        ["42 rows above live", "info", true],
        ["0 rows above live", "info", true],
        ["Selection copied", "info", false],
        ["Navigation rejected", "error", false],
        ["42 rows above live", "error", false],
        ["", "info", false],
    ]) {
        status.textContent = text;
        status.dataset.level = level;
        observer.callback();
        assert.equal(status.attributes.has("data-aspire-history-position"), hidden);
    }
    terminal.reconnectTerminal(id, attempts[0].options.url);
    assert.equal(observer.disconnected, true);
    attempts[1].resolve();
    await settle();
    const replacement = themeObservers.at(-1);
    terminal.disposeTerminal(id);
    assert.equal(replacement.disconnected, true);
});

test("init returns an id while mount waits for its first connected frame", async () => {
    const { id } = mount();
    assert.equal(terminal.getToolbarState(id).connected, false);
    assert.equal(attempts[0].options.label, "Localized terminal input");
    assert.equal(attempts[0].options.url, "wss://dashboard/api/terminal?resource=app&replica=1");
    assert.equal(attempts[0].options.renderer, "auto");
    assert.equal(attempts[0].options.padding, 3);
    attempts[0].options.onStatus("Socket open", "ready");
    attempts[0].role(false);
    await settle();
    assert.equal(snapshots.at(-1).connected, false);
    attempts[0].resolve();
    await settle();
    assert.deepEqual(snapshots.at(-1), {
        terminalId: id, generation: 1, status: "viewer", connected: true,
        title: "", workingDirectory: null, workingDirectoryUri: null,
        progressState: "none", progressPercentage: null,
        isPrimary: false, canTakeControl: true, sizeMode: "font", sizeKey: "100x30",
        fontPx: 13, fontControlsEnabled: true, sizeSelectEnabled: true,
        fitEnabled: true,
        canDecreaseFontSize: true, canIncreaseFontSize: true,
        cols: 100, rows: 30, error: null,
    });
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.equal(typeof attempts[0].options.onInput, "function");
    assert.equal(attempts[0].options.inputBindings, undefined);
    assert.equal(attempts[0].options.actions, undefined);
    assert.equal(attempts[0].options.readOnly, false);
});

test("opening a terminal focuses input after the first frame without taking primary", async () => {
    document.activeElement = { tagName: "BUTTON" };
    mount();
    assert.equal(attempts[0].client.focusCalls, 0);
    attempts[0].resolve();
    await settle();
    assert.equal(document.activeElement, attempts[0].client.element);
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(attempts[0].client.primaryRequests, 0);

    const otherControl = { tagName: "INPUT" };
    document.activeElement = otherControl;
    observers[0].callback();
    attempts[0].role(true);
    await settle();
    assert.equal(document.activeElement, otherControl);
    assert.equal(attempts[0].client.focusCalls, 1);
});

test("a delayed mount does not steal focus from a newly selected control", async () => {
    document.activeElement = { tagName: "BUTTON" };
    mount();
    const otherControl = { tagName: "INPUT" };
    document.activeElement = otherControl;
    attempts[0].resolve();
    await settle();
    observers[0].callback();
    assert.equal(document.activeElement, otherControl);
    assert.equal(attempts[0].client.focusCalls, 0);
});

test("a mount becoming ready after another terminal does not steal its focus", async () => {
    mount();
    mount();
    attempts[1].resolve();
    await settle();
    attempts[0].resolve();
    await settle();
    assert.equal(document.activeElement, attempts[1].client.element);
    assert.equal(attempts[0].client.focusCalls, 0);
});

test("inactive dock panes wait for activation before focusing and do not remount", async () => {
    const { id, element } = mount({ options: { showDimensions: false } });
    const pane = {};
    element.closest = selector => selector === "[inert]" ? pane : null;
    attempts[0].resolve();
    await settle();
    assert.equal(attempts[0].client.focusCalls, 0);

    element.closest = () => null;
    document.activeElement = { tagName: "BUTTON" };
    terminal.setAutoFit(id, true);
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(document.activeElement, attempts[0].client.element);
    terminal.setAutoFit(id, true);
    observers[0].callback();
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(attempts.length, 1);
});

test("hidden and read-only terminals do not take focus", async () => {
    const hidden = mount();
    hidden.element.visibility = "hidden";
    const readOnly = mount({ options: { readOnly: true } });
    attempts[0].resolve();
    attempts[1].resolve();
    await settle();
    assert.equal(attempts[0].client.focusCalls, 0);
    assert.equal(attempts[1].client.focusCalls, 0);
    terminal.refreshLayout(readOnly.id);
    assert.equal(attempts[1].client.focusCalls, 0);

    hidden.element.visibility = "visible";
    terminal.refreshLayout(hidden.id);
    assert.equal(attempts[0].client.focusCalls, 1);
    assert.equal(attempts.length, 2);
});

test("returning to the terminal view restores focus without replacing its client", async () => {
    const { id, element } = mount();
    attempts[0].resolve();
    await settle();
    element.clientWidth = 0;
    document.activeElement = { tagName: "BUTTON" };
    terminal.refreshLayout(id);
    assert.equal(attempts[0].client.focusCalls, 1);
    element.clientWidth = 800;
    terminal.refreshLayout(id);
    assert.equal(document.activeElement, attempts[0].client.element);
    assert.equal(attempts[0].client.focusCalls, 2);
    assert.equal(attempts.length, 1);
});

test("missing WebGPU and ordinary HTTP leave renderer selection to the package", async () => {
    navigator.gpu = undefined;
    const first = mount();
    navigator.gpu = {};
    window.isSecureContext = false;
    const second = mount();
    assert.equal(attempts.length, 2);
    for (const attempt of attempts) {
        assert.equal(attempt.options.renderer, "auto");
        attempt.resolve();
    }
    await settle();
    assert.equal(timers.size, 0);
    assert.equal(terminal.getToolbarState(first.id).connected, true);
    assert.equal(terminal.getToolbarState(second.id).connected, true);
    assert.equal(terminal.getToolbarState(first.id).error, null);
    assert.equal(terminal.getToolbarState(second.id).error, null);
});

for (const [browser, userAgent, renderer] of [
    ["Firefox desktop", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:142.0) Gecko/20100101 Firefox/142.0", "webgl2"],
    ["Firefox Android", "Mozilla/5.0 (Android 15; Mobile; rv:142.0) Gecko/142.0 Firefox/142.0", "webgl2"],
    ["Firefox iOS", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/142.0 Mobile/15E148 Safari/605.1.15", "webgl2"],
    ["Chrome", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36", "auto"],
    ["Edge", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0", "auto"],
    ["Safari", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "auto"],
]) {
    test(`${browser} mounts with ${renderer} regardless of WebGPU availability or secure context`, async () => {
        navigator.userAgent = userAgent;
        for (const secureContext of [true, false]) {
            window.isSecureContext = secureContext;
            for (const gpu of [{}, undefined]) {
                navigator.gpu = gpu;
                const { id } = mount();
                const attempt = attempts.at(-1);
                assert.equal(attempt.options.renderer, renderer);
                attempt.resolve();
                await settle();
                assert.equal(terminal.getToolbarState(id).connected, true);
            }
        }
    });
}

test("Firefox keeps WebGL2 on automatic retries and explicit reconnects", async () => {
    navigator.userAgent = "Mozilla/5.0 (X11; Linux x86_64; rv:142.0) Gecko/20100101 Firefox/142.0";
    const { id } = mount();
    assert.equal(attempts[0].options.renderer, "webgl2");
    attempts[0].reject();
    await settle();
    assert.equal(retry(), 500);
    assert.equal(attempts[1].options.renderer, "webgl2");
    attempts[1].resolve();
    await settle();
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other&replica=2");
    assert.equal(attempts[2].options.renderer, "webgl2");
    attempts[2].resolve();
    await settle();
    assert.equal(terminal.getToolbarState(id).connected, true);
    assert.equal(timers.size, 0);
});

test("hidden initial mounts wait for visibility without consuming the first-frame timeout", async () => {
    const { id, element } = mount({ visible: false });
    assert.equal(attempts.length, 0);
    element.clientWidth = 800;
    element.clientHeight = 600;
    terminal.refreshLayout(id);
    assert.equal(attempts.length, 1);
    observers[0].callback();
    assert.equal(attempts.length, 1);
    attempts[0].resolve();
    await settle();
    element.clientWidth = 0;
    terminal.refreshLayout(id);
    element.clientWidth = 800;
    terminal.refreshLayout(id);
    assert.equal(attempts.length, 1);
    assert.equal(attempts[0].client.selectionRefreshes, 1);
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
});

test("mount failure reports an error and retries with a fresh abortable generation", async () => {
    const { id } = mount();
    attempts[0].reject();
    await settle();
    assert.equal(snapshots.at(-1).error, "mount-failed");
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(retry(), 500);
    assert.equal(terminal.getToolbarState(id).generation, 2);
    assert.equal(attempts.length, 2);
    attempts[1].resolve();
    await settle();
    assert.equal(snapshots.at(-1).error, null);
    assert.equal(snapshots.at(-1).connected, true);
});

test("resource reconnect aborts pending mount and ignores late completion and callbacks", async () => {
    const { id } = mount();
    assert.equal(terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other&replica=2"), 2);
    assert.equal(attempts[0].options.signal.aborted, true);
    attempts[1].resolve();
    await settle();
    const expected = terminal.getToolbarState(id);
    attempts[0].resolve();
    attempts[0].role(true);
    attempts[0].options.onGeometry({ columns: 20, rows: 10 });
    attempts[0].options.onStatus("old socket closed", "error");
    await settle();
    assert.equal(attempts[0].client.disposed, true);
    assert.deepEqual(terminal.getToolbarState(id), expected);
    assert.equal(timers.size, 0);
});

test("a disconnect schedules only one retry and restores focus only if still appropriate", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    document.activeElement = attempts[0].client.element;
    attempts[0].client.connected = false;
    attempts[0].options.onStatus("closed", "error");
    attempts[0].options.onStatus("closed again", "error");
    await settle();
    assert.equal(timers.size, 1);
    assert.equal(attempts[0].client.disposed, true);
    retry();
    document.activeElement = document.body;
    attempts[1].resolve();
    await settle();
    assert.equal(attempts[1].client.focusCalls, 1);
    assert.equal(terminal.getToolbarState(id).connected, true);
});

test("sizing requests primary, waits for confirmation, and clamps to the public font limits", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.setFontSizeFromHost(id, 72);
    assert.equal(attempts[0].client.primaryRequests, 1);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    assert.equal(terminal.getToolbarState(id).isPrimary, false);
    attempts[0].role(true);
    assert.deepEqual(attempts[0].client.sizingCalls, [{ mode: "auto", fontSize: 32 }]);
    terminal.setFontSizeFromHost(id, 4);
    terminal.setSizeModeFromHost(id, "132x50");
    terminal.setSizeModeFromHost(id, "not-a-preset");
    assert.deepEqual(attempts[0].client.sizingCalls, [
        { mode: "auto", fontSize: 32 },
        { mode: "auto", fontSize: 8 },
        { mode: "fixed", columns: 132, rows: 50, fontSize: 8 },
    ]);
    // Geometry remains producer-authoritative; a request cannot rewrite it.
    assert.equal(terminal.getToolbarState(id).cols, 100);
    assert.equal(terminal.getToolbarState(id).sizeKey, "132x50");
    assert.equal(terminal.getToolbarState(id).fontControlsEnabled, false);
});

for (const error of [
    new DOMException("Read permission denied.", "NotAllowedError"),
    new Error("Resolving selection\u2026"),
    new Error("Timed out resolving selection. Copy again."),
    new Error("Clipboard unavailable"),
    new Error("Terminal input, selection, focus, or buffer changed while reading the clipboard. Paste again."),
]) {
    test(`input failure is console-only: ${error.message}`, async () => {
        const { element } = mount();
        attempts[0].resolve();
        await settle();
        const client = attempts[0].client;
        client.selection = { status: "pending", text: "Private selected text" };
        client.viewport = { pending: false };
        const before = terminal.getTerminalSnapshot(element);
        attempts[0].options.onInputError(error);
        await settle();
        assert.deepEqual(terminal.getTerminalSnapshot(element), before);
        assert.equal(snapshots.at(-1).error, null);
        assert.deepEqual(console.log.mock.calls.at(-1).arguments, ["Dashboard terminal input failed.", error, {
            selectionStatus: "pending",
            viewportPending: false,
            secureContext: true,
            documentFocused: true,
            visibilityState: "visible",
            userActivation: null,
            clipboardReadAvailable: false,
            clipboardWriteAvailable: false,
            clipboardReadAllowedByPolicy: null,
            clipboardWriteAllowedByPolicy: null,
        }]);
        assert.equal(client.disposed, false);
        assert.equal(client.focusCalls, 1);
        assert.equal(client.selectionClears, 0);
        assert.equal(client.primaryRequests, 0);
        assert.equal(timers.size, 0);
    });
}

test("input diagnostics distinguish browser policy and focus without reading the clipboard", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    document.hasFocus = () => false;
    document.visibilityState = "hidden";
    document.featurePolicy = { allowsFeature: feature => feature === "clipboard-write" };
    navigator.userActivation = { isActive: false };
    navigator.clipboard = {
        readText() { assert.fail("Diagnostics must not read the clipboard"); },
        write() { assert.fail("Diagnostics must not change the clipboard"); },
    };
    attempts[0].options.onInputError(new DOMException("Read permission denied.", "NotAllowedError"));
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.deepEqual(console.log.mock.calls.at(-1).arguments[2], {
        selectionStatus: "none",
        viewportPending: null,
        secureContext: true,
        documentFocused: false,
        visibilityState: "hidden",
        userActivation: false,
        clipboardReadAvailable: true,
        clipboardWriteAvailable: true,
        clipboardReadAllowedByPolicy: false,
        clipboardWriteAllowedByPolicy: true,
    });
});

test("selection copy permission denial is logged without covering the terminal", async () => {
    const { controls, element } = mount();
    attempts[0].resolve();
    await settle();
    const error = new DOMException("Write permission denied.", "NotAllowedError");
    selectionEvent(attempts[0], { runAction: () => Promise.reject(error) });
    controls[0].button.dispatchEvent(new Event("click"));
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).error, null);
    assert.equal(controls[0].actions.hidden, false);
    assert.equal(attempts[0].client.selectionClears, 0);
    assert.deepEqual(console.log.mock.calls.at(-1).arguments.slice(0, 2), ["Dashboard terminal input failed.", error]);
});

test("clipboard permission denial does not clear an existing sizing error", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].client.requestPrimary = () => { throw new Error("Resize failed"); };
    terminal.fitToContainer(id);
    attempts[0].options.onInputError(new DOMException("Read permission denied.", "NotAllowedError"));
    assert.equal(terminal.getToolbarState(id).error, "sizing-failed");
});

test("terminal status errors remain visible and dismiss without replacing the client", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    const client = attempts[0].client;
    client.screenText = "Retained terminal output";
    attempts[0].options.onStatus("Selection UI failed: invalid control", "error");
    await settle();
    const before = terminal.getTerminalSnapshot(attempts[0].element);
    assert.equal(before.error, "input-failed");
    document.activeElement = { tagName: "BUTTON" };

    terminal.dismissError(id);
    await settle();

    assert.deepEqual(terminal.getTerminalSnapshot(attempts[0].element), { ...before, error: null });
    assert.equal(snapshots.at(-1).error, null);
    assert.equal(document.activeElement, client.element);
    assert.equal(client.disposed, false);
    assert.equal(client.selectionClears, 0);
    assert.equal(client.primaryRequests, 0);
    assert.equal(attempts.length, 1);
    assert.equal(timers.size, 0);
});

test("dismissing a sizing error keeps the existing connection", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].client.requestPrimary = () => { throw new Error("Resize failed"); };
    terminal.fitToContainer(id);
    assert.equal(terminal.getToolbarState(id).error, "sizing-failed");
    terminal.dismissError(id);
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(attempts.length, 1);
});

test("a delayed dismiss cannot hide a connection failure or cancel its retry", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].options.onInputError(new Error("Clipboard unavailable"));
    attempts[0].close(1006);
    await settle();
    terminal.dismissError(id);
    assert.equal(terminal.getToolbarState(id).error, "mount-failed");
    assert.equal(timers.size, 1);
    assert.equal(attempts[0].client.disposed, true);
});

test("remote role changes authoritatively switch primary, viewer and unclaimed states", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    assert.equal(terminal.getToolbarState(id).status, "primary");
    assert.equal(terminal.getToolbarState(id).canTakeControl, false);
    attempts[0].role(false);
    assert.equal(terminal.getToolbarState(id).status, "viewer");
    assert.equal(terminal.getToolbarState(id).isPrimary, false);
    assert.equal(terminal.getToolbarState(id).canTakeControl, true);
    attempts[0].options.onRoleChange({ id: "browser-1", primaryId: null, isPrimary: false });
    assert.equal(terminal.getToolbarState(id).status, "no-primary");
    assert.equal(terminal.getToolbarState(id).canTakeControl, true);
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
});

test("dispose aborts pending mount, cancels queued work and ignores later results", async () => {
    const { id } = mount();
    terminal.disposeTerminal(id);
    attempts[0].resolve();
    await settle();
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(attempts[0].client.disposed, true);
    assert.equal(observers[0].disconnected, true);
    assert.equal(terminal.getToolbarState(id), null);
    assert.equal(timers.size, 0);
    assert.deepEqual(snapshots, []);
});

test("rejected Blazor notifications do not become unhandled rejections", async () => {
    const { id } = mount({ dotNetRef: { invokeMethodAsync: () => Promise.reject(new Error("Circuit disposed")) } });
    attempts[0].resolve();
    await settle();
    terminal.refreshToolbarState(id);
    await settle();
    assert.equal(terminal.getToolbarState(id).connected, true);
});

test("explicit reconnect cancels the automatic retry and drops a pending sizing request", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.setSizeModeFromHost(id, "80x24");
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other");
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(attempts[0].client.disposed, true);
    attempts[0].role(true);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    attempts[1].reject();
    await settle();
    assert.equal(timers.size, 1);
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=third");
    assert.equal(timers.size, 0);
    attempts[2].resolve();
    await settle();
    assert.equal(terminal.getToolbarState(id).generation, 3);
    assert.equal(terminal.getToolbarState(id).sizeKey, "100x30");
});

test("automatic retries are bounded and explicit reconnect resets the exhausted budget", async () => {
    const { id } = mount();
    for (let i = 0; i <= 30; i++) {
        attempts.at(-1).reject();
        await settle();
        if (i < 30) {
            retry();
        }
    }
    assert.equal(attempts.length, 31);
    assert.equal(timers.size, 0);
    assert.equal(terminal.getToolbarState(id).error, "disconnected");
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=app");
    attempts.at(-1).reject();
    await settle();
    assert.equal(retry(), 500);
});

function clickFooter(footer, control, { selectOption = false, ...options } = {}) {
    const option = { matches: selector => selector === "fluent-option" };
    const event = Object.assign(new Event("click"), {
        button: 0, detail: 1, pointerType: "mouse",
        composedPath: () => selectOption ? [option, control, footer] : [control, footer],
        ...options,
    });
    footer.dispatchEvent(event);
}

for (const [name, index] of [["font decrease", 0], ["font increase", 1], ["Fit", 2], ["dimensions", 3]]) {
    test(`mouse activation of ${name} returns focus while keyboard activation leaves it in place`, async () => {
        const { footer, footerControls } = mount();
        attempts[0].resolve();
        await settle();
        const control = footerControls[index];
        control.focus();
        clickFooter(footer, control, { selectOption: index === 3 });
        await settle();
        assert.equal(document.activeElement, attempts[0].client.element);
        assert.equal(attempts[0].client.focusCalls, 2);

        for (let i = 0; i < 2; i++) {
            control.focus();
            clickFooter(footer, control, { selectOption: index === 3, detail: 0, pointerType: "" });
            await settle();
            assert.equal(document.activeElement, control);
        }
        assert.equal(attempts[0].client.focusCalls, 2);
    });
}

test("the dimensions picker keeps focus while open and returns it after mouse selection", async () => {
    const { footer, footerControls } = mount();
    attempts[0].resolve();
    await settle();
    const select = footerControls[3];
    select.focus();
    clickFooter(footer, select);
    await settle();
    assert.equal(document.activeElement, select);
    clickFooter(footer, select, { selectOption: true });
    await settle();
    assert.equal(document.activeElement, attempts[0].client.element);
});

test("mouse focus restoration respects read-only, inactive and disabled controls", async () => {
    const { id, footer, footerControls, element } = mount();
    attempts[0].resolve();
    await settle();
    const control = footerControls[0];
    control.focus();
    control.disabled = true;
    clickFooter(footer, control);
    await settle();
    assert.equal(document.activeElement, control);
    control.disabled = false;
    terminal.setReadOnly(id, true);
    clickFooter(footer, control);
    await settle();
    assert.equal(document.activeElement, control);
    terminal.setReadOnly(id, false);
    element.closest = () => ({ inert: true });
    clickFooter(footer, control);
    await settle();
    assert.equal(document.activeElement, control);
});

test("a mouse click cannot steal focus after another control is selected or the view is disposed", async () => {
    const { id, footer, footerControls } = mount();
    attempts[0].resolve();
    await settle();
    footerControls[0].focus();
    clickFooter(footer, footerControls[0]);
    footerControls[1].focus();
    await settle();
    assert.equal(document.activeElement, footerControls[1]);
    clickFooter(footer, footerControls[1]);
    terminal.disposeTerminal(id);
    await settle();
    assert.equal(document.activeElement, footerControls[1]);
    clickFooter(footer, footerControls[1]);
    await settle();
    assert.equal(document.activeElement, footerControls[1]);
});

test("F6 focuses the footer and Shift+F6 focuses the preceding dashboard control", async () => {
    const { element, footer, footerControls } = mount();
    const previous = {
        tabIndex: 0, disabled: false,
        closest: () => null,
        getClientRects: () => [{}],
        compareDocumentPosition: () => 4,
        focus() { document.activeElement = this; },
    };
    element.closest = () => null;
    document.querySelectorAll = () => [previous];
    setGlobal("Node", { DOCUMENT_POSITION_FOLLOWING: 4 });
    attempts[0].resolve();
    await settle();
    const onInput = attempts[0].options.onInput;
    const key = { type: "key", key: "F6", ctrl: false, alt: false, meta: false, shift: false };
    assert.equal(onInput(key), "consume");
    assert.equal(document.activeElement, footerControls[0]);
    assert.equal(onInput({ ...key, shift: true }), "consume");
    assert.equal(document.activeElement, previous);
    previous.tabIndex = -1;
    assert.equal(onInput({ ...key, shift: true }), "browser");
    previous.tabIndex = 0;
    for (const modifier of ["ctrl", "alt", "meta"]) {
        assert.equal(onInput({ ...key, [modifier]: true }), "continue");
    }
    for (const shiftKey of [false, true]) {
        const event = Object.assign(new Event("keydown", { cancelable: true }),
            { key: "F6", shiftKey, ctrlKey: false, altKey: false, metaKey: false });
        footer.dispatchEvent(event);
        assert.equal(event.defaultPrevented, true);
        assert.equal(document.activeElement, attempts[0].client.element);
    }
    for (const control of footerControls) {
        control.disabled = true;
    }
    onInput(key);
    assert.equal(document.activeElement, footer, "The footer itself remains reachable before controls enable");
});

test("disposing unregisters the footer focus listener", async () => {
    const { id, footer } = mount();
    attempts[0].resolve();
    await settle();
    terminal.disposeTerminal(id);
    const event = Object.assign(new Event("keydown", { cancelable: true }), { key: "F6" });
    footer.dispatchEvent(event);
    assert.equal(event.defaultPrevented, false);
    assert.equal(attempts[0].client.focusCalls, 1);
});

test("font preference follows its surface across remounts but not another surface", async () => {
    const first = mount({ options: { sizeMemoryKey: "memory:dock" } });
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setFontSizeFromHost(first.id, 21);
    assert.equal(terminal.getToolbarState(first.id).fontPx, 21);
    terminal.disposeTerminal(first.id);
    mount({ options: { sizeMemoryKey: "memory:dock", initialFontSize: 15 } });
    mount({ options: { sizeMemoryKey: "memory:window" } });
    assert.equal(attempts[1].options.sizing.fontSize, 21);
    assert.equal(attempts[2].options.sizing.fontSize, 13);
});

test("initial font preferences use package bounds and default when absent", () => {
    for (const [initialFontSize, expected] of [
        [undefined, 13], [null, 13], [NaN, 13],
        [4, MIN_FONT_SIZE], [72, MAX_FONT_SIZE], [18.6, 19],
    ]) {
        mount({ options: { initialFontSize } });
        assert.equal(attempts.at(-1).options.sizing.fontSize, expected);
    }
});

test("a detached surface fits as primary using the originating font without stealing control back", async () => {
    const source = mount({ options: { sizeMemoryKey: "handoff:dock" } });
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setFontSizeFromHost(source.id, 21);
    const font = terminal.getToolbarState(source.id).fontPx;
    terminal.disposeTerminal(source.id);

    mount({ options: { autoFit: true, initialFontSize: font } });
    const popup = attempts[1];
    assert.deepEqual(popup.options.sizing, { mode: "auto", fontSize: 21 });
    popup.resolve();
    await settle();
    assert.equal(popup.client.primaryRequests, 1);
    popup.role(true);
    assert.deepEqual(popup.client.sizingCalls, [{ mode: "auto", fontSize: 21 }]);
    popup.role(false);
    assert.equal(popup.client.primaryRequests, 1);

    mount({ options: { sizeMemoryKey: "handoff:dock", autoFit: true } });
    attempts[2].resolve();
    await settle();
    assert.equal(attempts[2].client.primaryRequests, 1);
    attempts[2].role(true);
    assert.deepEqual(attempts[2].client.sizingCalls, [{ mode: "auto", fontSize: 21 }]);
});

test("font stepper states use package bounds instead of the former xterm range", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setFontSizeFromHost(id, MIN_FONT_SIZE - 1);
    let state = terminal.getToolbarState(id);
    assert.equal(state.fontPx, MIN_FONT_SIZE);
    assert.equal(state.canDecreaseFontSize, false);
    assert.equal(state.canIncreaseFontSize, true);
    terminal.setFontSizeFromHost(id, MAX_FONT_SIZE + 1);
    state = terminal.getToolbarState(id);
    assert.equal(state.fontPx, MAX_FONT_SIZE);
    assert.equal(state.canDecreaseFontSize, true);
    assert.equal(state.canIncreaseFontSize, false);
});

test("container-sized surfaces retain the font stepper but reject fixed presets", async () => {
    const { id } = mount({ options: { chromeless: true, showDimensions: false } });
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setSizeModeFromHost(id, "80x24");
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    terminal.setFontSizeFromHost(id, 18);
    assert.deepEqual(attempts[0].client.sizingCalls, [{ mode: "auto", fontSize: 18 }]);
});

test("opening an auto-fit surface takes primary once and preserves font size across activation", async () => {
    const { id, element } = mount({ options: { autoFit: true, showDimensions: false } });
    attempts[0].resolve();
    await settle();
    const client = attempts[0].client;
    assert.equal(client.primaryRequests, 1);
    assert.deepEqual(client.sizingCalls, []);
    attempts[0].role(true);
    assert.deepEqual(client.sizingCalls, [{ mode: "auto", fontSize: 13 }]);
    terminal.setFontSizeFromHost(id, 18);
    element.clientWidth = 1000;
    element.clientHeight = 700;
    observers[0].callback();
    assert.deepEqual(client.sizing, { mode: "auto", fontSize: 18 });
    assert.equal(client.primaryRequests, 1, "Native automatic sizing handles container resize");

    attempts[0].role(false);
    observers[0].callback();
    assert.equal(client.primaryRequests, 1, "Losing primary must not start a resize ownership fight");
    terminal.setAutoFit(id, false);
    terminal.setAutoFit(id, true);
    terminal.setAutoFit(id, true);
    assert.equal(client.primaryRequests, 2);
    attempts[0].role(true);
    assert.deepEqual(client.sizingCalls, [
        { mode: "auto", fontSize: 13 },
        { mode: "auto", fontSize: 18 },
        { mode: "auto", fontSize: 18 },
    ]);
    assert.equal(attempts.length, 1);
});

test("auto-fit waits for a visible writable view and does not size a deactivated pane", async () => {
    const { id, element } = mount({ visible: false, options: { autoFit: true, readOnly: true } });
    assert.equal(attempts.length, 0);
    element.clientWidth = 800;
    element.clientHeight = 600;
    observers[0].callback();
    attempts[0].resolve();
    await settle();
    const client = attempts[0].client;
    assert.equal(client.primaryRequests, 0);
    terminal.setReadOnly(id, false);
    assert.equal(client.primaryRequests, 1);
    terminal.setAutoFit(id, false);
    attempts[0].role(true);
    assert.deepEqual(client.sizingCalls, []);
    terminal.setReadOnly(id, true);
    terminal.setAutoFit(id, true);
    assert.deepEqual(client.sizingCalls, []);
    terminal.setReadOnly(id, false);
    assert.deepEqual(client.sizingCalls, [{ mode: "auto", fontSize: 13 }]);
    terminal.disposeTerminal(id);
    observers[0].callback();
    assert.equal(client.primaryRequests, 1);
});

test("Fit is separate from fixed presets and disabled only for an auto-sized primary or blocked view", async () => {
    const { id } = mount();
    assert.equal(terminal.getToolbarState(id).fitEnabled, false);
    assert.deepEqual(terminal.getSizePresets().map(p => p.value), ["80x24", "80x30", "100x30", "132x30", "132x50"]);
    attempts[0].resolve();
    await settle();
    assert.equal(terminal.getToolbarState(id).fitEnabled, true);
    terminal.fitToContainer(id);
    assert.equal(attempts[0].client.primaryRequests, 1);
    attempts[0].role(true);
    assert.equal(terminal.getToolbarState(id).fitEnabled, false);
    terminal.setSizeModeFromHost(id, "80x24");
    assert.equal(terminal.getToolbarState(id).fitEnabled, true);
    terminal.fitToContainer(id);
    assert.equal(terminal.getToolbarState(id).fitEnabled, false);
    assert.deepEqual(attempts[0].client.sizingCalls, [
        { mode: "auto", fontSize: 13 },
        { mode: "fixed", columns: 80, rows: 24, fontSize: 13 },
        { mode: "auto", fontSize: 13 },
    ]);
    attempts[0].role(false);
    assert.equal(terminal.getToolbarState(id).fitEnabled, true);
    terminal.setReadOnly(id, true);
    terminal.fitToContainer(id);
    assert.equal(terminal.getToolbarState(id).fitEnabled, false);
    assert.equal(attempts[0].client.primaryRequests, 1);
});

test("per-view read-only uses the native policy and blocks host sizing and control", async () => {
    const { id } = mount({ options: { readOnly: true } });
    assert.equal(attempts[0].options.readOnly, true);
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setFontSizeFromHost(id, 20);
    terminal.setSizeModeFromHost(id, "80x24");
    terminal.requestPrimaryFromHost(id);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    assert.equal(attempts[0].client.primaryRequests, 0);
    const state = terminal.getToolbarState(id);
    assert.equal(state.fontControlsEnabled, false);
    assert.equal(state.sizeSelectEnabled, false);
    assert.equal(state.canTakeControl, false);
    assert.equal(attempts[0].client.readOnly, true);
    assert.equal(attempts[0].options.onInput({ type: "wheel", deltaY: 10 }), "continue");
    assert.equal(attempts[0].options.onInput({ type: "pointer", button: "left", shift: true },
        { mouseCaptured: true }), "continue", "Native Shift-drag selection remains available");
});

test("live read-only changes update UX without remounting or mutating package options", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    Object.freeze(attempts[0].options);
    terminal.setReadOnly(id, true);
    assert.equal(attempts.length, 1);
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(attempts[0].options.readOnly, false);
    assert.equal(attempts[0].client.primaryRequests, 0);
    const context = { selection: { status: "valid", active: true }, mouseCaptured: true };
    const onInput = attempts[0].options.onInput;
    assert.equal(attempts[0].client.readOnly, true);
    for (const input of [
        { type: "key", key: "a" },
        { type: "text", text: "composed text" },
        { type: "paste", text: "pasted text" },
        { type: "pointer", button: "left" },
        { type: "key", key: "c", ctrl: true },
        { type: "pointer", button: "right" },
    ]) {
        assert.equal(onInput(input, context), "continue", "Native policy must own all application and inspection routing");
    }
    terminal.setReadOnly(id, false);
    assert.equal(onInput({ type: "key", key: "a" }, context), "continue");
    assert.equal(onInput({ type: "paste", text: "allowed" }, context), "continue");
    assert.equal(attempts[0].client.readOnly, false);
    assert.deepEqual(attempts[0].client.readOnlyCalls, [false, true, false]);
    assert.equal(attempts.length, 1);
});

for (const initialReadOnly of [false, true]) {
    test(`read-only changes during mounting reconcile from ${initialReadOnly} before input is available`, async () => {
        const { id } = mount({ options: { readOnly: initialReadOnly } });
        terminal.setReadOnly(id, !initialReadOnly);
        attempts[0].resolve();
        await settle();
        assert.equal(attempts[0].options.readOnly, initialReadOnly);
        assert.equal(attempts[0].client.readOnly, !initialReadOnly);
        assert.equal(attempts.length, 1);
    });
}

test("read-only cancels a pending resize request without changing producer ownership", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.setFontSizeFromHost(id, 20);
    assert.equal(attempts[0].client.primaryRequests, 1);
    terminal.setReadOnly(id, true);
    attempts[0].role(true);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    terminal.setReadOnly(id, false);
    assert.deepEqual(attempts[0].client.sizingCalls, [], "Unblocking must not replay a canceled resize");
    assert.equal(attempts[0].client.primaryRequests, 1);
});

test("a quiet retained connection stays mounted until the user closes its view", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.refreshLayout(id);
    await settle();
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(attempts[0].options.signal.aborted, false);
    assert.equal(terminal.getToolbarState(id).connected, true);
    assert.equal(timers.size, 0);
    assert.equal(attempts[0].client.primaryRequests, 0);
    terminal.disposeTerminal(id);
    assert.equal(attempts[0].client.disposed, true);
    assert.equal(attempts[0].options.signal.aborted, true);
});

test("element snapshots expose public screen and selection state for the matching live view", async () => {
    const { id, element } = mount();
    const other = mount();
    assert.equal(terminal.getTerminalSnapshot(element).screenText, "");
    attempts[0].client.screenText = "first terminal";
    attempts[0].client.selection = { status: "valid", text: "first" };
    attempts[0].client.viewport = { available: true, following: true };
    attempts[0].resolve();
    attempts[1].client.screenText = "other terminal";
    attempts[1].resolve();
    await settle();
    terminal.setReadOnly(id, true);
    const snapshot = terminal.getTerminalSnapshot(element);
    assert.equal(snapshot.terminalId, id);
    assert.equal(snapshot.readOnly, true);
    assert.equal(snapshot.screenText, "first terminal");
    assert.deepEqual(snapshot.selection, { status: "valid", text: "first" });
    assert.deepEqual(snapshot.viewport, { available: true, following: true });
    assert.equal(terminal.getTerminalSnapshot(other.element).screenText, "other terminal");
    terminal.disposeTerminal(id);
    assert.equal(terminal.getTerminalSnapshot(element), null);
});

test("authoritative close before the first frame retains the view without retry or a Blazor completion check", async () => {
    const { id, element } = mount();
    attempts[0].close(4000);
    attempts[0].reject(new Error("Native first-frame failure"));
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, true);
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(timers.size, 0);
    terminal.refreshLayout(id);
    terminal.reconnectTerminal(id, attempts[0].options.url);
    assert.equal(attempts.length, 1);
    assert.notEqual(terminal.getTerminalSnapshot(element), null);
    terminal.disposeTerminal(id);
    assert.equal(terminal.getTerminalSnapshot(element), null);
});

test("authoritative close keeps the existing presentation read-only without remounting", async () => {
    const { id, element } = mount();
    attempts[0].client.screenText = "Last available presentation";
    attempts[0].resolve();
    await settle();
    attempts[0].close(4000);
    attempts[0].options.onStatus("Late transport error", "error");
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, true);
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(timers.size, 0);
    terminal.setReadOnly(id, false);
    assert.equal(terminal.getTerminalSnapshot(element).readOnly, true);
    assert.equal(attempts[0].client.readOnly, true);
    assert.equal(terminal.getTerminalSnapshot(element).screenText, "Last available presentation");
    assert.equal(terminal.getToolbarState(id).error, null);
    terminal.requestPrimaryFromHost(id);
    assert.equal(attempts[0].client.primaryRequests, 0);
});

test("completion before the mount continuation cannot revive the connected state", async () => {
    const { id, element } = mount({ options: { autoFit: true } });
    attempts[0].resolve();
    attempts[0].close(4000);
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, true);
    assert.equal(terminal.getToolbarState(id).connected, false);
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(attempts[0].client.readOnly, true);
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.equal(timers.size, 0);
});

for (const code of [1000, 1001, 1006]) {
    for (const mounted of [false, true]) {
        test(`transport close ${code} ${mounted ? "after" : "before"} mounting retries regardless of close reason or cleanliness`, async () => {
            const { id, element } = mount();
            if (mounted) {
                attempts[0].resolve();
                await settle();
            }
            attempts[0].close(code, "Terminal ended", code !== 1006);
            attempts[0].reject();
            await settle();
            assert.equal(terminal.getTerminalSnapshot(element).ended, false);
            assert.equal(terminal.getToolbarState(id).connected, false);
            assert.equal(timers.size, 1);
            assert.equal(retry(), 500);
            attempts[1].resolve();
            await settle();
            assert.equal(terminal.getToolbarState(id).connected, true);
        });
    }
}

test("rebind ignores authoritative close from the old connection", async () => {
    const { id, element } = mount();
    attempts[0].resolve();
    await settle();
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=next&viewId=next");
    attempts[0].close(4000);
    attempts[1].resolve();
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, false);
    assert.equal(terminal.getToolbarState(id).connected, true);
    assert.equal(timers.size, 0);
});

test("disposing a view ignores later native close callbacks", async () => {
    const { id } = mount();
    terminal.disposeTerminal(id);
    attempts[0].close(4000);
    attempts[0].close(1006);
    await settle();
    assert.equal(timers.size, 0);
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(terminal.getToolbarState(id), null);
    assert.equal(attempts.length, 1);
});

test("frontend manifest, lockfile, minified bundle and backend use the exact paired version", async () => {
    const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
    const lockfile = JSON.parse(await readFile(new URL("package-lock.json", dashboard), "utf8"));
    const bundle = await readFile(new URL("dist/index.min.js", assets), "utf8");
    const version = manifest.dependencies["@hex1b/web-terminal"];
    assert.equal(version, "0.169.0");
    assert.ok(bundle.startsWith(`// @hex1b/web-terminal ${version}; minified with Terser. See ../LICENSE.\n`));
    assert.equal(lockfile.packages[""].dependencies["@hex1b/web-terminal"], version);
    assert.equal(lockfile.packages["node_modules/@hex1b/web-terminal"].version, version);

    // Central package rows have the form:
    //   <PackageVersion Include="Hex1b" Version="0.169.0" />
    // Match the exact Include value, not Hex1b.Tool or Hex1b.McpServer;
    // whitespace, attribute order and either XML quote style are allowed.
    const packages = await readFile(new URL("../../Directory.Packages.props", dashboard), "utf8");
    const declarations = [...packages.matchAll(/<PackageVersion\b[^>]*\/>/g)]
        .map(match => match[0])
        .filter(declaration => /\bInclude\s*=\s*["']Hex1b["']/.test(declaration));
    assert.equal(declarations.length, 1, "Expected exactly one central Hex1b library version.");
    const backendVersion = declarations[0].match(/\bVersion\s*=\s*["']([^"']+)["']/);
    assert.ok(backendVersion, "The paired Hex1b library must have an explicit central version.");
    assert.equal(backendVersion[1], version);
});

test("checked-in deployment contains only the minified bundle, font and required licenses without npm installation", async () => {
    const entries = await readdir(assets, { recursive: true, withFileTypes: true });
    const files = entries.filter(entry => entry.isFile())
        .map(entry => relative(fileURLToPath(assets), join(entry.parentPath, entry.name)).split(sep).join("/"))
        .sort();
    assert.deepEqual(files, [
        "LICENSE",
        "dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2",
        "dist/fonts/cascadia-mono-nf/LICENSE.txt",
        "dist/index.min.js",
    ]);
    for (const name of files) {
        assert.ok((await readFile(new URL(name, assets))).length > 0, `Missing or empty vendored asset: ${name}`);
    }
});

test("entry, both bundled module workers and font URLs preserve PathBase and same origin", async () => {
    // Inspect the emitted forms:
    //   import { WebTerminal, ... } from "../../js/.../dist/index.min.js";
    //   new URL(import.meta.url); t.hash=`hex1b-${e}-worker`;
    //   new URL("./fonts/.../CascadiaMonoNF.woff2",import.meta.url).href;
    // Minification renames local functions and parameters, not these URL shapes.
    // Keeping these module-relative URLs avoids both PathBase escapes and
    // blob/cross-origin worker URLs that require relaxing the dashboard CSP.
    const entryReference = source.match(/from "([^"]+)"/)[1];
    const entryUrl = new URL(entryReference, "https://dashboard.example/nested/aspire/Components/Controls/TerminalView.razor.js");
    assert.equal(entryUrl.href, "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/index.min.js");
    const clientSource = await readFile(new URL("dist/index.min.js", assets), "utf8");
    assert.match(clientSource, /new URL\(import\.meta\.url\)/);
    assert.match(clientSource, /\.hash=`hex1b-\$\{[\w$]+\}-worker`/);
    assert.match(clientSource, /globalThis instanceof DedicatedWorkerGlobalScope/);
    for (const kind of ["terminal", "link-detection"]) {
        assert.match(clientSource, new RegExp(`case\\s*"#hex1b-${kind}-worker":`));
        const workerUrl = new URL(`${entryUrl.href}?v=alpha`);
        workerUrl.hash = `hex1b-${kind}-worker`;
        assert.equal(workerUrl.href,
            `https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/index.min.js?v=alpha#hex1b-${kind}-worker`);
    }
    const fontReference = clientSource.match(/new URL\("([^"]+\.woff2)",import\.meta\.url\)/)[1];
    assert.equal(new URL(fontReference, entryUrl).href,
        "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2");
});
