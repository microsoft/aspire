// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { registerTabOverflow, unregisterTabOverflow } from "../../../src/Aspire.Dashboard/Components/Layout/ResourcesLayout.razor.js";

const globals = new Map();
let frames;
let observers;
let owners;
let nav;

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    frames = new Map();
    observers = [];
    owners = [];
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
    setGlobal("document", { body: {}, querySelector: () => nav });
    setGlobal("getComputedStyle", () => ({ display: "flex" }));
    nav = {
        clientWidth: 350,
        tablist: null,
        querySelector() { return this.tablist; },
    };
    replaceTabs([100, 100, 100]);
});

afterEach(() => {
    for (const owner of owners) {
        unregisterTabOverflow(owner);
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
    const owner = {};
    owners.push(owner);
    const reports = [];
    registerTabOverflow(owner, {
        invokeMethodAsync(method, ids) {
            assert.equal(method, "SetOverflowTabs");
            reports.push(ids);
            return Promise.resolve();
        },
    });
    flushFrames();
    return { owner, reports, resize: observers.at(-3), tabs: observers.at(-2), body: observers.at(-1) };
}

test("disposing a replaced recording layout keeps the new navigation responsive", () => {
    const previous = register();
    const current = register();
    unregisterTabOverflow(previous.owner);

    nav.clientWidth = 250;
    for (const observer of observers.filter(observer => observer.targets.has(nav))) {
        observer.callback();
    }
    flushFrames();

    assert.deepEqual(current.reports, [["tab-2"]]);
    assert.equal(current.resize.targets.has(nav), true);
    assert.equal(previous.resize.targets.size, 0);
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

    unregisterTabOverflow(current.owner);
    assert.equal(current.resize.targets.size, 0);
    assert.equal(current.tabs.targets.size, 0);
    assert.equal(current.body.targets.size, 0);
    assert.equal(frames.size, 0);
});
