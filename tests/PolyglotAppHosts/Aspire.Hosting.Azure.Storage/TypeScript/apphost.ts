import { AzureStorageRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const storage = await builder.addAzureStorage("storage");
await storage.runAsEmulator();
await storage.withRoleAssignments(storage, [AzureStorageRole.StorageBlobDataContributor, AzureStorageRole.StorageQueueDataContributor]);

// Callbacks are currently not working
// await storage.runAsEmulator({
//     configureContainer: async (emulator) => {
//         await emulator.withBlobPort(10000);
//         await emulator.withQueuePort(10001);
//         await emulator.withTablePort(10002);
//         await emulator.withDataVolume();
//         await emulator.withApiVersionCheck({ enable: false });
//     }
// });

await storage.addBlobs("blobs");
await storage.addTables("tables");
await storage.addQueues("queues");
await storage.addQueue("orders");
await storage.addBlobContainer("images");

await builder.build().run();
