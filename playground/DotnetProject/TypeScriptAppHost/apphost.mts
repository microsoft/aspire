import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// TODO: use WaitFor() for strict build and startup ordering between apiservice, workerservice, and worker.
// Until the coordinated .slnx build lands the DotnetProjectResource launches each service via its own `dotnet run`, 
// so two services building the shared Playground.ServiceDefaults/SharedLibrary at the same time
// would race on the build outputs (e.g. *.deps.json). Chaining the waits guarantees only one service builds at a time.

const apiservice = await builder
    .addDotnetProject("apiservice", "../DotnetProject.ApiService")
    .withExternalHttpEndpoints();

const workerservice = await builder
    .addDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .withReference(apiservice)
    .waitFor(apiservice)
    .withExternalHttpEndpoints();

await builder
    .addDotnetProject("worker", "../worker/worker.cs")
    .withReference(apiservice)
    .waitFor(workerservice)
    .withExternalHttpEndpoints();

await builder.build().run();
