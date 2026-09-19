// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/Components/Controls/TerminalTitle.razor.js", import.meta.url), "utf8");
const { compactPath, observePath, disconnectPath } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("paths that fit retain every character, including roots and trailing separators", () => {
    for (const path of ["/", "/home/user/app/", "C:\\work\\app", "\\\\server\\share\\app", "relative/path", "/a//b", "/user/e\u0301/\u{1f680}"]) {
        assert.equal(compactPath(path, value => value.length <= path.length), path);
    }
});

test("middle omissions preserve whole leading and trailing components", () => {
    for (const [path, expected] of [
        ["/home/user/projects/service/src", "/home/user/\u2026/service/src"],
        ["/home/a\\b/projects/service/src", "/home/a\\b/\u2026/service/src"],
        ["C:\\work\\projects\\service\\src", "C:\\work\\\u2026\\service\\src"],
        ["/home/user/projects/service/src/", "/\u2026/src/"],
        ["/one-very-long-component", "/\u2026"],
        ["one-very-long-component", "\u2026"],
    ]) {
        assert.equal(compactPath(path, value => value.length <= expected.length), expected);
    }
});

test("every width omits only complete path segments, never partial names", () => {
    for (const path of [
        "/Users/name/projects/aspire/src",
        "C:\\Users\\name\\projects\\aspire\\src",
        "\\\\server\\share\\projects\\aspire\\",
        "/user/e\u0301/\u{1f469}\u200d\u{1f4bb}/项目/a long final directory",
        "a-single-component-without-separators",
    ]) {
        const original = new Set([...path.split(/[\\/]/), "\u2026", ""]);
        for (let width = 0; width <= path.length; width++) {
            const display = compactPath(path, value => value.length <= width);
            assert.ok(display.length <= width);
            assert.ok(display.split(/[\\/]/).every(segment => original.has(segment)), display);
        }
    }
});

test("measurement follows resize, path changes and fonts, and releases observers", async () => {
    const originals = new Map();
    const install = (name, value) => {
        originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
        Object.defineProperty(globalThis, name, { value, configurable: true });
    };
    const resizes = [];
    const mutations = [];
    const fonts = new EventTarget();
    fonts.ready = Promise.resolve();
    const context = { font: "", measureText: value => ({ width: value ? value.length + 0.2 : 0 }) };
    const display = { textContent: "" };
    const textBox = width => ({
        clientWidth: width,
        getBoundingClientRect() { return { width: this.clientWidth + 0.25 }; },
        querySelector: () => display,
    });
    let text = textBox(6);
    let path = "/home/user/project/src";
    const element = { querySelector: () => text, getAttribute: () => path };
    try {
        install("document", { fonts, createElement: () => ({ getContext: () => context }) });
        install("getComputedStyle", () => ({ font: "12px monospace" }));
        install("ResizeObserver", class {
            constructor(callback) { this.callback = callback; this.targets = new Set(); resizes.push(this); }
            observe(target) { this.targets.add(target); }
            unobserve(target) { this.targets.delete(target); }
            disconnect() { this.targets.clear(); }
        });
        install("MutationObserver", class {
            constructor(callback) { this.callback = callback; mutations.push(this); }
            observe(target) { this.target = target; }
            disconnect() { this.target = null; }
        });

        observePath(element);
        assert.equal(display.textContent, "/\u2026/src");
        assert.equal(context.font, "12px monospace");
        text.clientWidth = 100;
        resizes[0].callback();
        assert.equal(display.textContent, path);
        text.clientWidth = path.length;
        resizes[0].callback();
        assert.equal(display.textContent, path, "A fractional-width path that fits must remain complete.");
        path = "/other/project";
        mutations[0].callback();
        assert.equal(display.textContent, path);
        text.clientWidth = 2;
        fonts.dispatchEvent(new Event("loadingdone"));
        assert.equal(display.textContent, "/\u2026");
        text = null;
        path = "";
        mutations[0].callback();
        assert.equal(resizes[0].targets.size, 0);
        text = textBox(100);
        path = "C:\\work\\restored";
        mutations[0].callback();
        assert.equal(display.textContent, path);
        assert.deepEqual([...resizes[0].targets], [text]);

        disconnectPath(element);
        assert.equal(resizes[0].targets.size, 0);
        assert.equal(mutations[0].target, null);
        display.textContent = "disposed";
        await fonts.ready;
        fonts.dispatchEvent(new Event("loadingdone"));
        resizes[0].callback();
        assert.equal(display.textContent, "disposed");
    } finally {
        disconnectPath(element);
        for (const [name, original] of originals) {
            if (original) {
                Object.defineProperty(globalThis, name, original);
            } else {
                delete globalThis[name];
            }
        }
    }
});
