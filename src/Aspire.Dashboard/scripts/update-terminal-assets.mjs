// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { cp, mkdir, readFile, rm } from "node:fs/promises";

const dashboard = new URL("../", import.meta.url);
const source = new URL("node_modules/@hex1b/web-terminal/", dashboard);
const destination = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
const manifest = JSON.parse(await readFile(new URL("package.json", dashboard), "utf8"));
const installed = JSON.parse(await readFile(new URL("package.json", source), "utf8"));
if (installed.version !== manifest.dependencies["@hex1b/web-terminal"]) {
    throw new Error("Run npm ci before updating terminal assets; the installed package must match the exact manifest version.");
}

await rm(destination, { recursive: true, force: true });
await mkdir(destination, { recursive: true });
// The module worker and font URLs are relative to the emitted modules. Keep
// the complete tree, including maps, declarations, font provenance and licenses.
await cp(new URL("dist/", source), new URL("dist/", destination), { recursive: true });
for (const name of ["LICENSE", "README.md", "package.json"]) {
    await cp(new URL(name, source), new URL(name, destination));
}
console.log(`Vendored @hex1b/web-terminal ${installed.version}.`);
