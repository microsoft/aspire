// Aspire TypeScript AppHost — exercises the Aspire.Hosting.JavaScript `AddDenoApp` API.
// Run with: aspire run
// Publish with: aspire publish (emits Docker Compose + Dockerfiles using denoland/deno:2)

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addDockerComposeEnvironment("compose");

// Direct file execution: `deno run -A main.ts`
await builder
    .addDenoApp("denoapp", "./DenoFrontend", "main.ts")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

// Task execution: `deno task start` (uses the `start` task in deno.json)
await builder
    .addDenoApp("denoscript", "./DenoFrontend", "main.ts")
    .withRunScript("start")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

await builder.build().run();
