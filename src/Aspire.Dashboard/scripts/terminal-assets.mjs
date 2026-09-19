// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { minify } from "terser";

const dashboard = new URL("../", import.meta.url);
const installed = new URL("node_modules/@hex1b/web-terminal/", dashboard);
const copiedAssets = [
    "LICENSE",
    "dist/fonts/cascadia-mono-nf/CascadiaMonoNF.woff2",
    "dist/fonts/cascadia-mono-nf/LICENSE.txt",
];

export async function prepareTerminalAssets() {
    const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
    const packageInfo = JSON.parse(await readFile(new URL("package.json", installed), "utf8"));
    assert.equal(packageInfo.version, manifest.dependencies["@hex1b/web-terminal"],
        "Run npm ci first; the installed package must match the exact manifest version.");

    const files = await readdir(new URL("dist/", installed), { recursive: true });
    assert.deepEqual(files.filter(name => name.endsWith(".js")).sort(), ["index.js"],
        "Expected the upstream single-module distribution before replacing checked-in assets.");

    // Minify the published bundle, not upstream source. Module mode preserves
    // public exports and import.meta.url, which both embedded workers and fonts use.
    const { code } = await minify(await readFile(new URL("dist/index.js", installed), "utf8"), {
        module: true,
        sourceMap: false,
        format: {
            comments: /^!|@preserve|@license/,
            preamble: `// @hex1b/web-terminal ${packageInfo.version}; minified with Terser. See ../LICENSE.`,
        },
    });
    assert.ok(code, "Minification must produce the terminal bundle.");
    const assets = new Map(await Promise.all(copiedAssets.map(async name =>
        [name, await readFile(new URL(name, installed))])));
    assets.set("dist/index.min.js", Buffer.from(code + "\n"));
    return { version: packageInfo.version, assets };
}
