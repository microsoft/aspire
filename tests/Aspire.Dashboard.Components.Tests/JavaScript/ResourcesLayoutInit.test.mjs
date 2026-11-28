// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import {
    initializeResourcesLayout,
    disposeResourcesLayout
} from "../../../src/Aspire.Dashboard/Components/Layout/ResourcesLayout.razor.js";

const globals = new Map();

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

function createFakeElement(classList = []) {
    const listeners = new Map();
    const attributes = new Map();
    return {
        listeners,
        attributes,
        tabIndex: -1,
        dataset: {},
        classList: {
            contains: name => classList.includes(name),
            add: () => { },
            remove: () => { }
        },
        style: {
            getPropertyValue: () => "",
            setProperty: () => { }
        },
        addEventListener(type, listener) {
            if (!listeners.has(type)) {
                listeners.set(type, new Set());
            }
            listeners.get(type).add(listener);
        },
        removeEventListener(type, listener) {
            listeners.get(type)?.delete(listener);
        },
        getAttribute(name) {
            return attributes.get(name) ?? null;
        },
        setAttribute(name, value) {
            attributes.set(name, String(value));
        },
        toggleAttribute() { },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        closest() { return null; }
    };
}

let observers;
let windowListeners;
let documentListeners;

beforeEach(() => {
    observers = [];
    windowListeners = new Map();
    documentListeners = new Map();

    class FakeObserver {
        constructor() {
            this.targets = new Set();
            observers.push(this);
        }
        observe(target) { this.targets.add(target); }
        unobserve(target) { this.targets.delete(target); }
        disconnect() { this.targets.clear(); }
    }

    setGlobal("ResizeObserver", FakeObserver);
    setGlobal("MutationObserver", FakeObserver);
    setGlobal("requestAnimationFrame", () => 1);
    setGlobal("cancelAnimationFrame", () => { });
    setGlobal("navigator", { platform: "MacIntel" });
    setGlobal("window", {
        innerWidth: 1440,
        addEventListener(type, listener) {
            if (!windowListeners.has(type)) {
                windowListeners.set(type, new Set());
            }
            windowListeners.get(type).add(listener);
        },
        removeEventListener(type, listener) {
            windowListeners.get(type)?.delete(listener);
        }
    });
    setGlobal("document", {
        body: {},
        visibilityState: "visible",
        getElementById: () => null,
        querySelector: () => null,
        addEventListener(type, listener) {
            if (!documentListeners.has(type)) {
                documentListeners.set(type, new Set());
            }
            documentListeners.get(type).add(listener);
        },
        removeEventListener(type, listener) {
            documentListeners.get(type)?.delete(listener);
        }
    });
});

afterEach(() => {
    for (const [name, descriptor] of globals) {
        if (descriptor) {
            Object.defineProperty(globalThis, name, descriptor);
        } else {
            delete globalThis[name];
        }
    }
    globals.clear();
});

test("initializeResourcesLayout registers all handlers, optional resizer, and returns command modifier; dispose cleans up", () => {
    const handle = createFakeElement();
    handle.setAttribute("aria-valuenow", "260");
    const pane = createFakeElement();
    pane.querySelector = selector => selector === ":scope > .resource-pane-resizer" ? handle : null;
    const layoutElement = createFakeElement();
    layoutElement.querySelector = selector => selector === ":scope > .resource-pane" ? pane : null;

    const dotNetRef = { invokeMethodAsync: () => Promise.resolve() };
    const usesCommand = initializeResourcesLayout(layoutElement, dotNetRef, "layout-1", true, 160, 480, 260);

    assert.equal(usesCommand, true);
    assert.equal(handle.tabIndex, 0);
    assert.equal(handle.getAttribute("aria-disabled"), "false");
    assert.ok(layoutElement.listeners.get("dragstart")?.size === 1);
    assert.ok(layoutElement.listeners.get("contextmenu")?.size === 1);
    assert.ok(observers.some(observer => observer.targets.size > 0));

    disposeResourcesLayout(layoutElement, "layout-1");

    assert.equal(handle.tabIndex, -1);
    assert.equal(handle.getAttribute("aria-disabled"), "true");
    for (const listeners of layoutElement.listeners.values()) {
        assert.equal(listeners.size, 0);
    }
    for (const listeners of handle.listeners.values()) {
        assert.equal(listeners.size, 0);
    }
    for (const listeners of windowListeners.values()) {
        assert.equal(listeners.size, 0);
    }
    for (const listeners of documentListeners.values()) {
        assert.equal(listeners.size, 0);
    }
    for (const observer of observers) {
        assert.equal(observer.targets.size, 0);
    }
});

test("initializeResourcesLayout skips resizer registration when registerResizer is false", () => {
    setGlobal("navigator", { platform: "Win32" });
    const handle = createFakeElement();
    handle.setAttribute("aria-valuenow", "260");
    const pane = createFakeElement();
    pane.querySelector = selector => selector === ":scope > .resource-pane-resizer" ? handle : null;
    const layoutElement = createFakeElement();
    layoutElement.querySelector = selector => selector === ":scope > .resource-pane" ? pane : null;

    const dotNetRef = { invokeMethodAsync: () => Promise.resolve() };
    const usesCommand = initializeResourcesLayout(layoutElement, dotNetRef, "layout-2", false, 160, 480, 260);

    assert.equal(usesCommand, false);
    assert.equal(handle.tabIndex, -1);
    assert.equal(handle.listeners.size, 0);

    disposeResourcesLayout(layoutElement, "layout-2");
});
