// Aspire TypeScript AppHost — exercises the Aspire.Hosting.JavaScript `AddBunApp` API.
// Run with: aspire run
// Publish with: aspire publish (emits Docker Compose + Dockerfiles using oven/bun:1)

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addDockerComposeEnvironment("compose");

// Direct file execution: `bun server.ts`
await builder
    .addBunApp("bunapp", "./BunFrontend", "server.ts")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

// Package-script execution: `bun run start` (uses the `start` script in package.json)
await builder
    .addBunApp("bunscript", "./BunFrontend", "server.ts")
    .withRunScript("start")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

await builder.build().run();
