// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const dashboard = new URL("../", import.meta.url);
const installed = new URL("node_modules/@hex1b/web-terminal/", dashboard);
const vendored = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
const packageInfo = JSON.parse(await readFile(new URL("package.json", installed), "utf8"));
assert.equal(packageInfo.version, manifest.dependencies["@hex1b/web-terminal"],
    "Run npm ci before verifying terminal assets; the installed package must match the exact manifest version.");

async function listFiles(root) {
    const entries = await readdir(root, { recursive: true, withFileTypes: true });
    return entries.filter(entry => entry.isFile())
        .map(entry => relative(root, join(entry.parentPath, entry.name)))
        .sort();
}

const installedDist = fileURLToPath(new URL("dist/", installed));
const vendoredDist = fileURLToPath(new URL("dist/", vendored));
const files = await listFiles(installedDist);
assert.deepEqual(await listFiles(vendoredDist), files,
    "The vendored dist tree must contain every installed file and no stale extras.");
for (const name of files) {
    assert.deepEqual(await readFile(join(vendoredDist, name)), await readFile(join(installedDist, name)), name);
}
for (const name of ["LICENSE", "README.md", "package.json"]) {
    assert.deepEqual(await readFile(new URL(name, vendored)), await readFile(new URL(name, installed)), name);
}
console.log(`Verified ${files.length} dist files and package metadata/licenses for @hex1b/web-terminal ${packageInfo.version}.`);
