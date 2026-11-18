// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { registerTabOverflow, unregisterTabOverflow } from "../../../src/Aspire.Dashboard/Components/Layout/ResourcesLayout.razor.js";

const globals = new Map();
let frames;
let observers;
let registrationIds;
let nav;
let documentListeners;

class FakeElement {
    closest() { return this; }
}

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    frames = new Map();
    observers = [];
    registrationIds = [];
    let frameId = 0;
    class Observer {
        constructor(callback) {
            this.callback = callback;
            this.targets = new Set();
            observers.push(this);
        }
        observe(target) { this.targets.add(target); }
        disconnect() { this.targets.clear(); }
    }
    setGlobal("ResizeObserver", Observer);
    setGlobal("MutationObserver", Observer);
    setGlobal("requestAnimationFrame", callback => {
        const id = ++frameId;
        frames.set(id, callback);
        return id;
    });
    setGlobal("cancelAnimationFrame", id => frames.delete(id));
    setGlobal("window", new EventTarget());
    documentListeners = new Map();
    setGlobal("Element", FakeElement);
    setGlobal("document", {
        body: {},
        activeElement: null,
        elements: new Map(),
        querySelector: () => nav,
        getElementById(id) { return this.elements.get(id) ?? null; },
        addEventListener(type, listener) { documentListeners.set(type, listener); },
        removeEventListener(type, listener) {
            if (documentListeners.get(type) === listener) {
                documentListeners.delete(type);
            }
        },
    });
    setGlobal("getComputedStyle", () => ({ display: "flex" }));
    nav = {
        clientWidth: 350,
        tablist: null,
        querySelector() { return this.tablist; },
    };
    replaceTabs([100, 100, 100]);
});

afterEach(() => {
    for (const registrationId of registrationIds) {
        unregisterTabOverflow(registrationId);
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

function replaceTabs(widths) {
    const tabs = widths.map((width, index) => ({
        id: `tab-${index}`,
        overflow: false,
        get offsetWidth() { return this.overflow ? 0 : width; },
        removeAttribute() { this.overflow = false; },
        setAttribute() { this.overflow = true; },
        getBoundingClientRect() {
            const left = tabs.slice(0, index).reduce((sum, tab) => sum + tab.offsetWidth, 0);
            return { left, right: left + this.offsetWidth };
        },
    }));
    nav.tablist = { querySelectorAll: () => tabs };
    return tabs;
}

function flushFrames() {
    const callbacks = [...frames.values()];
    frames.clear();
    callbacks.forEach(callback => callback());
}

function register() {
    const registrationId = `layout-${registrationIds.length}`;
    registrationIds.push(registrationId);
    const reports = [];
    registerTabOverflow(registrationId, {
        invokeMethodAsync(method, ids) {
            assert.equal(method, "SetOverflowTabs");
            reports.push(ids);
            return Promise.resolve();
        },
    });
    flushFrames();
    return { registrationId, reports, resize: observers.at(-3), tabs: observers.at(-2), body: observers.at(-1) };
}

test("disposing a replaced recording layout keeps the new navigation responsive", () => {
    const previous = register();
    const current = register();
    unregisterTabOverflow(previous.registrationId);

    nav.clientWidth = 250;
    for (const observer of observers.filter(observer => observer.targets.has(nav))) {
        observer.callback();
    }
    flushFrames();

    assert.deepEqual(current.reports, [["tab-2"]]);
    assert.equal(current.resize.targets.has(nav), true);
    assert.equal(previous.resize.targets.size, 0);
});

test("cleanup after DOM removal disconnects observers and cancels pending measurements", () => {
    const previous = register();
    nav = null;
    previous.body.callback();
    assert.equal(frames.size, 1);

    unregisterTabOverflow(previous.registrationId);

    assert.equal(previous.resize.targets.size, 0);
    assert.equal(previous.tabs.targets.size, 0);
    assert.equal(previous.body.targets.size, 0);
    assert.equal(frames.size, 0);
    window.dispatchEvent(new Event("resize"));
    assert.equal(frames.size, 0);
    flushFrames();
    assert.deepEqual(previous.reports, []);
});

test("keyboard focus moves to the same tab when navigation re-creates the strip", () => {
    const current = register();
    const previous = Object.assign(new FakeElement(), { id: "tab-Console", isConnected: true });
    documentListeners.get("focusin")({ target: previous });

    // The navigation removes the focused tab with its strip, so focus falls to the page.
    previous.isConnected = false;
    document.activeElement = document.body;
    const replacement = { id: "tab-Console", focus() { document.activeElement = this; } };
    document.elements.set("tab-Console", replacement);
    current.body.callback();

    assert.equal(document.activeElement, replacement);
});

test("focus elsewhere isn't moved to a tab when the strip is re-created", () => {
    const current = register();
    const previous = Object.assign(new FakeElement(), { id: "tab-Console", isConnected: true });
    documentListeners.get("focusin")({ target: previous });
    const search = { id: "search" };
    documentListeners.get("focusin")({ target: null });
    previous.isConnected = false;
    document.activeElement = search;
    document.elements.set("tab-Console", { id: "tab-Console", focus() { document.activeElement = this; } });
    current.body.callback();

    assert.equal(document.activeElement, search);
});

test("cleanup removes the focus listener", () => {
    const current = register();
    assert.equal(documentListeners.has("focusin"), true);
    unregisterTabOverflow(current.registrationId);
    assert.equal(documentListeners.has("focusin"), false);
});

test("navigation that replaces only the tab list reconnects its content observer", () => {
    const current = register();
    const previousTablist = nav.tablist;
    replaceTabs([100, 100, 200]);
    current.body.callback();
    flushFrames();

    assert.deepEqual([...current.tabs.targets], [nav.tablist]);
    assert.equal(current.tabs.targets.has(previousTablist), false);
    assert.deepEqual(current.reports, [["tab-2"]]);

    replaceTabs([100, 100, 100]);
    current.body.callback();
    flushFrames();
    assert.deepEqual(current.reports, [["tab-2"], []]);
});

test("fractional widths move whole tabs into overflow and restore them when space returns", () => {
    const tabs = replaceTabs([100.25, 100.25, 100.25]);
    nav.clientWidth = 300;
    const current = register();
    assert.deepEqual(current.reports, [["tab-2"]]);
    assert.deepEqual(tabs.map(tab => tab.overflow), [false, false, true]);

    nav.clientWidth = 301;
    current.resize.callback();
    flushFrames();
    assert.deepEqual(current.reports, [["tab-2"], []]);
    assert.deepEqual(tabs.map(tab => tab.overflow), [false, false, false]);

    unregisterTabOverflow(current.registrationId);
    assert.equal(current.resize.targets.size, 0);
    assert.equal(current.tabs.targets.size, 0);
    assert.equal(current.body.targets.size, 0);
    assert.equal(frames.size, 0);
});
