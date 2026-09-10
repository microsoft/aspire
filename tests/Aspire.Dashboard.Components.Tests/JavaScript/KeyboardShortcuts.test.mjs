// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app.js", import.meta.url), "utf8");

function element(tagName, activeElement, parentElement = null) {
    return {
        tagName, children: [], parentElement,
        shadowRoot: activeElement ? { activeElement, children: [activeElement] } : null,
        closest(selector) {
            for (let current = this; current; current = current.parentElement) {
                if (current.tagName.toLowerCase() === selector) {
                    return current;
                }
            }
            return null;
        },
    };
}

function shortcutsFor(activeElement) {
    const listeners = new Map();
    const shortcuts = [];
    const document = {
        activeElement,
        readyState: "loading",
        body: { querySelector: () => null, classList: { remove() {} } },
        addEventListener: (type, listener) => listeners.set(type, listener),
        removeEventListener: type => listeners.delete(type),
    };
    const window = { document, addEventListener() {} };
    runInNewContext(source, {
        document, window,
        customElements: { define() {} },
        CSSStyleSheet: class { replaceSync() {} },
    });
    const registration = window.registerGlobalKeydownListener({
        invokeMethodAsync: (method, shortcut) => {
            assert.equal(method, "OnGlobalKeyDown");
            shortcuts.push(shortcut);
        },
    });
    for (const key of ["c", "r", "s", "t", "m", "?", "S", "+", "-"]) {
        listeners.get("keydown")({ key });
    }
    window.unregisterGlobalKeydownListener(registration);
    assert.equal(listeners.has("keydown"), false);
    return shortcuts;
}

test("terminal textarea focus inside a non-Fluent shadow host suppresses dashboard shortcuts", () => {
    assert.deepEqual(shortcutsFor(element("DIV", element("TEXTAREA"))), []);
});

test("nested shadow input focus suppresses dashboard shortcuts", () => {
    assert.deepEqual(shortcutsFor(element("DIV", element("CUSTOM-EDITOR", element("INPUT")))), []);
});

test("native and Fluent inputs still suppress dashboard shortcuts", () => {
    for (const target of [element("INPUT"), element("TEXTAREA"), element("FLUENT-TEXT-FIELD", element("INPUT"))]) {
        assert.deepEqual(shortcutsFor(target), []);
    }
});

test("Fluent dropdown controls and options suppress dashboard shortcuts", () => {
    const dropdown = element("FLUENT-DROPDOWN", element("BUTTON"));
    const nestedControl = element("DIV", element("BUTTON"), dropdown);
    for (const target of [
        dropdown,
        element("BUTTON", null, dropdown),
        element("FLUENT-OPTION", null, dropdown),
        nestedControl,
        element("DIV", dropdown),
    ]) {
        assert.deepEqual(shortcutsFor(target), []);
    }
});

test("dashboard shortcuts remain available outside inputs", () => {
    for (const target of [element("BODY"), element("BUTTON"), element("DIV", element("BUTTON"))]) {
        assert.deepEqual(shortcutsFor(target), [210, 200, 220, 230, 240, 100, 110, 330, 340]);
    }
});
