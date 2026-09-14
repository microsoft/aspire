// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, mock, test } from "node:test";
import * as terminalWindows from "../../../src/Aspire.Dashboard/wwwroot/js/app-terminalwindow.js";

let keys;
let calls;
let poll;
let windowDescriptor;
const owner = { invokeMethodAsync: async () => {} };

beforeEach(() => {
    keys = new Set();
    calls = [];
    poll = null;
    const contexts = new Map();
    windowDescriptor = Object.getOwnPropertyDescriptor(globalThis, "window");
    Object.defineProperty(globalThis, "window", {
        configurable: true,
        value: {
            open(url, name) {
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
                calls.push({ name, popup });
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
    mock.restoreAll();
    if (windowDescriptor) {
        Object.defineProperty(globalThis, "window", windowDescriptor);
    } else {
        delete globalThis.window;
    }
});

function open(key, url) {
    keys.add(key);
    return terminalWindows.openTerminalWindow(key, url, 800, 600, owner);
}

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

    terminalWindows.untrackTerminalWindow(key);
    assert.equal(first.popup.closed, false);
    assert.equal(terminalWindows.isTerminalWindowOpen(key), false);

    assert.equal(open(key, nextUrl), "opened");
    assert.equal(calls.length, 2);
    assert.equal(calls[1].name, first.name);
    assert.equal(calls[1].popup, first.popup);
    assert.equal(first.popup.url, nextUrl);
});
