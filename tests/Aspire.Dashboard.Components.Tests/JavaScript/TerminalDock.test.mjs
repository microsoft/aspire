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
