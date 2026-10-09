// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = await readFile(new URL("../../../src/Aspire.Dashboard/wwwroot/js/app.js", import.meta.url), "utf8");

function createClipboard() {
    const requests = [];
    const timers = new Map();
    let nextTimer = 1;
    const copyIcon = { style: { display: "" } };
    const checkmarkIcon = { style: { display: "none" } };
    const status = { classList: { contains: name => name === "terminal-copy-status" }, textContent: "" };
    const tooltip = { innerText: "Copy" };
    const button = {
        dataset: {},
        nextElementSibling: status,
        querySelector: selector => selector === ".copy-icon" ? copyIcon : checkmarkIcon,
        getAttribute: name => name === "data-copyfailed" ? "Copy failed" : null,
    };
    const document = {
        getElementById: () => button,
        querySelector: () => ({ children: [tooltip] }),
        addEventListener() {},
    };
    const window = { document };
    runInNewContext(source, {
        window, document,
        navigator: {
            clipboard: {
                writeText(text) {
                    return new Promise((resolve, reject) => requests.push({ text, resolve, reject }));
                },
            },
        },
        customElements: { define() {} },
        CSSStyleSheet: class { replaceSync() {} },
        setTimeout(callback, delay) {
            assert.equal(delay, 1500);
            const id = nextTimer++;
            timers.set(id, callback);
            return id;
        },
        clearTimeout: id => timers.delete(Number(id)),
    });
    return {
        requests, timers, status, tooltip, copyIcon, checkmarkIcon, button,
        copy: text => window.copyTextToClipboard("copy-button", text, "Copy", "Copied"),
        reset() {
            assert.equal(timers.size, 1);
            const [id, callback] = timers.entries().next().value;
            timers.delete(id);
            callback();
        },
    };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test("clipboard success receives a full feedback interval after the write finishes", async () => {
    const clipboard = createClipboard();
    clipboard.copy("first");
    assert.equal(clipboard.requests[0].text, "first");
    assert.equal(clipboard.timers.size, 0);
    clipboard.requests[0].resolve();
    await flush();
    assert.equal(clipboard.status.textContent, "Copied");
    assert.equal(clipboard.tooltip.innerText, "Copied");
    assert.equal(clipboard.copyIcon.style.display, "none");
    assert.equal(clipboard.checkmarkIcon.style.display, "");
    assert.equal(clipboard.timers.size, 1);

    clipboard.reset();
    assert.equal(clipboard.status.textContent, "");
    assert.equal(clipboard.tooltip.innerText, "Copy");
    assert.equal(clipboard.copyIcon.style.display, "");
    assert.equal(clipboard.checkmarkIcon.style.display, "none");
    assert.equal(clipboard.button.dataset.copyTimeout, undefined);
});

test("clipboard failure also resets after the write finishes", async () => {
    const clipboard = createClipboard();
    clipboard.copy("first");
    assert.equal(clipboard.timers.size, 0);
    clipboard.requests[0].reject(new Error("Permission denied"));
    await flush();
    assert.equal(clipboard.status.textContent, "Copy failed");
    assert.equal(clipboard.tooltip.innerText, "Copy failed");
    assert.equal(clipboard.timers.size, 1);
    clipboard.reset();
    assert.equal(clipboard.status.textContent, "");
    assert.equal(clipboard.tooltip.innerText, "Copy");
});

test("a late clipboard result cannot overwrite feedback from a newer request", async () => {
    const clipboard = createClipboard();
    clipboard.copy("old");
    clipboard.copy("new");
    clipboard.requests[1].resolve();
    await flush();
    assert.equal(clipboard.status.textContent, "Copied");
    assert.equal(clipboard.timers.size, 1);
    clipboard.requests[0].reject(new Error("Old request failed"));
    await flush();
    assert.equal(clipboard.status.textContent, "Copied");
    assert.equal(clipboard.tooltip.innerText, "Copied");
    assert.equal(clipboard.timers.size, 1);

    clipboard.copy("third");
    assert.equal(clipboard.timers.size, 0);
    assert.equal(clipboard.status.textContent, "");
    assert.equal(clipboard.tooltip.innerText, "Copy");
    assert.equal(clipboard.checkmarkIcon.style.display, "none");
    clipboard.requests[2].reject(new Error("New request failed"));
    await flush();
    assert.equal(clipboard.status.textContent, "Copy failed");
    assert.equal(clipboard.timers.size, 1);
    clipboard.reset();
    assert.equal(clipboard.status.textContent, "");
});
