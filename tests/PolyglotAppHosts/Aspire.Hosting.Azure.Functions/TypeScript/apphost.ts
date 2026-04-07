// Aspire TypeScript AppHost — Azure Functions validation
// Exercises every exported member of Aspire.Hosting.Azure.Functions

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// ── 1. addAzureFunctionsProject (path-based overload) ───────────────────────
const funcApp = await builder.addAzureFunctionsProject(
    "myfunc",
    "../MyFunctions/MyFunctions.csproj",
);

// ── 2. withHostStorage — specify custom Azure Storage for Functions host ────
const storage = await builder.addAzureStorage("funcstorage");
await funcApp.withHostStorage(storage);

// ── 3. Fluent chaining — verify return types enable chaining ────────────────
await builder
    .addAzureFunctionsProject("chained-func", "../OtherFunc/OtherFunc.csproj")
    .withHostStorage(storage)
    .withEnvironment("MY_KEY", "my-value")
    .withHttpEndpoint({ port: 7071 });

// ── 4. withReference from base builder — standard resource references ───────
const anotherStorage = await builder.addAzureStorage("appstorage");
await funcApp.withReference(anotherStorage);

await builder.build().run();