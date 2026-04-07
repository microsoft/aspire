// Aspire TypeScript AppHost - E2E Demo with PostgreSQL and Express
// This demonstrates compute, databases, and references working together.
// Run with: aspire run
// Publish with: aspire publish

import { createBuilder, refExpr, EnvironmentCallbackContext, ContainerLifetime } from './.modules/aspire.js';

console.log("Aspire TypeScript AppHost starting...\n");

// Create the distributed application builder
const builder = await createBuilder();

var ec = await builder.executionContext.get();

const isPublishMode = await ec.isPublishMode.get();
console.log(`isRunMode: ${await ec.isRunMode.get()}`);
console.log(`isPublishMode: ${isPublishMode}`);

// Add Docker Compose environment for publishing
await builder.addDockerComposeEnvironment("compose");

var dir = await builder.appHostDirectory.get();
console.log(`AppHost directory: ${dir}`);

// Add PostgreSQL server and database
const postgres = await builder.addPostgres("postgres");
const db = await postgres.addDatabase("db");
const localCancellation = new AbortController();
const dbUriExpression = await db.uriExpression.get();
const _dbUri = await dbUriExpression.getValue(localCancellation.signal);

console.log("Added PostgreSQL server with database 'db'");

// Add Express API that connects to PostgreSQL (uses npm run dev with tsx)
const api = await builder
    .addNodeApp("api", "./express-api", "src/server.ts")
    .withRunScript("dev")
    .withHttpEndpoint({ env: "PORT" })
    .withReference(db)
    .waitFor(db);

console.log("Added Express API with reference to PostgreSQL database");

// Also keep Redis as an example of another service with persistent lifetime
const cache = await builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);

console.log("Added Redis cache");

// Add Vite frontend that connects to the API using the unified reference API
await builder
    .addViteApp("frontend", "./vite-frontend")
    .withReference(api)
    .waitFor(api)
    .withEnvironment("CUSTOM_ENV", "value")
    .withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
        // Custom environment callback logic
        var ep = await api.getEndpoint("http");

        await ctx.environmentVariables.set("API_ENDPOINT", refExpr`${ep}`);
    });

console.log("Added Vite frontend with reference to API");

await builder.build().run();
