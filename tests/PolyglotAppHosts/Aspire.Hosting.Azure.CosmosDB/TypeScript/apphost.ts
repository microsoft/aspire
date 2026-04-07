import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// 1) addAzureCosmosDB
const cosmos = await builder.addAzureCosmosDB("cosmos");

// 2) withDefaultAzureSku
await cosmos.withDefaultAzureSku();

// 3) addCosmosDatabase
const db = await cosmos.addCosmosDatabase("app-db", { databaseName: "appdb" });

// 4) addContainer (single partition key path)
await db.addContainer("orders", "/orderId", { containerName: "orders-container" });

// 5) addContainerWithPartitionKeyPaths (IEnumerable<string> export)
await db.addContainerWithPartitionKeyPaths("events", ["/tenantId", "/eventId"], {
    containerName: "events-container",
});

// 6) withAccessKeyAuthentication
await cosmos.withAccessKeyAuthentication();

// 7) withAccessKeyAuthenticationWithKeyVault
const keyVault = await builder.addAzureKeyVault("kv");
await cosmos.withAccessKeyAuthenticationWithKeyVault(keyVault);

// 8) runAsEmulator + emulator container configuration methods
const cosmosEmulator = await builder.addAzureCosmosDB("cosmos-emulator");
await cosmosEmulator.runAsEmulator({
    configureContainer: async (emulator) => {
        await emulator.withDataVolume({ name: "cosmos-emulator-data" }); // 9) withDataVolume
        await emulator.withGatewayPort({ port: 18081 }); // 10) withGatewayPort
        await emulator.withPartitionCount(25); // 11) withPartitionCount
    },
});

// 12) runAsPreviewEmulator + 13) withDataExplorer
const cosmosPreview = await builder.addAzureCosmosDB("cosmos-preview-emulator");
await cosmosPreview.runAsPreviewEmulator({
    configureContainer: async (emulator) => {
        await emulator.withDataExplorer({ port: 11234 });
    },
});

const app = await builder.build();
await app.run();
