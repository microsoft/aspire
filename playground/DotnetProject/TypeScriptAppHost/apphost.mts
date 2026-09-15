import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const apiservice = await builder
    .addDotnetProject("apiservice", "../DotnetProject.ApiService")
    .withExternalHttpEndpoints();

await builder
    .addDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .withReference(apiservice)
    .withExternalHttpEndpoints();

await builder
    .addDotnetProject("worker", "../worker/worker.cs")
    .withReference(apiservice)
    .withExternalHttpEndpoints();

await builder.build().run();
