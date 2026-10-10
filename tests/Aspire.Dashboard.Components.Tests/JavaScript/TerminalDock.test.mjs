// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

const terminalDock = await import(new URL("../../../src/Aspire.Dashboard/Components/Layout/TerminalDock.razor.js", import.meta.url));

const globals = new Map();
let timers;
let timerId;
let dockElement;

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    timers = new Map();
    timerId = 0;
    setGlobal("window", Object.assign(new EventTarget(), { innerHeight: 900 }));
    setGlobal("document", Object.assign(new EventTarget(), { activeElement: null, body: {} }));
    setGlobal("setTimeout", (callback, delay) => {
        const id = ++timerId;
        timers.set(id, { callback, delay });
        return id;
    });
    setGlobal("clearTimeout", id => timers.delete(id));
});

afterEach(() => {
    if (dockElement) {
        terminalDock.unregisterTabNavigation(dockElement);
        terminalDock.unregisterResizeHandle(dockElement);
        dockElement = null;
    }
    for (const [name, descriptor] of globals) {
        if (descriptor) {
            Object.defineProperty(globalThis, name, descriptor);
        } else {
            delete globalThis[name];
        }
    }
    globals.clear();
});

function createRegistration() {
    const captures = new Set();
    const grabber = Object.assign(new EventTarget(), {
        focus() { document.activeElement = grabber; },
        setPointerCapture(id) { captures.add(id); },
        hasPointerCapture(id) { return captures.has(id); },
        releasePointerCapture(id) { captures.delete(id); },
    });
    dockElement = {
        inert: false,
        style: {},
        querySelector: selector => selector === ".terminal-dock-resize-handle" ? grabber : null,
        getBoundingClientRect: () => ({ height: 320 }),
    };
    const invocations = [];
    const dotNetRef = {
        invokeMethodAsync(method, height, viewportHeight) {
            invocations.push({ method, height, viewportHeight });
            return Promise.resolve();
        },
    };

    terminalDock.registerResizeHandle(dockElement, dotNetRef, 120, 1200);
    assert.deepEqual(invocations, [{ method: "SetHeightAsync", height: 320, viewportHeight: 900 }]);
    invocations.length = 0;
    return { grabber, invocations };
}

function dispatch(target, type, properties = {}) {
    const event = Object.assign(new Event(type), properties);
    target.dispatchEvent(event);
}

function runPendingTimer() {
    assert.equal(timers.size, 1);
    const [id, timer] = timers.entries().next().value;
    timers.delete(id);
    assert.equal(timer.delay, 100);
    timer.callback();
}

function createTabRegistration() {
    const observers = [];
    class Observer {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe() {}
        disconnect() { this.disconnected = true; }
    }
    setGlobal("ResizeObserver", Observer);
    setGlobal("MutationObserver", Observer);
    setGlobal("getComputedStyle", () => ({ direction: "ltr" }));
    const calls = [];
    const scrolls = [];
    const makeButton = direction => ({
        dataset: { tabScroll: direction },
        disabled: false,
        closest: selector => selector === "[data-tab-scroll]" ? buttonByDirection[direction] : null,
        toggleAttribute(name, value) { this.disabled = value; },
        setAttribute(name, value) { this[name] = value; },
        hasAttribute() { return this.disabled; },
    });
    const buttonByDirection = { "-1": makeButton("-1"), "1": makeButton("1") };
    const controls = { hidden: false, querySelectorAll: () => Object.values(buttonByDirection) };
    const wrapper = { clientWidth: 200, toggleAttribute() {} };
    const tabs = ["one", "two", "three"].map((id, index) => {
        const group = {
            dataset: { terminalId: id },
            isConnected: true,
            scrollIntoView() {},
            classList: { add() {}, remove() {} },
            removeAttribute() { delete this.dataset.dropPosition; },
            getBoundingClientRect: () => ({ left: index * 100, right: index * 100 + 100, width: 100 }),
            contains: element => element === group || element === tab,
            querySelector: () => tab,
        };
        const tab = {
            closest: selector => selector === ".terminal-dock-tab-select" ? tab : selector === ".terminal-dock-tab" ? group : null,
            scrollIntoView() {},
            focus() { document.activeElement = tab; },
        };
        return { group, tab };
    });
    const list = {
        clientWidth: 144,
        scrollWidth: 300,
        querySelectorAll: () => tabs.map(t => t.group),
        querySelector: () => tabs[0].tab,
        contains: element => element === list || tabs.some(t => t.tab === element || t.group === element),
        getBoundingClientRect: () => ({ left: 0, right: 144 }),
        scrollBy: options => scrolls.push(options),
    };
    document.activeElement = { closest: () => null };
    dockElement = Object.assign(new EventTarget(), {
        isConnected: true,
        inert: false,
        querySelector: selector => ({
            ".terminal-dock-tablist": list,
            ".terminal-dock-tab-scroll": controls,
            ".terminal-dock-tabs": wrapper,
        })[selector],
        contains: element => list.contains(element),
    });
    const emit = (type, target, properties = {}) => {
        const event = new Event(type, { cancelable: true });
        Object.defineProperty(event, "target", { value: target });
        Object.assign(event, properties);
        dockElement.dispatchEvent(event);
        return event;
    };
    terminalDock.registerTabNavigation(dockElement, {
        invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); },
    });
    return { calls, scrolls, controls, wrapper, list, tabs, emit, observers, buttonByDirection };
}

test("overflow arrows remain visible and disable when tabs fit or the list is removed", () => {
    const { controls, buttonByDirection, emit, scrolls, list, observers } = createTabRegistration();
    assert.equal(controls.hidden, false);
    assert.equal(buttonByDirection["-1"].disabled, true);
    assert.equal(buttonByDirection["-1"]["aria-disabled"], "true");
    assert.equal(buttonByDirection["1"].disabled, false);
    assert.equal(buttonByDirection["1"]["aria-disabled"], "false");
    emit("click", buttonByDirection["1"]);
    assert.deepEqual(scrolls, [{ left: 108, behavior: "instant" }]);
    list.getBoundingClientRect = () => ({ left: 0, right: 500 });
    observers[0].callback();
    assert.equal(controls.hidden, false);
    assert.equal(buttonByDirection["-1"].disabled, true);
    assert.equal(buttonByDirection["1"].disabled, true);
    const querySelector = dockElement.querySelector;
    dockElement.querySelector = selector => selector === ".terminal-dock-tablist" ? null : querySelector(selector);
    observers[1].callback();
    assert.equal(controls.hidden, false);
    assert.equal(buttonByDirection["-1"].disabled, true);
    assert.equal(buttonByDirection["1"].disabled, true);
});

test("local drag commits relative order, ignores foreign drops and cleans up edge scrolling", async () => {
    const { emit, tabs, calls, observers } = createTabRegistration();
    const dataTransfer = { setData() {} };
    emit("drop", tabs[0].tab, { dataTransfer });
    assert.deepEqual(calls, []);
    emit("dragstart", tabs[2].tab, { dataTransfer });
    assert.equal(dataTransfer.effectAllowed, "move");
    emit("dragover", tabs[0].tab, { dataTransfer, clientX: 5 });
    assert.equal(tabs[0].group.dataset.dropPosition, "before");
    emit("drop", tabs[0].tab, { dataTransfer });
    await Promise.resolve();
    assert.deepEqual(calls, [["ReorderTerminalAsync", "three", "one", false]]);
    assert.equal(timers.size, 0);
    emit("dragstart", tabs[0].tab, { dataTransfer });
    emit("dragover", tabs[2].tab, { dataTransfer, clientX: 140 });
    assert.equal(timers.size, 1);
    terminalDock.unregisterTabNavigation(dockElement);
    assert.equal(timers.size, 0);
    assert.ok(observers.every(observer => observer.disconnected));
});

test("keyboard reorder is confined to modified tab-header keys", async () => {
    const { emit, tabs, calls } = createTabRegistration();
    emit("keydown", tabs[0].tab, { key: "ArrowRight", altKey: true, shiftKey: true });
    await Promise.resolve();
    assert.deepEqual(calls, [["ReorderTerminalAsync", "one", "two", true]]);
    calls.length = 0;
    emit("keydown", tabs[0].tab, { key: "ArrowLeft", altKey: true, shiftKey: true });
    emit("keydown", { closest: () => null }, { key: "ArrowRight", altKey: true, shiftKey: true });
    dockElement.inert = true;
    emit("keydown", tabs[0].tab, { key: "ArrowRight", altKey: true, shiftKey: true });
    assert.deepEqual(calls, []);
});

test("stationary edge drag updates the drop target as tabs scroll underneath", async () => {
    const { emit, tabs, calls, list } = createTabRegistration();
    const dataTransfer = { setData() {} };
    emit("dragstart", tabs[0].tab, { dataTransfer });
    emit("dragover", tabs[1].tab, { dataTransfer, clientX: 140 });
    assert.equal(tabs[1].group.dataset.dropPosition, "before");
    list.scrollBy = () => {
        tabs[1].group.getBoundingClientRect = () => ({ left: 20, right: 120, width: 100 });
        tabs[2].group.getBoundingClientRect = () => ({ left: 120, right: 220, width: 100 });
    };
    const [id, timer] = timers.entries().next().value;
    timers.delete(id);
    timer.callback();
    assert.equal(tabs[2].group.dataset.dropPosition, "before");
    emit("drop", tabs[2].tab, { dataTransfer });
    await Promise.resolve();
    assert.deepEqual(calls, [["ReorderTerminalAsync", "one", "three", false]]);
    assert.equal(timers.size, 0);
});

test("removal or collapse cancels a drag without reordering", () => {
    const { emit, tabs, calls, observers } = createTabRegistration();
    const dataTransfer = { setData() {} };
    for (const collapse of [false, true]) {
        tabs[0].group.isConnected = true;
        emit("dragstart", tabs[0].tab, { dataTransfer });
        emit("dragover", tabs[1].tab, { dataTransfer, clientX: 140 });
        if (collapse) dockElement.inert = true;
        else tabs[0].group.isConnected = false;
        observers[1].callback();
        emit("drop", tabs[1].tab, { dataTransfer });
        assert.equal(timers.size, 0);
        assert.equal(tabs[1].group.dataset.dropPosition, undefined);
    }
    assert.deepEqual(calls, []);
});

test("drag and viewport changes share one throttled update", () => {
    const { grabber, invocations } = createRegistration();

    dispatch(grabber, "pointerdown", { button: 0, isPrimary: true, pointerId: 1 });
    dispatch(grabber, "pointermove", { pointerId: 1, clientY: 500 });
    window.innerHeight = 800;
    dispatch(window, "resize");

    assert.equal(timers.size, 1);
    assert.deepEqual(invocations, []);
    assert.equal(dockElement.style.height, "400px");

    runPendingTimer();
    assert.deepEqual(invocations, [{ method: "SetHeightAsync", height: 400, viewportHeight: 800 }]);
});

test("pointer release flushes a pending drag update", () => {
    const { grabber, invocations } = createRegistration();

    dispatch(grabber, "pointerdown", { button: 0, isPrimary: true, pointerId: 1 });
    dispatch(grabber, "pointermove", { pointerId: 1, clientY: 500 });
    assert.equal(timers.size, 1);
    assert.deepEqual(invocations, []);

    dispatch(grabber, "pointerup", { pointerId: 1 });

    assert.equal(timers.size, 0);
    assert.deepEqual(invocations, [{ method: "SetHeightAsync", height: 400, viewportHeight: 900 }]);
});
