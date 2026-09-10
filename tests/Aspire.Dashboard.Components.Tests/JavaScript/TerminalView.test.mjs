// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, mock, test } from "node:test";
import { readFile } from "node:fs/promises";

const dashboard = new URL("../../../src/Aspire.Dashboard/", import.meta.url);
const assets = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const { WebTerminal, MIN_FONT_SIZE, MAX_FONT_SIZE } = await import(new URL("dist/index.js", assets));
const source = await readFile(new URL("Components/Controls/TerminalView.razor.js", dashboard), "utf8");
// Remap the public browser asset import to its checked-in location for Node,
// without changing the adapter implementation under test.
const terminal = await import(`data:text/javascript;base64,${Buffer.from(source.replace(
    '"../../js/hex1b-web-terminal/dist/index.js"', JSON.stringify(new URL("dist/index.js", assets).href)
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

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    mock.method(console, "warn", () => {});
    attempts = [];
    observers = [];
    timers = new Map();
    frames = new Map();
    ids = [];
    snapshots = [];
    serial = 0;
    setGlobal("window", { isSecureContext: true });
    setGlobal("navigator", { gpu: {} });
    setGlobal("document", { activeElement: null, body: {} });
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
        const client = {
            element: { contains: value => value === client.element },
            connected: true,
            peer: { id: "browser-1", primaryId: "cli-1", isPrimary: false },
            geometry: { columns: 100, rows: 30 },
            sizing: { ...options.sizing },
            sizingCalls: [],
            primaryRequests: 0,
            focusCalls: 0,
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
            focus() { this.focusCalls++; document.activeElement = this.element; },
            clearSelection() { this.selectionClears++; },
            refreshSelectionUI() { this.selectionRefreshes++; },
        };
        const attempt = {
            element, options, client,
            resolve() { ready.resolve(client); },
            reject(error = new Error("No first frame")) { ready.reject(error); },
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
            ...overrides.selection },
    } });
    assert.equal(attempt.options.onSelectionUI(event), undefined, "UI ownership must be synchronous");
    assert.equal(event.defaultPrevented, true);
    return event;
}

function mount({ visible = true, dotNetRef, options = {}, isEnded = () => false } = {}) {
    const element = {
        clientWidth: visible ? 800 : 0,
        clientHeight: visible ? 600 : 0,
        contains: value => value === element,
    };
    const controls = [];
    const template = { firstElementChild: { cloneNode() {
        const control = selectionControl();
        controls.push(control);
        return control.actions;
    } } };
    const footerControls = [0, 1, 2].map(() => ({
        disabled: false, tabIndex: 0,
        focus() { document.activeElement = this; },
    }));
    const footer = Object.assign(new EventTarget(), {
        querySelectorAll: () => footerControls,
        focus() { document.activeElement = this; },
    });
    const viewId = options.viewId ?? `view-${++serial}`;
    const id = terminal.initTerminal(element, "wss://dashboard/api/terminal?resource=app&replica=1",
        dotNetRef ?? { invokeMethodAsync: (name, value) =>
            name === "OnTerminalStateChanged" ? snapshots.push(value) : isEnded(value) },
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
    assert.equal(attempts[0].client.focusCalls, 1);
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
    assert.equal(attempts[0].client.focusCalls, 0);
    assert.equal(actions.hidden, false);
    copy.resolve("<untrusted selected text>");
    await settle();
    assert.equal(button.disabled, false);
    assert.equal(button.attributes.get("aria-disabled"), "false");
    assert.equal(button.attributes.get("aria-busy"), "false");
    assert.equal(actions.hidden, true);
    assert.equal(attempts[0].client.selectionClears, 1);
    assert.equal(attempts[0].client.focusCalls, 1);
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

test("copy failures remain local and a successful retry clears the error", async () => {
    const { id, controls } = mount();
    attempts[0].resolve();
    await settle();
    selectionEvent(attempts[0], { runAction: () => Promise.reject(new Error("Clipboard denied")) });
    controls[0].button.dispatchEvent(new Event("click"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, "input-failed");
    assert.equal(controls[0].button.disabled, false);
    assert.equal(controls[0].actions.hidden, false);
    assert.equal(attempts[0].client.selectionClears, 0);
    assert.equal(attempts[0].client.focusCalls, 0);
    assert.equal(attempts.length, 1);
    assert.equal(timers.size, 0);
    selectionEvent(attempts[0]);
    controls[0].button.dispatchEvent(new Event("click"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(controls[0].actions.hidden, true);
    assert.equal(attempts[0].client.selectionClears, 1);
    assert.equal(attempts[0].client.focusCalls, 1);
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

test("init returns an id while mount waits for its first connected frame", async () => {
    const { id } = mount();
    assert.equal(terminal.getToolbarState(id).connected, false);
    assert.equal(attempts[0].options.label, "Localized terminal input");
    assert.equal(attempts[0].options.url, "wss://dashboard/api/terminal?resource=app&replica=1");
    assert.equal(attempts[0].options.renderer, "auto");
    attempts[0].options.onStatus("Socket open", "ready");
    attempts[0].role(false);
    await settle();
    assert.equal(snapshots.at(-1).connected, false);
    attempts[0].resolve();
    await settle();
    assert.deepEqual(snapshots.at(-1), {
        terminalId: id, generation: 1, status: "viewer", connected: true,
        isPrimary: false, canTakeControl: true, sizeMode: "font", sizeKey: "auto",
        fontPx: 13, fontControlsEnabled: true, sizeSelectEnabled: true,
        canDecreaseFontSize: true, canIncreaseFontSize: true,
        cols: 100, rows: 30, error: null,
    });
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.equal(typeof attempts[0].options.onInput, "function");
    assert.equal(attempts[0].options.inputBindings, undefined);
    assert.equal(attempts[0].options.actions, undefined);
    assert.equal(attempts[0].options.readOnly, false);
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

test("clipboard errors remain visible without discarding the mounted history", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].options.onInputError(new Error("Clipboard denied"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, "input-failed");
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(timers.size, 0);
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
    assert.equal(terminal.getToolbarState(id).sizeKey, "auto");
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
    setGlobal("getComputedStyle", () => ({ visibility: "visible" }));
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
    assert.equal(attempts[0].client.focusCalls, 0);
});

test("font preference follows its surface across remounts but not another surface", async () => {
    const first = mount({ options: { sizeMemoryKey: "memory:dock" } });
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    terminal.setFontSizeFromHost(first.id, 21);
    assert.equal(terminal.getToolbarState(first.id).fontPx, 21);
    terminal.disposeTerminal(first.id);
    mount({ options: { sizeMemoryKey: "memory:dock" } });
    mount({ options: { sizeMemoryKey: "memory:window" } });
    assert.equal(attempts[1].options.sizing.fontSize, 21);
    assert.equal(attempts[2].options.sizing.fontSize, 13);
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

test("per-view read-only preserves inspection and blocks host sizing and control without static native mode", async () => {
    const { id } = mount({ options: { readOnly: true } });
    assert.equal(attempts[0].options.readOnly, false, "Mount-time native mode cannot support live unblocking");
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
    assert.deepEqual(attempts[0].options.onInput({ type: "wheel", deltaY: 10 }), { action: "scrollLines", args: 3 });
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
    assert.equal(onInput({ type: "key", key: "a" }, context), "consume");
    assert.equal(onInput({ type: "text", text: "composed text" }, context), "consume");
    assert.equal(onInput({ type: "paste", text: "pasted text" }, context), "consume");
    assert.equal(onInput({ type: "pointer", button: "left" }, context), "consume");
    assert.deepEqual(onInput({ type: "key", key: "c", ctrl: true }, context), { action: "copySelection" });
    assert.deepEqual(onInput({ type: "pointer", button: "right" }, context), { action: "copySelection" });
    terminal.setReadOnly(id, false);
    assert.equal(onInput({ type: "key", key: "a" }, context), "continue");
    assert.equal(onInput({ type: "paste", text: "allowed" }, context), "continue");
    assert.equal(attempts.length, 1);
});

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

test("ended registration prevents retry after an unsuccessful first frame without dismissing the view", async () => {
    const { id, element } = mount({ isEnded: () => true });
    attempts[0].reject(new Error("Native first-frame failure"));
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, true);
    assert.equal(terminal.getToolbarState(id).error, null);
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(timers.size, 0);
    terminal.refreshLayout(id);
    terminal.reconnectTerminal(id, attempts[0].options.url);
    assert.equal(attempts.length, 1);
    assert.notEqual(terminal.getTerminalSnapshot(element), null);
    terminal.disposeTerminal(id);
    assert.equal(terminal.getTerminalSnapshot(element), null);
});

test("completion check keeps an existing last presentation and does not forward input", async () => {
    const { id, element } = mount({ isEnded: () => true });
    attempts[0].resolve();
    await settle();
    attempts[0].client.connected = false;
    attempts[0].options.onStatus("Connection failed", "error");
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, true);
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(timers.size, 0);
    terminal.setReadOnly(id, false);
    assert.equal(terminal.getTerminalSnapshot(element).readOnly, true);
    assert.equal(attempts[0].options.onInput({ type: "key", key: "a" }, { selection: {} }), "consume");
    terminal.requestPrimaryFromHost(id);
    assert.equal(attempts[0].client.primaryRequests, 0);
});

test("completion checks are bounded without guessing when the Blazor circuit is unavailable", async () => {
    const result = Promise.withResolvers();
    const { id, element } = mount({ isEnded: () => result.promise });
    attempts[0].resolve();
    await settle();
    attempts[0].client.connected = false;
    attempts[0].options.onStatus("Connection failed", "error");
    await settle();
    const [timer, { callback, delay }] = timers.entries().next().value;
    assert.equal(delay, 5000);
    timers.delete(timer);
    callback();
    await settle();
    assert.equal(terminal.getToolbarState(id).error, "disconnected");
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(timers.size, 0);
    result.resolve(true);
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, false);
});

test("rebind cancels pending completion checks and ignores the old registration's answer", async () => {
    const result = Promise.withResolvers();
    const checked = [];
    const { id, element, viewId } = mount({ isEnded: value => { checked.push(value); return result.promise; } });
    attempts[0].resolve();
    await settle();
    attempts[0].client.connected = false;
    attempts[0].options.onStatus("Connection failed", "error");
    await settle();
    assert.deepEqual(checked, [viewId]);
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=next&viewId=next");
    result.resolve(true);
    attempts[1].resolve();
    await settle();
    assert.equal(terminal.getTerminalSnapshot(element).ended, false);
    assert.equal(terminal.getToolbarState(id).connected, true);
    assert.equal(timers.size, 0);
});

test("disposing a view cancels its outstanding completion check and mount", async () => {
    const result = Promise.withResolvers();
    const { id } = mount({ isEnded: () => result.promise });
    attempts[0].reject();
    await settle();
    terminal.disposeTerminal(id);
    await settle();
    assert.equal(timers.size, 0);
    assert.equal(attempts[0].options.signal.aborted, true);
    result.resolve(true);
    await settle();
    assert.equal(attempts.length, 1);
});

test("frontend manifest, lockfile, vendored package and backend use the exact paired version", async () => {
    const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
    const lockfile = JSON.parse(await readFile(new URL("package-lock.json", dashboard), "utf8"));
    const vendored = JSON.parse(await readFile(new URL("package.json", assets), "utf8"));
    const version = manifest.dependencies["@hex1b/web-terminal"];
    assert.equal(version, "0.167.0-alpha.1547.1.798b26c");
    assert.equal(vendored.version, version);
    assert.equal(lockfile.packages[""].dependencies["@hex1b/web-terminal"], version);
    assert.equal(lockfile.packages["node_modules/@hex1b/web-terminal"].version, version);

    // Central package rows have the form:
    //   <PackageVersion Include="Hex1b" Version="0.167.0-alpha..." />
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

test("checked-in deployment includes the worker and licensed font without npm installation", async () => {
    for (const name of [
        "dist/index.js",
        "dist/terminal-worker.js",
        "dist/webgpu-backend.js",
        "dist/webgl2-backend.js",
        "dist/hyperlinks.js",
        "dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2",
        "dist/fonts/cascadia-mono-nf/LICENSE.txt",
        "dist/fonts/cascadia-mono-nf/README.md",
        "LICENSE",
        "README.md",
    ]) {
        assert.ok((await readFile(new URL(name, assets))).length > 0, `Missing or empty vendored asset: ${name}`);
    }
});

test("entry, module worker and bundled font URLs preserve PathBase and same origin", async () => {
    // Inspect the emitted forms:
    //   import { WebTerminal, ... } from "../../js/.../dist/index.js";
    //   new Worker(new URL("./terminal-worker.js", import.meta.url), ...);
    //   new URL("./fonts/.../CascadiaMonoNF.woff2", import.meta.url).href;
    // Keeping these module-relative URLs avoids both PathBase escapes and
    // blob/cross-origin worker URLs that require relaxing the dashboard CSP.
    const entryReference = source.match(/from "([^"]+)"/)[1];
    const entryUrl = new URL(entryReference, "https://dashboard.example/nested/aspire/Components/Controls/TerminalView.razor.js");
    assert.equal(entryUrl.href, "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/index.js");
    const clientSource = await readFile(new URL("dist/web-terminal.js", assets), "utf8");
    const workerReference = clientSource.match(/new Worker\(new URL\("([^"]+)", import\.meta\.url\)/)[1];
    assert.equal(new URL(workerReference, entryUrl).href,
        "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/terminal-worker.js");
    const fontSource = await readFile(new URL("dist/terminal-font.js", assets), "utf8");
    const fontReference = fontSource.match(/new URL\("([^"]+)", import\.meta\.url\)/)[1];
    assert.equal(new URL(fontReference, entryUrl).href,
        "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2");
});
