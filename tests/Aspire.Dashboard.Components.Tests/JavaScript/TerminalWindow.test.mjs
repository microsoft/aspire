// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, mock, test } from "node:test";
import * as terminalWindows from "../../../src/Aspire.Dashboard/wwwroot/js/app-terminalwindow.js";

let keys;
let calls;
let poll;
let windowDescriptor;
let documentDescriptor;
let elements;
let buttons;
let notifications;
let registrations;
let nextId = 0;

beforeEach(() => {
    keys = new Set();
    calls = [];
    buttons = new Map();
    notifications = [];
    registrations = [];
    elements = new Map();
    poll = null;
    const contexts = new Map();
    windowDescriptor = Object.getOwnPropertyDescriptor(globalThis, "window");
    documentDescriptor = Object.getOwnPropertyDescriptor(globalThis, "document");
    Object.defineProperty(globalThis, "document", {
        configurable: true,
        value: { getElementById: id => elements.get(id) ?? null },
    });
    Object.defineProperty(globalThis, "window", {
        configurable: true,
        value: {
            open(url, name, features) {
                // Browsers reuse and navigate an existing browsing context with the same target name.
                let popup = contexts.get(name);
                if (!popup || popup.closed) {
                    popup = {
                        closed: false,
                        focusCalls: 0,
                        focus() { this.focusCalls++; },
                        close() { this.closed = true; },
                    };
                    contexts.set(name, popup);
                }
                popup.url = url;
                calls.push({ name, popup, features });
                return popup;
            },
        },
    });
    mock.method(globalThis, "setInterval", callback => {
        poll = callback;
        return 1;
    });
    mock.method(globalThis, "clearInterval", () => { poll = null; });
});

afterEach(() => {
    for (const key of keys) {
        terminalWindows.closeTerminalWindow(key);
    }
    poll?.();
    for (const id of registrations) {
        terminalWindows.unregisterTerminalWindowButton(id);
    }
    mock.restoreAll();
    if (windowDescriptor) {
        Object.defineProperty(globalThis, "window", windowDescriptor);
    } else {
        delete globalThis.window;
    }
    if (documentDescriptor) {
        Object.defineProperty(globalThis, "document", documentDescriptor);
    } else {
        delete globalThis.document;
    }
});

function open(key, url) {
    keys.add(key);
    const existing = terminalWindows.isTerminalWindowOpen(key);
    const button = buttons.get(key) ?? register(key, url).button;
    button.setAttribute("data-terminal-window-url", url);
    button.click();
    return terminalWindows.isTerminalWindowOpen(key) ? existing ? "focused" : "opened" : "blocked";
}

function register(key = "terminal", url = "https://localhost/dashboard/terminal-window/apphost/terminal?fontSize=17", callback) {
    keys.add(key);
    const button = new TestButton(key, url);
    const id = `button-${++nextId}`;
    const owner = { invokeMethodAsync: callback ?? (async (...args) => { notifications.push(args); }) };
    registrations.push(id);
    elements.set(id, button);
    buttons.set(key, button);
    terminalWindows.registerTerminalWindowButton(id, id, owner);
    return { id, button, owner };
}

const flushNotifications = () => new Promise(resolve => setImmediate(resolve));

for (const [firstKey, secondKey] of [
    ["resource:a.b:0", "resource:a_b:0"],
    ["resource:a:b:0", "resource:a_b:0"],
    ["resource:a/b:0", "resource:a_b:0"],
    ["resource:caf\u00e9:0", "resource:caf\u00e8:0"],
    ["resource:a%3Ab:0", "resource:a:b:0"],
]) {
    test(`distinct keys keep separate windows: ${firstKey} and ${secondKey}`, () => {
        const firstUrl = "https://localhost/dashboard/terminal-window/resource/first/0";
        const secondUrl = "https://localhost/dashboard/terminal-window/resource/second/0";
        assert.equal(open(firstKey, firstUrl), "opened");
        assert.equal(open(secondKey, secondUrl), "opened");

        const [first, second] = calls;
        assert.notEqual(first.name, second.name);
        assert.notEqual(first.popup, second.popup);
        assert.equal(first.popup.url, firstUrl);
        assert.equal(second.popup.url, secondUrl);

        assert.equal(terminalWindows.focusTerminalWindow(firstKey), true);
        assert.equal(first.popup.focusCalls, 1);
        assert.equal(second.popup.focusCalls, 0);

        terminalWindows.closeTerminalWindow(firstKey);
        assert.equal(first.popup.closed, true);
        assert.equal(second.popup.closed, false);
        assert.equal(terminalWindows.isTerminalWindowOpen(firstKey), false);
        assert.equal(terminalWindows.isTerminalWindowOpen(secondKey), true);
    });
}

test("the same key focuses its window and reuses its stable name after untracking", () => {
    const key = "resource:a.b:0";
    const firstUrl = "https://localhost/dashboard/terminal-window/resource/a.b/0";
    const nextUrl = `${firstUrl}?fontSize=16`;
    assert.equal(open(key, firstUrl), "opened");
    const first = calls[0];
    assert.equal(open(key, nextUrl), "focused");
    assert.equal(calls.length, 1);
    assert.equal(first.popup.focusCalls, 1);
    assert.equal(first.popup.url, firstUrl);

    terminalWindows.unregisterTerminalWindowButton(registrations[0]);
    buttons.delete(key);
    assert.equal(first.popup.closed, false);
    assert.equal(terminalWindows.isTerminalWindowOpen(key), false);

    assert.equal(open(key, nextUrl), "opened");
    assert.equal(calls.length, 2);
    assert.equal(calls[1].name, first.name);
    assert.equal(calls[1].popup, first.popup);
    assert.equal(first.popup.url, nextUrl);
});

test("native clicks open and focus synchronously, even while a previous .NET acknowledgement is pending", async () => {
    const { promise, resolve } = Promise.withResolvers();
    const { button } = register("first", "https://localhost/dashboard/terminal-window/apphost/first?fontSize=19",
        (...args) => {
            assert.ok(calls.length > 0, "window.open must precede the first .NET notification");
            notifications.push(args);
            return promise;
        });
    button.click();
    assert.equal(calls.length, 1);
    assert.deepEqual(notifications, []);
    assert.match(calls[0].features, /width=960,height=600$/);
    await flushNotifications();
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "first", "opened"]]);

    button.click();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].popup.focusCalls, 1);
    keys.add("second");
    button.setAttribute("data-terminal-window-key", "second");
    button.setAttribute("data-terminal-window-url", "https://localhost/dashboard/terminal-window/apphost/second?fontSize=23");
    button.click();
    assert.equal(calls.length, 2);
    assert.equal(calls[1].popup.url, "https://localhost/dashboard/terminal-window/apphost/second?fontSize=23");
    assert.equal(notifications.length, 1);

    resolve();
    await flushNotifications();
    assert.deepEqual(notifications, [
        ["OnTerminalWindowOpenedAsync", "first", "opened"],
        ["OnTerminalWindowOpenedAsync", "first", "focused"],
        ["OnTerminalWindowOpenedAsync", "second", "opened"],
    ]);
});

for (const gate of ["disabled-property", "disabled-attribute", "aria-disabled", "inert", "removed", "missing-key", "missing-url"]) {
    test(`native listener rejects ${gate} controls without opening or notifying`, async () => {
        const { button } = register();
        switch (gate) {
            case "disabled-property": button.disabled = true; break;
            case "disabled-attribute": button.setAttribute("disabled", ""); break;
            case "aria-disabled": button.setAttribute("aria-disabled", "true"); break;
            case "inert": button.inertAncestor = true; break;
            case "removed": button.isConnected = false; break;
            case "missing-key": button.removeAttribute("data-terminal-window-key"); break;
            case "missing-url": button.removeAttribute("data-terminal-window-url"); break;
        }
        button.click();
        await flushNotifications();
        assert.deepEqual(calls, []);
        assert.deepEqual(notifications, []);
        assert.equal(poll, null);
    });
}

test("blocked popups are reported with the captured key and can be retried", async () => {
    const { button } = register();
    const open = mock.method(window, "open", () => null);
    button.click();
    button.setAttribute("data-terminal-window-key", "changed");
    await flushNotifications();
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "blocked"]]);
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), false);
    assert.equal(poll, null);

    open.mock.restore();
    button.setAttribute("data-terminal-window-key", "terminal");
    button.click();
    await flushNotifications();
    assert.deepEqual(notifications[1], ["OnTerminalWindowOpenedAsync", "terminal", "opened"]);
});

test("re-registration removes the old listener and disposal leaves independent windows open", async () => {
    const { button, id, owner } = register();
    terminalWindows.registerTerminalWindowButton(id, id, owner);
    button.click();
    await flushNotifications();
    assert.equal(calls.length, 1);
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "opened"]]);

    terminalWindows.unregisterTerminalWindowButton(id);
    button.click();
    await flushNotifications();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].popup.closed, false);
    assert.equal(poll, null);
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), false);
    assert.equal(notifications.length, 1);
});

test("disposing an old owner cannot untrack a window adopted by a replacement button", async () => {
    const old = register();
    old.button.click();
    await flushNotifications();
    const current = register();
    current.button.click();
    terminalWindows.unregisterTerminalWindowButton(old.id);
    await flushNotifications();
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), true);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].popup.focusCalls, 1);
    calls[0].popup.close();
    poll();
    await flushNotifications();
    assert.deepEqual(notifications, [
        ["OnTerminalWindowOpenedAsync", "terminal", "opened"],
        ["OnTerminalWindowOpenedAsync", "terminal", "focused"],
        ["OnTerminalWindowClosedAsync", "terminal"],
    ]);
});

test("a user close waits for the detach acknowledgement and is reported exactly once", async () => {
    const { promise, resolve } = Promise.withResolvers();
    const { button } = register("terminal", undefined, (...args) => {
        notifications.push(args);
        return args[0] === "OnTerminalWindowOpenedAsync" ? promise : Promise.resolve();
    });
    button.click();
    await flushNotifications();
    calls[0].popup.close();
    poll();
    assert.equal(poll, null);
    assert.equal(notifications.length, 1);
    resolve();
    await flushNotifications();
    assert.deepEqual(notifications, [
        ["OnTerminalWindowOpenedAsync", "terminal", "opened"],
        ["OnTerminalWindowClosedAsync", "terminal"],
    ]);
});

for (const end of ["return", "close", "dispose"]) {
    test(`${end} cancels queued open notifications instead of resurrecting a detached pane`, async () => {
        const { promise, resolve } = Promise.withResolvers();
        const { button, id } = register("terminal", undefined, (...args) => {
            notifications.push(args);
            return promise;
        });
        button.click();
        await flushNotifications();
        button.click();
        if (end === "return") {
            terminalWindows.closeTerminalWindow("terminal");
        } else if (end === "close") {
            calls[0].popup.close();
            poll();
        } else {
            terminalWindows.unregisterTerminalWindowButton(id);
        }
        resolve();
        await flushNotifications();
        assert.deepEqual(notifications, end === "close"
            ? [["OnTerminalWindowOpenedAsync", "terminal", "opened"], ["OnTerminalWindowClosedAsync", "terminal"]]
            : [["OnTerminalWindowOpenedAsync", "terminal", "opened"]]);
        assert.equal(calls[0].popup.closed, end !== "dispose");
    });
}

test("reopening a closed window suppresses its obsolete queued close notification", async () => {
    const { promise, resolve } = Promise.withResolvers();
    const { button } = register("terminal", undefined, (...args) => {
        notifications.push(args);
        return promise;
    });
    button.click();
    await flushNotifications();
    calls[0].popup.close();
    poll();
    button.click();
    assert.equal(calls.length, 2);
    resolve();
    await flushNotifications();
    assert.deepEqual(notifications, [
        ["OnTerminalWindowOpenedAsync", "terminal", "opened"],
        ["OnTerminalWindowOpenedAsync", "terminal", "opened"],
    ]);
});

test("failed browser operations and rejected notifications are observed without poisoning later clicks", async () => {
    const errors = [];
    const warnings = [];
    mock.method(console, "error", (...args) => errors.push(args));
    mock.method(console, "warn", (...args) => warnings.push(args));
    const open = mock.method(window, "open", () => { throw new Error("Browser unavailable"); });
    const { button } = register("terminal", undefined, async (...args) => {
        notifications.push(args);
        throw new Error("Circuit unavailable");
    });
    button.click();
    await flushNotifications();
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "failed"]]);
    assert.equal(errors.length, 1);
    assert.equal(warnings.length, 1);
    open.mock.restore();
    button.click();
    assert.equal(calls.length, 1);
    await flushNotifications();
    assert.deepEqual(notifications[1], ["OnTerminalWindowOpenedAsync", "terminal", "opened"]);
    assert.equal(warnings.length, 2);
});

class TestButton extends EventTarget {
    isConnected = true;
    disabled = false;
    inertAncestor = false;
    attributes = new Map();

    constructor(key, url) {
        super();
        this.setAttribute("data-terminal-window-key", key);
        this.setAttribute("data-terminal-window-url", url);
    }

    setAttribute(key, value) { this.attributes.set(key, value); }
    getAttribute(key) { return this.attributes.get(key) ?? null; }
    hasAttribute(key) { return this.attributes.has(key); }
    removeAttribute(key) { this.attributes.delete(key); }
    closest() { return this.inertAncestor ? this : null; }
    click() { this.dispatchEvent(new Event("click")); }
}
