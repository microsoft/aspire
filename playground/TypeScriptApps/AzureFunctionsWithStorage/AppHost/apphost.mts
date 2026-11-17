// Aspire TypeScript AppHost - Azure Functions with Azure Storage
// For more information, see: https://aspire.dev

import { AzureFunctionsLanguage, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureContainerAppEnvironment("azure");

const storage = await builder.addAzureStorage("storage").runAsEmulator();
const blobs = await storage.addBlobs("blobs");
const samples = await storage.addBlobContainer("samples");
const queues = await storage.addQueues("queues");
const backgroundWork = await storage.addQueue("background-work");

const functions = await builder
    .addAzureFunctionsApp("functions", "../Functions", AzureFunctionsLanguage.TypeScript)
    .withExternalHttpEndpoints()
    .withReference(blobs).waitFor(blobs)
    .waitFor(samples)
    .withReference(queues, { connectionName: "workQueue" }).waitFor(queues)
    .waitFor(backgroundWork);

await builder
    .addViteApp("frontend", "../Frontend")
    .withEnvironment("FUNCTIONS_BASE_URL", functions.getEndpoint("http"))
    .waitFor(functions)
    .publishAsStaticWebsite({
        apiPath: "/api",
        apiTarget: functions
    })
    .withExternalHttpEndpoints();

await builder.build().run();
