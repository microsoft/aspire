// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { prepareTerminalAssets } from "./terminal-assets.mjs";

const dashboard = new URL("../", import.meta.url);
const vendored = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const { version, assets } = await prepareTerminalAssets();

async function listFiles(root) {
    const entries = await readdir(root, { recursive: true, withFileTypes: true });
    return entries.filter(entry => entry.isFile())
        .map(entry => relative(root, join(entry.parentPath, entry.name)).split(sep).join("/"))
        .sort();
}

assert.deepEqual(await listFiles(fileURLToPath(vendored)), [...assets.keys()].sort(),
    "Only the minified runtime, font and required licenses should be vendored.");
for (const [name, content] of assets) {
    assert.deepEqual(await readFile(new URL(name, vendored)), content, name);
}
console.log(`Verified ${assets.size} runtime/license files for @hex1b/web-terminal ${version}.`);
