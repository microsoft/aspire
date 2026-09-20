// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { afterEach, beforeEach, describe, mock, test } from "node:test";
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
        value: Object.assign(new EventTarget(), { getElementById: id => elements.get(id) ?? null }),
    });
    Object.defineProperty(globalThis, "window", {
        configurable: true,
        value: Object.assign(new EventTarget(), {
            name: "",
            crypto: globalThis.crypto,
            sessionStorage: new TestStorage(),
            localStorage: new TestStorage(),
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
        }),
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
    terminalWindows.registerTerminalWindowButton(id, id, owner, "https://localhost/dashboard/");
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

test("the same key retains its handle and focuses without navigation after launcher replacement", () => {
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
    assert.equal(terminalWindows.isTerminalWindowOpen(key), true);

    assert.equal(open(key, nextUrl), "focused");
    assert.equal(calls.length, 1);
    assert.equal(first.popup.focusCalls, 2);
    assert.equal(first.popup.url, firstUrl);
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
    terminalWindows.registerTerminalWindowButton(id, id, owner, "https://localhost/dashboard/");
    button.click();
    await flushNotifications();
    assert.equal(calls.length, 1);
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "opened"]]);

    terminalWindows.unregisterTerminalWindowButton(id);
    button.click();
    await flushNotifications();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].popup.closed, false);
    assert.equal(typeof poll, "function");
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), true);
    assert.equal(notifications.length, 1);

    calls[0].popup.close();
    poll();
    await flushNotifications();
    assert.equal(poll, null);
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), false);
    assert.equal(notifications.length, 1);
});

test("a replacement adopts all requested surviving handles without clicks, focus, or navigation", async () => {
    const old = register("first", "https://localhost/dashboard/terminal-window/apphost/first?fontSize=23");
    old.button.click();
    keys.add("inactive");
    old.button.setAttribute("data-terminal-window-key", "inactive");
    old.button.setAttribute("data-terminal-window-url", "https://localhost/dashboard/terminal-window/apphost/inactive?fontSize=19");
    old.button.click();
    await flushNotifications();
    terminalWindows.unregisterTerminalWindowButton(old.id);
    const unrelated = register("resource:first:0");
    unrelated.button.click();
    await flushNotifications();
    const current = register("first");
    current.button.disabled = true;

    await terminalWindows.adoptTerminalWindows(current.id, ["first", "inactive", "missing"]);
    assert.deepEqual(notifications.slice(3), [
        ["OnTerminalWindowOpenedAsync", "first", "adopted"],
        ["OnTerminalWindowOpenedAsync", "inactive", "adopted"],
    ]);
    assert.equal(calls.length, 3);
    assert.deepEqual(calls.map(call => call.popup.focusCalls), [0, 0, 0]);
    assert.equal(calls[0].popup.url, "https://localhost/dashboard/terminal-window/apphost/first?fontSize=23");
    assert.equal(calls[1].popup.url, "https://localhost/dashboard/terminal-window/apphost/inactive?fontSize=19");

    terminalWindows.unregisterTerminalWindowButton(current.id);
    for (const call of calls) {
        call.popup.close();
    }
    poll();
    await flushNotifications();
    assert.deepEqual(notifications.slice(5), [["OnTerminalWindowClosedAsync", "resource:first:0"]]);
    assert.equal(poll, null);
});

test("a closed orphan is not adopted, even before polling notices its closure", async () => {
    const old = register();
    old.button.click();
    await flushNotifications();
    terminalWindows.unregisterTerminalWindowButton(old.id);
    calls[0].popup.close();
    const current = register();
    await terminalWindows.adoptTerminalWindows(current.id, ["terminal"]);
    poll();
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "opened"]]);
    assert.equal(poll, null);
});

test("adoption transfers callbacks from a stale connected launcher without waiting for its circuit", async () => {
    const { promise, resolve } = Promise.withResolvers();
    const oldCalls = [];
    const old = register("terminal", undefined, (...args) => {
        oldCalls.push(args);
        return promise;
    });
    old.button.click();
    await flushNotifications();
    old.button.click();
    const current = register();

    await terminalWindows.adoptTerminalWindows(current.id, ["terminal"]);
    resolve();
    await flushNotifications();
    terminalWindows.unregisterTerminalWindowButton(old.id);
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), true);
    assert.deepEqual(oldCalls, [["OnTerminalWindowOpenedAsync", "terminal", "opened"]]);
    assert.deepEqual(notifications, [["OnTerminalWindowOpenedAsync", "terminal", "adopted"]]);
    calls[0].popup.close();
    poll();
    await flushNotifications();
    assert.deepEqual(notifications[1], ["OnTerminalWindowClosedAsync", "terminal"]);
});

for (const end of ["return", "close", "dispose", "replace"]) {
    test(`${end} while adoption is queued does not resurrect a detached pane`, async () => {
        const { promise, resolve } = Promise.withResolvers();
        const old = register("old");
        old.button.click();
        await flushNotifications();
        terminalWindows.unregisterTerminalWindowButton(old.id);
        const current = register("current", undefined, (...args) => {
            notifications.push(args);
            return promise;
        });
        current.button.click();
        await flushNotifications();
        const adoption = terminalWindows.adoptTerminalWindows(current.id, ["old"]);

        if (end === "return") {
            terminalWindows.closeTerminalWindow("old");
        } else if (end === "close") {
            calls[0].popup.close();
            poll();
        } else if (end === "dispose") {
            terminalWindows.unregisterTerminalWindowButton(current.id);
        } else {
            const replacement = register("old");
            await terminalWindows.adoptTerminalWindows(replacement.id, ["old"]);
        }
        resolve();
        await adoption;
        await flushNotifications();
        assert.deepEqual(notifications.slice(2), end === "close"
            ? [["OnTerminalWindowClosedAsync", "old"]]
            : end === "replace" ? [["OnTerminalWindowOpenedAsync", "old", "adopted"]] : []);
        assert.equal(calls.length, 2);
        assert.equal(calls[0].popup.closed, end === "return" || end === "close");
    });
}

test("adoption completes only after reconciliation and reports a rejected acknowledgement", async () => {
    const old = register();
    old.button.click();
    await flushNotifications();
    terminalWindows.unregisterTerminalWindowButton(old.id);
    const { promise, reject } = Promise.withResolvers();
    const current = register("terminal", undefined, () => promise);
    const warnings = [];
    mock.method(console, "warn", (...args) => warnings.push(args));
    let completed = false;
    const adoption = terminalWindows.adoptTerminalWindows(current.id, ["terminal"]);
    const rejected = assert.rejects(adoption, /Circuit unavailable/);
    adoption.then(() => { completed = true; }, () => {});
    await flushNotifications();
    assert.equal(completed, false);
    reject(new Error("Circuit unavailable"));
    await rejected;
    assert.equal(completed, false);
    assert.equal(warnings.length, 1);
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

test("a browser close failure still forgets the handle and releases polling", async () => {
    const { button } = register();
    button.click();
    await flushNotifications();
    notifications.length = 0;
    button.click();
    mock.method(calls[0].popup, "close", () => { throw new Error("Browser close denied"); });

    assert.throws(() => terminalWindows.closeTerminalWindow("terminal"), /Browser close denied/);
    assert.equal(terminalWindows.isTerminalWindowOpen("terminal"), false);
    assert.equal(poll, null);
    await flushNotifications();
    assert.deepEqual(notifications, []);
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

class TestStorage {
    values = new Map();
    getItem(key) { return this.values.get(key) ?? null; }
    setItem(key, value) { this.values.set(key, String(value)); }
    removeItem(key) { this.values.delete(key); }
}

describe("cross-document terminal tracking", async () => {
    // Separate module instances model actual document loss: no opener map or WindowProxy is shared across reload.
    // These protocol tests exercise storage/message ordering; the real-browser check verifies browser semantics.
    const documentModules = globalThis.__terminalWindowTestDocuments = new Map();
    const moduleSource = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app-terminalwindow.js", import.meta.url), "utf8");

    function emit(target, type, properties) {
        const event = new Event(type);
        for (const [key, value] of Object.entries(properties)) {
            Object.defineProperty(event, key, { value });
        }
        target.dispatchEvent(event);
    }

    function createBrowser() {
        const windows = [];
        const stores = new Map();
        const messages = [];
        function createWindow(url = "https://localhost/dashboard/", name = "", opener) {
            const browserWindow = {
                location: new URL(url), name, closed: false, suspended: false, focusCalls: 0,
                crypto: globalThis.crypto, sessionStorage: new TestStorage(),
                events: new EventTarget(), timers: new Map(), openCalls: [],
                focus() { this.focusCalls++; },
                close() { this.closed = true; },
                addEventListener(...args) { this.events.addEventListener(...args); },
                removeEventListener(...args) { this.events.removeEventListener(...args); },
                setInterval(callback) { const id = Symbol(); browserWindow.timers.set(id, callback); return id; },
                clearInterval(id) { browserWindow.timers.delete(id); },
                open(targetUrl, targetName, features) {
                    let popup = windows.find(item => item.name === targetName && !item.closed);
                    if (!popup) {
                        popup = createWindow(targetUrl, targetName, browserWindow);
                    }
                    popup.location = new URL(targetUrl);
                    this.openCalls.push({ popup, targetUrl, targetName, features });
                    return popup;
                },
            };
            browserWindow.sessionStorage.values = new Map(opener?.sessionStorage.values);
            if (opener) {
                browserWindow.opener = {
                    get closed() { return opener.closed; },
                    postMessage(data, origin) {
                        messages.push({ data, source: browserWindow, origin: browserWindow.location.origin, target: opener });
                        queueMicrotask(() => {
                            if (!opener.closed && !opener.suspended && opener.location.origin === origin) {
                                emit(opener.events, "message", { data, source: browserWindow, origin: browserWindow.location.origin });
                            }
                        });
                    },
                };
            }
            const store = stores.get(browserWindow.location.origin) ?? new TestStorage();
            stores.set(browserWindow.location.origin, store);
            const changeStorage = (key, value) => {
                const oldValue = store.getItem(key);
                if (value === null) {
                    store.removeItem(key);
                } else {
                    store.setItem(key, value);
                }
                if (oldValue !== value) {
                    for (const other of windows.filter(item => item !== browserWindow && item.location.origin === browserWindow.location.origin)) {
                        queueMicrotask(() => {
                            if (!other.closed && !other.suspended) {
                                emit(other.events, "storage", { key, newValue: value, oldValue });
                            }
                        });
                    }
                }
            };
            browserWindow.localStorage = {
                getItem: key => store.getItem(key),
                setItem: (key, value) => changeStorage(key, String(value)),
                removeItem: key => changeStorage(key, null),
            };
            windows.push(browserWindow);
            return browserWindow;
        }
        return { createWindow, windows, stores, messages };
    }

    async function loadDocument(browserWindow) {
        browserWindow.events = new EventTarget();
        browserWindow.timers.clear();
        const document = Object.assign(new EventTarget(), {
            elements: new Map(),
            getElementById(id) { return this.elements.get(id); },
        });
        browserWindow.document = document;
        const id = ++nextId;
        documentModules.set(id, browserWindow);
        const prelude = `const window = globalThis.__terminalWindowTestDocuments.get(${id});
    const document = window.document;
    const setInterval = window.setInterval;
    const clearInterval = window.clearInterval;\n`;
        const module = await import(`data:text/javascript;base64,${Buffer.from(prelude + moduleSource + `\n//# sourceURL=terminal-window-document-${id}.mjs`).toString("base64")}`);
        return {
            module, window: browserWindow, notifications: [],
            register(key = "terminal", baseUri = "https://localhost/dashboard/") {
                const buttonId = `launcher-${id}-${document.elements.size}`;
                const button = new TestButton(key, `${baseUri}terminal-window/apphost/${encodeURIComponent(key)}?fontSize=23`);
                button.setAttribute("data-terminal-window-focus-group", buttonId);
                document.elements.set(buttonId, button);
                module.registerTerminalWindowButton(buttonId, buttonId,
                    { invokeMethodAsync: async (...args) => { this.notifications.push(args); } }, baseUri);
                return { id: buttonId, button };
            },
            registerPopup(key = "terminal", baseUri = "https://localhost/dashboard/") {
                return module.registerDetachedTerminalWindow(`popup-${id}`, key, baseUri,
                    { invokeMethodAsync: async (...args) => { this.notifications.push(args); } });
            },
            poll() { for (const callback of browserWindow.timers.values()) { callback(); } },
        };
    }

    async function openCoordinatedWindow(browser, baseUri = "https://localhost/dashboard/") {
        const mainWindow = browser.createWindow(baseUri);
        const main = await loadDocument(mainWindow);
        const launcher = main.register("terminal", baseUri);
        await main.module.adoptTerminalWindows(launcher.id, ["terminal"]);
        launcher.button.click();
        const popup = await loadDocument(mainWindow.openCalls[0].popup);
        assert.equal(popup.registerPopup("terminal", baseUri), true);
        await flushNotifications();
        return { main, launcher, popup };
    }

    test("document reload recovers a live WindowProxy without opening, focusing, or navigating the popup", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        const popupUrl = popup.window.location.href;
        const recovered = await loadDocument(main.window);
        const launcher = recovered.register();
        await recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]);
        await flushNotifications();

        assert.equal(main.window.openCalls.length, 1);
        assert.equal(popup.window.location.href, popupUrl);
        assert.equal(popup.window.focusCalls, 0);
        assert.ok(recovered.notifications.some(call => call[2] === "adopted"));
        assert.equal(recovered.module.focusTerminalWindow("terminal"), true);
        assert.equal(popup.window.focusCalls, 1);
        popup.window.close();
        recovered.poll();
        await flushNotifications();
        assert.deepEqual(recovered.notifications.at(-1), ["OnTerminalWindowClosedAsync", "terminal"]);
        assert.equal(recovered.module.isTerminalWindowOpen("terminal"), false);
    });

    test("a suspended or closed-before-recovery popup keeps a conservative placeholder until explicit return", async () => {
        for (const closed of [false, true]) {
            const browser = createBrowser();
            const { main, popup } = await openCoordinatedWindow(browser);
            popup.window.suspended = true;
            popup.window.closed = closed;
            const recovered = await loadDocument(main.window);
            const launcher = recovered.register();
            await recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]);
            for (let i = 0; i < 100; i++) {
                recovered.poll();
            }
            await flushNotifications();
            assert.deepEqual(recovered.notifications, [["OnTerminalWindowOpenedAsync", "terminal", "recovering"]]);
            assert.equal(main.window.openCalls.length, 1);
            assert.equal(recovered.module.isTerminalWindowOpen("terminal"), true);
            recovered.module.closeTerminalWindow("terminal");
            assert.equal(recovered.module.isTerminalWindowOpen("terminal"), false);
            popup.window.suspended = false;
            const reloadedPopup = await loadDocument(popup.window);
            assert.equal(reloadedPopup.registerPopup(), false);
            assert.equal(popup.window.closed, true);
        }
    });

    test("a detached document reload preserves its generation but a returned generation cannot mount again", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        const reloadedPopup = await loadDocument(popup.window);
        assert.equal(reloadedPopup.registerPopup(), true);
        await flushNotifications();
        assert.equal(main.module.isTerminalWindowOpen("terminal"), true);
        assert.equal(main.window.openCalls.length, 1);
        main.module.closeTerminalWindow("terminal");
        const afterReturn = await loadDocument(popup.window);
        assert.equal(afterReturn.registerPopup(), false);
    });

    test("return racing discovery rejects late ready messages and does not close a new generation", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        const recovered = await loadDocument(main.window);
        const launcher = recovered.register();
        const adoption = recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]);
        recovered.module.closeTerminalWindow("terminal");
        await adoption;
        await flushNotifications();
        assert.equal(recovered.module.isTerminalWindowOpen("terminal"), false);
        assert.equal(popup.window.closed, true);
        launcher.button.click();
        const replacement = main.window.openCalls.at(-1).popup;
        assert.notEqual(replacement, popup.window);
        const lateMessage = browser.messages[0];
        emit(main.window.events, "message", lateMessage);
        await flushNotifications();
        assert.equal(replacement.closed, false);
        assert.equal(recovered.module.isTerminalWindowOpen("terminal"), true);
    });

    test("native focus recovers an unresolved named target and retains its font while passive recovery never opens it", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        popup.window.suspended = true;
        const recovered = await loadDocument(main.window);
        const launcher = recovered.register();
        await recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]);
        await flushNotifications();
        assert.equal(main.window.openCalls.length, 1);
        const focus = new TestButton("terminal", "");
        focus.setAttribute("data-terminal-window-focus-key", "terminal");
        focus.setAttribute("data-terminal-window-focus-group", launcher.id);
        focus.closest = selector => selector === "[data-terminal-window-focus-key]" ? focus : null;
        emit(main.window.document, "click", { target: focus });
        assert.equal(main.window.openCalls.length, 2);
        assert.equal(main.window.openCalls[1].popup, popup.window);
        assert.equal(new URL(main.window.openCalls[1].targetUrl).searchParams.get("fontSize"), "23");
    });

    test("dashboard instances, origins, PathBase, and terminal identities do not adopt each other's records", async () => {
        const browser = createBrowser();
        const { main } = await openCoordinatedWindow(browser);
        for (const baseUri of ["https://localhost/dashboard/", "https://localhost/other/", "https://other.example/dashboard/"]) {
            const other = await loadDocument(browser.createWindow(baseUri, "", main.window));
            const launcher = other.register("terminal", baseUri);
            await other.module.adoptTerminalWindows(launcher.id, ["terminal", "another-apphost-terminal"]);
            assert.deepEqual(other.notifications, []);
            assert.equal(other.module.isTerminalWindowOpen("terminal"), false);
        }
        const recovered = await loadDocument(main.window);
        const launcher = recovered.register("new-apphost-terminal");
        await recovered.module.adoptTerminalWindows(launcher.id, ["new-apphost-terminal"]);
        assert.deepEqual(recovered.notifications, []);
    });

    test("spoofed origins and stale document or window generations cannot supply a recovered handle", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        popup.window.suspended = true;
        const recovered = await loadDocument(main.window);
        const launcher = recovered.register();
        await recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]);
        const old = browser.messages[0];
        for (const patch of [
            { origin: "https://evil.example" },
            { data: { ...old.data, baseUri: "https://localhost/other/" } },
            { data: { ...old.data, generation: "previous-window" } },
            {},
        ]) {
            emit(main.window.events, "message", { ...old, ...patch });
        }
        await flushNotifications();
        assert.deepEqual(recovered.notifications, [["OnTerminalWindowOpenedAsync", "terminal", "recovering"]]);
        assert.equal(popup.window.focusCalls, 0);
    });

    test("corrupt or unavailable durable storage rejects recovery rather than reporting no detached windows", async () => {
        for (const corrupt of [false, true]) {
            const browser = createBrowser();
            const { main } = await openCoordinatedWindow(browser);
            const recovered = await loadDocument(main.window);
            const launcher = recovered.register();
            const store = browser.stores.get(main.window.location.origin);
            if (corrupt) {
                const recordKey = [...store.values.keys()].find(key => key.startsWith("aspire-terminal-window:"));
                store.setItem(recordKey, "{invalid-json");
            } else {
                main.window.localStorage.getItem = () => { throw new Error("Storage denied"); };
            }
            assert.throws(() => recovered.module.adoptTerminalWindows(launcher.id, ["terminal"]));
            assert.deepEqual(recovered.notifications, []);
        }
    });

    for (const failure of ["corrupt-json", "invalid-record", "read-denied", "remove-denied"]) {
        test(`explicit return releases its live handle even when durable revocation fails: ${failure}`, async () => {
            const browser = createBrowser();
            const { main, launcher, popup } = await openCoordinatedWindow(browser);
            const store = browser.stores.get(main.window.location.origin);
            const recordKey = [...store.values.keys()].find(key => key.startsWith("aspire-terminal-window:"));
            const raw = store.getItem(recordKey);
            let restore = () => {};
            if (failure === "corrupt-json") {
                store.setItem(recordKey, "{invalid-json");
            } else if (failure === "invalid-record") {
                store.setItem(recordKey, JSON.stringify({ ...JSON.parse(raw), version: 2 }));
            } else {
                const method = failure === "read-denied" ? "getItem" : "removeItem";
                const fault = mock.method(main.window.localStorage, method, () => { throw new Error("Storage denied"); });
                restore = () => fault.mock.restore();
            }

            main.notifications.length = 0;
            launcher.button.click(); // Queue an acknowledgement that must not survive the explicit return.
            assert.throws(() => main.module.closeTerminalWindow("terminal"),
                failure === "corrupt-json" ? SyntaxError
                    : failure === "invalid-record" ? /Invalid detached terminal window record/ : /Storage denied/);
            restore();
            assert.equal(popup.window.closed, true);
            assert.equal(main.module.isTerminalWindowOpen("terminal"), false);
            assert.equal(main.window.timers.size, 0);
            emit(main.window.events, "message", browser.messages[0]);
            await flushNotifications();
            assert.deepEqual(main.notifications, []);
            assert.equal(store.getItem(recordKey),
                failure === "corrupt-json" || failure === "invalid-record" ? null : raw);

            if (store.getItem(recordKey) === null) {
                const reloadedPopup = await loadDocument(popup.window);
                assert.equal(reloadedPopup.registerPopup(), false);
            }
        });
    }

    test("explicit return of an old handle preserves a newer durable generation", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        const store = browser.stores.get(main.window.location.origin);
        const recordKey = [...store.values.keys()].find(key => key.startsWith("aspire-terminal-window:"));
        const record = JSON.parse(store.getItem(recordKey));
        const url = new URL(record.url);
        url.searchParams.set("windowGeneration", "replacement");
        const replacement = JSON.stringify({ ...record, generation: "replacement", url: url.href });
        store.setItem(recordKey, replacement);

        main.module.closeTerminalWindow("terminal");
        assert.equal(popup.window.closed, true);
        assert.equal(main.module.isTerminalWindowOpen("terminal"), false);
        assert.equal(store.getItem(recordKey), replacement);
    });

    for (const failure of ["later-record", "discovery"]) {
        for (const reload of [false, true]) {
            test(`failed adoption leaves no partial acknowledgements or ownership: ${failure}, reload=${reload}`, async () => {
                const browser = createBrowser();
                const { main, launcher: oldLauncher, popup } = await openCoordinatedWindow(browser);
                main.module.unregisterTerminalWindowButton(oldLauncher.id);
                const current = reload ? await loadDocument(main.window) : main;
                const launcher = current.register();
                const store = browser.stores.get(main.window.location.origin);
                const recordKey = [...store.values.keys()].find(key => key.startsWith("aspire-terminal-window:"));
                const identity = JSON.parse(recordKey.slice("aspire-terminal-window:".length));
                identity[2] = "later";
                const laterKey = "aspire-terminal-window:" + JSON.stringify(identity);
                let restore;
                if (failure === "later-record") {
                    store.setItem(laterKey, "{invalid-json");
                    restore = () => store.removeItem(laterKey);
                } else {
                    const fault = mock.method(main.window.localStorage, "setItem", () => { throw new Error("Discovery denied"); });
                    restore = () => fault.mock.restore();
                }
                current.notifications.length = 0;
                assert.throws(() => current.module.adoptTerminalWindows(launcher.id, ["terminal", "later"]),
                    failure === "later-record" ? SyntaxError : /Discovery denied/);
                await flushNotifications();
                const afterFailure = [...current.notifications];
                const trackedAfterFailure = current.module.isTerminalWindowOpen("terminal");

                // Even if queued acknowledgements are suppressed, a failed batch must not acquire ownership:
                // a later close callback would clear the failure placeholder without setting batch readiness.
                popup.window.close();
                current.poll();
                await flushNotifications();
                restore();
                assert.deepEqual({
                    afterFailure, afterClose: current.notifications, trackedAfterFailure,
                }, {
                    afterFailure: [], afterClose: [], trackedAfterFailure: !reload,
                });
                assert.equal(main.window.openCalls.length, 1);
                assert.equal(popup.window.focusCalls, 0);

                await current.module.adoptTerminalWindows(launcher.id, ["terminal", "later"]);
                assert.deepEqual(current.notifications, reload
                    ? [["OnTerminalWindowOpenedAsync", "terminal", "recovering"]]
                    : []);
                current.module.closeTerminalWindow("terminal");
            });
        }
    }

    test("a blocked or failed native popup does not leave a phantom durable detachment after reload", async () => {
        mock.method(console, "error", () => {});
        for (const throws of [false, true]) {
            const browser = createBrowser();
            const main = await loadDocument(browser.createWindow());
            const launcher = main.register();
            await main.module.adoptTerminalWindows(launcher.id, ["terminal"]);
            main.window.open = () => {
                if (throws) {
                    throw new Error("Browser unavailable");
                }
                return null;
            };
            launcher.button.click();
            await flushNotifications();
            assert.deepEqual(main.notifications, [["OnTerminalWindowOpenedAsync", "terminal", throws ? "failed" : "blocked"]]);
            assert.equal(main.module.isTerminalWindowOpen("terminal"), false);
            const recovered = await loadDocument(main.window);
            const replacement = recovered.register();
            await recovered.module.adoptTerminalWindows(replacement.id, ["terminal"]);
            assert.deepEqual(recovered.notifications, []);
        }
    });

    test("empty coordinated-window identities cannot bypass generation validation", async () => {
        const browser = createBrowser();
        const popup = await loadDocument(browser.createWindow(
            "https://localhost/dashboard/terminal-window/apphost/terminal?windowOwner=&windowGeneration="));
        assert.throws(() => popup.registerPopup(), /Incomplete detached terminal window identity/);
    });

    test("terminal disappearance releases the record without disposing a producer or closing an independent page", async () => {
        const browser = createBrowser();
        const { main, popup } = await openCoordinatedWindow(browser);
        popup.module.releaseDetachedTerminalWindow(`popup-${nextId}`);
        await flushNotifications();
        assert.equal(popup.window.closed, false);
        assert.equal(main.module.isTerminalWindowOpen("terminal"), false);
        assert.deepEqual(main.notifications.at(-1), ["OnTerminalWindowClosedAsync", "terminal"]);
    });
});
