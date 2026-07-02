// Aspire TypeScript AppHost — exercises the Aspire.Hosting.Dotnet `addDotnetProject` API by adding C#
// projects (and a file-based C# app) by path, mirroring the C# app host in the sibling
// DotnetProject.AppHost folder. Run with: aspire run

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// Backend API added by path via addDotnetProject.
const apiservice = await builder
    .addDotnetProject("apiservice", "../DotnetProject.ApiService")
    .withExternalHttpEndpoints();

// A second .csproj service that references the same shared library and calls the API. withReference wires
// service discovery; waitFor also serializes startup so the two services don't race building the shared
// library before the coordinated build (Session 5) lands.
await builder
    .addDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .withReference(apiservice)
    .waitFor(apiservice)
    .withExternalHttpEndpoints();

// A file-based C# app (launched as `dotnet run --file worker.cs`), added by path to the .cs file.
await builder
    .addDotnetProject("worker", "../worker/worker.cs")
    .withReference(apiservice)
    .waitFor(apiservice)
    .withExternalHttpEndpoints();

await builder.build().run();
