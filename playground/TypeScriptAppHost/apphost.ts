// Aspire TypeScript AppHost - E2E Demo with PostgreSQL and Express
// This demonstrates compute, databases, and references working together.
// Run with: aspire run
// Publish with: aspire publish

import { createBuilder, refExpr, EnvironmentCallbackContext, ContainerLifetime } from './.modules/aspire.js';

console.log("Aspire TypeScript AppHost starting...\n");

// Create the distributed application builder
const builder = await createBuilder();

const ec = await builder.executionContext.get();

const isPublishMode = await ec.isPublishMode.get();
console.log(`isRunMode: ${await ec.isRunMode.get()}`);
console.log(`isPublishMode: ${isPublishMode}`);

// Add Docker Compose environment for publishing
await builder.addDockerComposeEnvironment("compose");

const dir = await builder.appHostDirectory.get();
console.log(`AppHost directory: ${dir}`);

// Add PostgreSQL server and database
const postgres = builder.addPostgres("postgres");
const db = postgres.addDatabase("db");

console.log("Added PostgreSQL server with database 'db'");

// Add Express API that connects to PostgreSQL (uses npm run dev with tsx)
// No await needed — withReference/waitFor accept promises directly
const api = builder
    .addNodeApp("api", "./express-api", "src/server.ts")
    .withRunScript("dev")
    .withHttpEndpoint({ env: "PORT" })
    .withReference(db)
    .waitFor(db);

console.log("Added Express API with reference to PostgreSQL database");

// Redis
builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);

console.log("Added Redis cache");

// Vite frontend — withReference/waitFor accept the un-awaited 'api' promise
builder
    .addViteApp("frontend", "./vite-frontend")
    .withReference(api)
    .waitFor(api)
    .withEnvironment("CUSTOM_ENV", "value")
    .withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
        // await needed here because getEndpoint returns a value we use
        var ep = await api.getEndpoint("http");
        await ctx.environmentVariables.set("API_ENDPOINT", refExpr`${ep}`);
    });

console.log("Added Vite frontend with reference to API");

// build() flushes all pending promises before running
await builder.build().run();
