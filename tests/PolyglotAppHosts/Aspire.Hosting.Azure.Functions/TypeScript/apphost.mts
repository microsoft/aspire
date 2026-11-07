// Aspire TypeScript AppHost — Azure Functions validation
// Exercises every exported member of Aspire.Hosting.Azure.Functions

import { AzureFunctionsLanguage, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const storage = await builder.addAzureStorage("funcstorage");

// ── 1. addDotnetAzureFunction and its typed fluent APIs ─────────────────────
const functions = await builder.addDotnetAzureFunction(
    "dotnetfunc",
    "../MyFunctions/MyFunctions.csproj",
);
await functions.withHostStorage(storage);
await functions.withBuildEnvironment("BUILD_KEY", "build-value");
await functions.withReplicas(2);
await functions.disableForwardedHeaders();
await functions.withEndpointsInEnvironment(["http"]);

// ── 2. Legacy addAzureFunctionsProject compatibility surface ────────────────
const legacyFuncApp = await builder.addAzureFunctionsProject(
    "legacyfunc",
    "../OtherFunc/OtherFunc.csproj",
);
await legacyFuncApp.withHostStorage(storage);

// ── 3. addAzureFunctionsApp — directory-based TypeScript Functions app ──────
const nodeFuncApp = await builder.addAzureFunctionsApp(
    "nodefunc",
    "../NodeFunctions",
    AzureFunctionsLanguage.TypeScript,
);
await nodeFuncApp
    .withHostStorage(storage)
    .withReference(storage);

await builder.build().run();
