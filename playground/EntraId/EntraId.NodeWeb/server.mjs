// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { readFileSync } from "node:fs";
import { createServer } from "node:https";
import { createApp, readConfiguration } from "./app.mjs";

const configuration = readConfiguration(process.env);
const port = Number(process.env.PORT);
if (!Number.isInteger(port) || port < 1 || port > 65535)
{
    throw new Error("PORT must be a valid TCP port supplied by Aspire.");
}

const server = createServer({
    cert: readFileSync(process.env.HTTPS_CERT_FILE),
    key: readFileSync(process.env.HTTPS_CERT_KEY_FILE)
}, await createApp(configuration));
server.listen(port, "0.0.0.0", () => console.log("Node Entra web front end is listening on its HTTPS endpoint."));

for (const signal of ["SIGINT", "SIGTERM"])
{
    process.on(signal, () => server.close());
}
