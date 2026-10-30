// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

const load = async name => {
    const source = await readFile(new URL(`../../../src/Aspire.Dashboard/Components/Layout/${name}.razor.js`, import.meta.url), "utf8");
    return import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
};
const { registerTabOverflow, unregisterTabOverflow } = await load("ResourcesLayout");
const { observeHeader, disconnectHeader } = await load("MainLayout");
const meterSource = await readFile(new URL("../../../src/Aspire.Dashboard/Components/Controls/Chart/MeterOverview.razor.js", import.meta.url), "utf8");
const { observeCards } = await import(`data:text/javascript;base64,${Buffer.from(meterSource).toString("base64")}`);

test("metrics navigation does not attach observers to a container already removed by the router", () => {
    assert.equal(observeCards(null, {}), null);
    assert.equal(observeCards({ isConnected: false }, {}), null);
});

function environment() {
    const originals = new Map();
    const install = (name, value) => {
        originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
        Object.defineProperty(globalThis, name, { configurable: true, value });
    };
    const frames = new Map();
    const observers = [];
    let id = 0;
    const fonts = new EventTarget();
    fonts.ready = new Promise(() => {});
    install("document", { body: {}, fonts, querySelector: () => null });
    install("window", new EventTarget());
    install("requestAnimationFrame", callback => { frames.set(++id, callback); return id; });
    install("cancelAnimationFrame", key => frames.delete(key));
    const observer = class {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe(target) { this.target = target; }
        disconnect() { this.target = null; }
    };
    install("ResizeObserver", observer);
    install("MutationObserver", observer);
    install("getComputedStyle", () => ({ display: "flex", columnGap: "6px" }));
    return {
        observers, fonts,
        flush() {
            for (const [key, callback] of [...frames]) {
                frames.delete(key);
                callback();
            }
        },
        dispose() {
            for (const [name, descriptor] of originals) {
                if (descriptor) {
                    Object.defineProperty(globalThis, name, descriptor);
                } else {
                    delete globalThis[name];
                }
            }
        }
    };
}

function attributes(values = {}) {
    const attrs = new Map(Object.entries(values));
    return {
        getAttribute: name => attrs.get(name) ?? null,
        setAttribute: (name, value) => attrs.set(name, value),
        removeAttribute: name => attrs.delete(name),
        hasAttribute: name => attrs.has(name),
        toggleAttribute(name, on) { if (on) { attrs.set(name, ""); } else { attrs.delete(name); } }
    };
}

test("tab overflow follows keyed host replacement, exact available space and font loading", () => {
    const env = environment();
    try {
        const reports = [];
        let tabs;
        const replaceTabs = () => tabs = ["Overview", "Console", "StructuredLogs", "Traces", "Metrics"].map(id => ({
            ...attributes(), id,
            get offsetWidth() { return this.hasAttribute("data-tab-overflow") ? 0 : 100; },
            getBoundingClientRect() { return { right: tabs.slice(0, tabs.indexOf(this) + 1).reduce((sum, tab) => sum + tab.offsetWidth, 0) }; }
        }));
        replaceTabs();
        const tablist = { querySelectorAll: () => tabs };
        const nav = {
            ...attributes({ "data-overflow-location": "/resources/api", "data-overflow-desktop": "false" }),
            clientWidth: 350, isConnected: true,
            getBoundingClientRect: () => ({ left: 0 }),
            querySelector: selector => selector === ".resources-tab-header" ? tablist : { getBoundingClientRect: () => ({ width: 32 }) }
        };
        document.querySelector = () => nav;
        registerTabOverflow({ invokeMethodAsync: (...args) => { reports.push(args); return Promise.resolve(); } });
        env.flush();
        assert.equal(env.observers[1].target, nav, "Observe the stable nav, not the replaceable FluentTabs host.");
        assert.deepEqual(reports.at(-1), ["SetOverflowTabs", ["Traces", "Metrics"], "/resources/api", false]);

        replaceTabs();
        nav.setAttribute("data-overflow-location", "/consolelogs/resource/api");
        env.observers[1].callback();
        env.flush();
        assert.deepEqual(tabs.filter(tab => tab.offsetWidth > 0).map(tab => tab.id), ["Overview", "Console", "StructuredLogs"]);
        assert.deepEqual(reports.at(-1), ["SetOverflowTabs", ["Traces", "Metrics"], "/consolelogs/resource/api", false]);

        nav.clientWidth = 500;
        env.observers[0].callback();
        env.flush();
        assert.deepEqual(reports.at(-1)[1], []);
        assert.equal(nav.hasAttribute("data-has-tab-overflow"), false);
        assert.equal(tabs.every(tab => tab.offsetWidth === 100), true);

        nav.clientWidth = 499;
        env.fonts.dispatchEvent(new Event("loadingdone"));
        env.flush();
        assert.deepEqual(reports.at(-1)[1], ["Metrics"]);
        assert.equal(nav.hasAttribute("data-has-tab-overflow"), true);
        const count = reports.length;
        unregisterTabOverflow();
        env.fonts.dispatchEvent(new Event("loadingdone"));
        env.flush();
        assert.equal(reports.length, count);
        assert.equal(env.observers.every(observer => observer.target === null), true);
    } finally {
        unregisterTabOverflow();
        env.dispose();
    }
});

test("header moves only actions that cannot fit, restores them on resize and disconnects", () => {
    const env = environment();
    try {
        const actions = ["help", "notifications", "settings"].map(id => ({ ...attributes({ "data-header-action": id }) }));
        const reports = [];
        const header = {
            ...attributes(),
            clientWidth: 300,
            querySelectorAll: () => actions,
            get scrollWidth() {
                const actionWidth = actions.filter(action => !action.hasAttribute("data-header-overflow")).length * 32;
                return 220 + actionWidth + (this.hasAttribute("data-has-header-overflow") ? 32 : 0);
            }
        };
        observeHeader(header, { invokeMethodAsync: (...args) => { reports.push(args); return Promise.resolve(); } });
        env.flush();
        assert.deepEqual(reports.at(-1), ["SetHeaderOverflowActions", ["notifications", "settings"]]);
        assert.equal(actions[0].hasAttribute("data-header-overflow"), false);
        header.clientWidth = 316;
        env.observers[0].callback();
        env.flush();
        assert.deepEqual(reports.at(-1), ["SetHeaderOverflowActions", []]);
        assert.equal(actions.every(action => !action.hasAttribute("data-header-overflow")), true);
        disconnectHeader(header);
        assert.equal(env.observers.every(observer => observer.target === null), true);
    } finally {
        env.dispose();
    }
});
