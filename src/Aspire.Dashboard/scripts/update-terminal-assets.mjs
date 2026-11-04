// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { mkdir, rm, writeFile } from "node:fs/promises";
import { prepareTerminalAssets } from "./terminal-assets.mjs";

const dashboard = new URL("../", import.meta.url);
const destination = new URL("wwwroot/js/hex1b-web-terminal/", dashboard);
// Validate and prepare every output before deleting the previous acquisition.
const { version, assets } = await prepareTerminalAssets();

await rm(destination, { recursive: true, force: true });
for (const [name, content] of assets) {
    const file = new URL(name, destination);
    await mkdir(new URL(".", file), { recursive: true });
    await writeFile(file, content);
}
console.log(`Vendored ${assets.size} runtime/license files for @hex1b/web-terminal ${version}.`);
