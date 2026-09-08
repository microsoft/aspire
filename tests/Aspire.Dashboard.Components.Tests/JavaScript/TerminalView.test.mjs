// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { afterEach, beforeEach, mock, test } from "node:test";
import { readFile } from "node:fs/promises";

const dashboard = new URL("../../../src/Aspire.Dashboard/", import.meta.url);
const assets = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const { WebTerminal } = await import(new URL("dist/index.js", assets));
const source = await readFile(new URL("Components/Controls/TerminalView.razor.js", dashboard), "utf8");
// Remap the public browser asset import to its checked-in location for Node,
// without changing the adapter implementation under test.
const terminal = await import(`data:text/javascript;base64,${Buffer.from(source.replace(
    '"../../js/hex1b-web-terminal/dist/index.js"', JSON.stringify(new URL("dist/index.js", assets).href)
)).toString("base64")}`);

let attempts;
let observers;
let timers;
let frames;
let ids;
let snapshots;
let serial;
const globals = new Map();
let originalMount;

function setGlobal(name, value) {
    globals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}

beforeEach(() => {
    mock.method(console, "warn", () => {});
    attempts = [];
    observers = [];
    timers = new Map();
    frames = new Map();
    ids = [];
    snapshots = [];
    serial = 0;
    setGlobal("window", { isSecureContext: true });
    setGlobal("navigator", { gpu: {} });
    setGlobal("document", { activeElement: null, body: {} });
    setGlobal("requestAnimationFrame", callback => {
        frames.set(++serial, callback);
        return serial;
    });
    setGlobal("cancelAnimationFrame", id => frames.delete(id));
    setGlobal("setTimeout", (callback, delay) => {
        timers.set(++serial, { callback, delay });
        return serial;
    });
    setGlobal("clearTimeout", id => timers.delete(id));
    setGlobal("ResizeObserver", class {
        constructor(callback) {
            this.callback = callback;
            this.disconnected = false;
            observers.push(this);
        }
        observe(element) { this.element = element; }
        disconnect() { this.disconnected = true; }
    });
    originalMount = WebTerminal.mount;
    WebTerminal.mount = (element, options) => {
        const ready = Promise.withResolvers();
        const client = {
            element: { contains: value => value === client.element },
            connected: true,
            peer: { id: "browser-1", primaryId: "cli-1", isPrimary: false },
            geometry: { columns: 100, rows: 30 },
            sizing: { ...options.sizing },
            sizingCalls: [],
            primaryRequests: 0,
            focusCalls: 0,
            selectionRefreshes: 0,
            disposed: false,
            dispose() { this.disposed = true; },
            requestPrimary() { this.primaryRequests++; },
            setSizing(sizing) {
                assert.equal(this.peer.isPrimary, true, "Sizing must wait for confirmed primary");
                this.sizing = sizing;
                this.sizingCalls.push(sizing);
                options.onSizingChange(sizing);
            },
            focus() { this.focusCalls++; },
            refreshSelectionUI() { this.selectionRefreshes++; },
        };
        const attempt = {
            element, options, client,
            resolve() { ready.resolve(client); },
            reject(error = new Error("No first frame")) { ready.reject(error); },
            role(primary) {
                client.peer = { ...client.peer, primaryId: primary ? client.peer.id : "cli-1", isPrimary: primary };
                options.onRoleChange(client.peer);
            },
        };
        // Deliberately allow completion after abort to exercise stale async
        // cleanup independently of the package's own cancellation safeguards.
        attempts.push(attempt);
        return ready.promise;
    };
});

afterEach(async () => {
    for (const id of ids) {
        terminal.disposeTerminal(id);
    }
    for (const attempt of attempts) {
        attempt.reject();
    }
    await settle();
    WebTerminal.mount = originalMount;
    mock.restoreAll();
    for (const [name, descriptor] of globals) {
        if (descriptor) {
            Object.defineProperty(globalThis, name, descriptor);
        } else {
            delete globalThis[name];
        }
    }
    globals.clear();
});

function mount({ visible = true, dotNetRef } = {}) {
    const element = {
        clientWidth: visible ? 800 : 0,
        clientHeight: visible ? 600 : 0,
        contains: value => value === element,
    };
    const id = terminal.initTerminal(element, "wss://dashboard/api/terminal?resource=app&replica=1",
        dotNetRef ?? { invokeMethodAsync: (_name, snapshot) => snapshots.push(snapshot) }, "Localized terminal input");
    ids.push(id);
    return { id, element };
}

async function settle() {
    for (let i = 0; i < 5; i++) {
        await Promise.resolve();
        const pending = [...frames.values()];
        frames.clear();
        for (const callback of pending) {
            callback();
        }
    }
}

function retry() {
    assert.equal(timers.size, 1);
    const [id, { callback, delay }] = timers.entries().next().value;
    timers.delete(id);
    callback();
    return delay;
}

test("init returns an id while mount waits for its first connected frame", async () => {
    const { id } = mount();
    assert.equal(terminal.getToolbarState(id).connected, false);
    assert.equal(attempts[0].options.label, "Localized terminal input");
    assert.equal(attempts[0].options.url, "wss://dashboard/api/terminal?resource=app&replica=1");
    assert.equal(attempts[0].options.renderer, "auto");
    attempts[0].options.onStatus("Socket open", "ready");
    attempts[0].role(false);
    await settle();
    assert.equal(snapshots.at(-1).connected, false);
    attempts[0].resolve();
    await settle();
    assert.deepEqual(snapshots.at(-1), {
        terminalId: id, generation: 1, status: "viewer", connected: true,
        isPrimary: false, canTakeControl: true, sizeMode: "font", sizeKey: "auto",
        fontPx: 13, fontControlsEnabled: true, sizeSelectEnabled: true,
        cols: 100, rows: 30, error: null,
    });
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.equal(attempts[0].options.onInput, undefined);
    assert.equal(attempts[0].options.inputBindings, undefined);
    assert.equal(attempts[0].options.actions, undefined);
    assert.equal(attempts[0].options.readOnly, undefined);
});

test("missing WebGPU and ordinary HTTP leave renderer selection to the package", async () => {
    navigator.gpu = undefined;
    const first = mount();
    navigator.gpu = {};
    window.isSecureContext = false;
    const second = mount();
    assert.equal(attempts.length, 2);
    for (const attempt of attempts) {
        assert.equal(attempt.options.renderer, "auto");
        attempt.resolve();
    }
    await settle();
    assert.equal(timers.size, 0);
    assert.equal(terminal.getToolbarState(first.id).connected, true);
    assert.equal(terminal.getToolbarState(second.id).connected, true);
    assert.equal(terminal.getToolbarState(first.id).error, null);
    assert.equal(terminal.getToolbarState(second.id).error, null);
});

test("hidden initial mounts wait for visibility without consuming the first-frame timeout", async () => {
    const { id, element } = mount({ visible: false });
    assert.equal(attempts.length, 0);
    element.clientWidth = 800;
    element.clientHeight = 600;
    terminal.refreshLayout(id);
    assert.equal(attempts.length, 1);
    observers[0].callback();
    assert.equal(attempts.length, 1);
    attempts[0].resolve();
    await settle();
    element.clientWidth = 0;
    terminal.refreshLayout(id);
    element.clientWidth = 800;
    terminal.refreshLayout(id);
    assert.equal(attempts.length, 1);
    assert.equal(attempts[0].client.selectionRefreshes, 1);
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
});

test("mount failure reports an error and retries with a fresh abortable generation", async () => {
    const { id } = mount();
    attempts[0].reject();
    await settle();
    assert.equal(snapshots.at(-1).error, "mount-failed");
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(retry(), 500);
    assert.equal(terminal.getToolbarState(id).generation, 2);
    assert.equal(attempts.length, 2);
    attempts[1].resolve();
    await settle();
    assert.equal(snapshots.at(-1).error, null);
    assert.equal(snapshots.at(-1).connected, true);
});

test("resource reconnect aborts pending mount and ignores late completion and callbacks", async () => {
    const { id } = mount();
    assert.equal(terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other&replica=2"), 2);
    assert.equal(attempts[0].options.signal.aborted, true);
    attempts[1].resolve();
    await settle();
    const expected = terminal.getToolbarState(id);
    attempts[0].resolve();
    attempts[0].role(true);
    attempts[0].options.onGeometry({ columns: 20, rows: 10 });
    attempts[0].options.onStatus("old socket closed", "error");
    await settle();
    assert.equal(attempts[0].client.disposed, true);
    assert.deepEqual(terminal.getToolbarState(id), expected);
    assert.equal(timers.size, 0);
});

test("a disconnect schedules only one retry and restores focus only if still appropriate", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    document.activeElement = attempts[0].client.element;
    attempts[0].client.connected = false;
    attempts[0].options.onStatus("closed", "error");
    attempts[0].options.onStatus("closed again", "error");
    assert.equal(timers.size, 1);
    assert.equal(attempts[0].client.disposed, true);
    retry();
    document.activeElement = document.body;
    attempts[1].resolve();
    await settle();
    assert.equal(attempts[1].client.focusCalls, 1);
    assert.equal(terminal.getToolbarState(id).connected, true);
});

test("sizing requests primary, waits for confirmation, and clamps to the public font limits", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.setFontSizeFromHost(id, 72);
    assert.equal(attempts[0].client.primaryRequests, 1);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    assert.equal(terminal.getToolbarState(id).isPrimary, false);
    attempts[0].role(true);
    assert.deepEqual(attempts[0].client.sizingCalls, [{ mode: "auto", fontSize: 32 }]);
    terminal.setFontSizeFromHost(id, 4);
    terminal.setSizeModeFromHost(id, "132x50");
    terminal.setSizeModeFromHost(id, "not-a-preset");
    assert.deepEqual(attempts[0].client.sizingCalls, [
        { mode: "auto", fontSize: 32 },
        { mode: "auto", fontSize: 8 },
        { mode: "fixed", columns: 132, rows: 50, fontSize: 8 },
    ]);
    // Geometry remains producer-authoritative; a request cannot rewrite it.
    assert.equal(terminal.getToolbarState(id).cols, 100);
    assert.equal(terminal.getToolbarState(id).sizeKey, "132x50");
    assert.equal(terminal.getToolbarState(id).fontControlsEnabled, false);
});

test("clipboard errors remain visible without discarding the mounted history", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].options.onInputError(new Error("Clipboard denied"));
    await settle();
    assert.equal(terminal.getToolbarState(id).error, "input-failed");
    assert.equal(attempts[0].client.disposed, false);
    assert.equal(timers.size, 0);
});

test("remote role changes authoritatively switch primary, viewer and unclaimed states", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    attempts[0].role(true);
    assert.equal(terminal.getToolbarState(id).status, "primary");
    assert.equal(terminal.getToolbarState(id).canTakeControl, false);
    attempts[0].role(false);
    assert.equal(terminal.getToolbarState(id).status, "viewer");
    assert.equal(terminal.getToolbarState(id).isPrimary, false);
    assert.equal(terminal.getToolbarState(id).canTakeControl, true);
    attempts[0].options.onRoleChange({ id: "browser-1", primaryId: null, isPrimary: false });
    assert.equal(terminal.getToolbarState(id).status, "no-primary");
    assert.equal(terminal.getToolbarState(id).canTakeControl, true);
    assert.equal(attempts[0].client.primaryRequests, 0);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
});

test("dispose aborts pending mount, cancels queued work and ignores later results", async () => {
    const { id } = mount();
    terminal.disposeTerminal(id);
    attempts[0].resolve();
    await settle();
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(attempts[0].client.disposed, true);
    assert.equal(observers[0].disconnected, true);
    assert.equal(terminal.getToolbarState(id), null);
    assert.equal(timers.size, 0);
    assert.deepEqual(snapshots, []);
});

test("rejected Blazor notifications do not become unhandled rejections", async () => {
    const { id } = mount({ dotNetRef: { invokeMethodAsync: () => Promise.reject(new Error("Circuit disposed")) } });
    attempts[0].resolve();
    await settle();
    terminal.refreshToolbarState(id);
    await settle();
    assert.equal(terminal.getToolbarState(id).connected, true);
});

test("explicit reconnect cancels the automatic retry and drops a pending sizing request", async () => {
    const { id } = mount();
    attempts[0].resolve();
    await settle();
    terminal.setSizeModeFromHost(id, "80x24");
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=other");
    assert.equal(attempts[0].options.signal.aborted, true);
    assert.equal(attempts[0].client.disposed, true);
    attempts[0].role(true);
    assert.deepEqual(attempts[0].client.sizingCalls, []);
    attempts[1].reject();
    await settle();
    assert.equal(timers.size, 1);
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=third");
    assert.equal(timers.size, 0);
    attempts[2].resolve();
    await settle();
    assert.equal(terminal.getToolbarState(id).generation, 3);
    assert.equal(terminal.getToolbarState(id).sizeKey, "auto");
});

test("automatic retries are bounded and explicit reconnect resets the exhausted budget", async () => {
    const { id } = mount();
    for (let i = 0; i <= 30; i++) {
        attempts.at(-1).reject();
        await settle();
        if (i < 30) {
            retry();
        }
    }
    assert.equal(attempts.length, 31);
    assert.equal(timers.size, 0);
    assert.equal(terminal.getToolbarState(id).error, "disconnected");
    terminal.reconnectTerminal(id, "wss://dashboard/api/terminal?resource=app");
    attempts.at(-1).reject();
    await settle();
    assert.equal(retry(), 500);
});

test("frontend manifest, lockfile, vendored package and backend use the exact paired version", async () => {
    const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
    const lockfile = JSON.parse(await readFile(new URL("package-lock.json", dashboard), "utf8"));
    const vendored = JSON.parse(await readFile(new URL("package.json", assets), "utf8"));
    const version = manifest.dependencies["@hex1b/web-terminal"];
    assert.equal(version, "0.167.0-alpha.1519.1.b8be265");
    assert.equal(vendored.version, version);
    assert.equal(lockfile.packages[""].dependencies["@hex1b/web-terminal"], version);
    assert.equal(lockfile.packages["node_modules/@hex1b/web-terminal"].version, version);

    // Central package rows have the form:
    //   <PackageVersion Include="Hex1b" Version="0.167.0-alpha..." />
    // Match the exact Include value, not Hex1b.Tool or Hex1b.McpServer;
    // whitespace, attribute order and either XML quote style are allowed.
    const packages = await readFile(new URL("../../Directory.Packages.props", dashboard), "utf8");
    const declarations = [...packages.matchAll(/<PackageVersion\b[^>]*\/>/g)]
        .map(match => match[0])
        .filter(declaration => /\bInclude\s*=\s*["']Hex1b["']/.test(declaration));
    assert.equal(declarations.length, 1, "Expected exactly one central Hex1b library version.");
    const backendVersion = declarations[0].match(/\bVersion\s*=\s*["']([^"']+)["']/);
    assert.ok(backendVersion, "The paired Hex1b library must have an explicit central version.");
    assert.equal(backendVersion[1], version);
});

test("checked-in deployment includes the worker and licensed font without npm installation", async () => {
    for (const name of [
        "dist/index.js",
        "dist/terminal-worker.js",
        "dist/webgpu-backend.js",
        "dist/webgl2-backend.js",
        "dist/hyperlinks.js",
        "dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2",
        "dist/fonts/cascadia-mono-nf/LICENSE.txt",
        "dist/fonts/cascadia-mono-nf/README.md",
        "LICENSE",
        "README.md",
    ]) {
        assert.ok((await readFile(new URL(name, assets))).length > 0, `Missing or empty vendored asset: ${name}`);
    }
});

test("entry, module worker and bundled font URLs preserve PathBase and same origin", async () => {
    // Inspect the emitted forms:
    //   import { WebTerminal, ... } from "../../js/.../dist/index.js";
    //   new Worker(new URL("./terminal-worker.js", import.meta.url), ...);
    //   new URL("./fonts/.../CascadiaMonoNF.woff2", import.meta.url).href;
    // Keeping these module-relative URLs avoids both PathBase escapes and
    // blob/cross-origin worker URLs that require relaxing the dashboard CSP.
    const entryReference = source.match(/from "([^"]+)"/)[1];
    const entryUrl = new URL(entryReference, "https://dashboard.example/nested/aspire/Components/Controls/TerminalView.razor.js");
    assert.equal(entryUrl.href, "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/index.js");
    const clientSource = await readFile(new URL("dist/web-terminal.js", assets), "utf8");
    const workerReference = clientSource.match(/new Worker\(new URL\("([^"]+)", import\.meta\.url\)/)[1];
    assert.equal(new URL(workerReference, entryUrl).href,
        "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/terminal-worker.js");
    const fontSource = await readFile(new URL("dist/terminal-font.js", assets), "utf8");
    const fontReference = fontSource.match(/new URL\("([^"]+)", import\.meta\.url\)/)[1];
    assert.equal(new URL(fontReference, entryUrl).href,
        "https://dashboard.example/nested/aspire/js/hex1b-web-terminal/dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2");
});
