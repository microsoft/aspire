// Aspire TypeScript AppHost - Azure Functions E2E (TypeScript)
// For more information, see: https://aspire.dev

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Azure Storage with emulator (queues + blobs)
const storage = builder.addAzureStorage("storage").runAsEmulator();
const queue = await storage.addQueues("queue");
const blob = await storage.addBlobs("blob");
const myBlobContainer = await storage.addBlobContainer("myblobcontainer");

// Azure Event Hubs with emulator
const eventHub = await builder.addAzureEventHubs("eventhubs")
    .runAsEmulator()
    .addHub("myhub");

// TypeScript Azure Functions app (Node.js v4 programming model)
const funcApp = await builder
    .addJavaScriptApp("funcapp", "../TypeScriptFunctions", { runScriptName: "start" })
    .withHttpEndpoint({ targetPort: 7071 })
    .withExternalHttpEndpoints()
    .withReference(queue).waitFor(queue)
    .withReference(blob).waitFor(blob)
    .withReference(myBlobContainer).waitFor(myBlobContainer)
    .withReference(eventHub).waitFor(eventHub);

// TypeScript API service (Express)
await builder
    .addJavaScriptApp("apiservice", "../TypeScriptApiService", { runScriptName: "dev" })
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints()
    .withReference(queue)
    .withReference(blob)
    .withReference(eventHub).waitFor(eventHub)
    .withServiceReference(funcApp).waitFor(funcApp);

await builder.build().run();
