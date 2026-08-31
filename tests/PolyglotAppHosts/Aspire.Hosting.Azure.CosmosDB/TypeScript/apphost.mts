import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// 1) addAzureCosmosDB
const cosmos = await builder.addAzureCosmosDB("cosmos");
await cosmos.configureInfrastructure(async infrastructure => {
    const account = await infrastructure.getCosmosDBAccount();
    const tags = await account.tags.get();
    await tags.set("provisioning-proxy", "typescript");
    const bypassResourceId = await infrastructure.createCosmosDBResourceIdentifier(
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared/providers/Microsoft.DocumentDB/databaseAccounts/bypass",
    );
    const bypassResourceIds = await account.networkAclBypassResourceIds.get();
    await bypassResourceIds.add(bypassResourceId);
});

// 2) withDefaultAzureSku
await cosmos.withDefaultAzureSku();

// 3) addCosmosDatabase
const db = await cosmos.addCosmosDatabase("app-db", { databaseName: "appdb" });

// 4) addContainer (single partition key path)
await db.addContainer("orders", "/orderId", { containerName: "orders-container" });

// 5) addContainer (IEnumerable<string> partition key paths)
await db.addContainer("events", ["/tenantId", "/eventId"], {
    containerName: "events-container",
});

// 6) withAccessKeyAuthentication
await cosmos.withAccessKeyAuthentication();

// 7) withAccessKeyAuthentication(keyVault)
const keyVault = await builder.addAzureKeyVault("kv");
await cosmos.withAccessKeyAuthentication({ keyVaultBuilder: keyVault });

// 8) runAsEmulator + emulator container configuration methods
const cosmosEmulator = await builder.addAzureCosmosDB("cosmos-emulator");
await cosmosEmulator.runAsEmulator({
    configureContainer: async (emulator) => {
        await emulator.withDataVolume({ name: "cosmos-emulator-data" }); // 9) withDataVolume
        await emulator.withGatewayPort({ port: 18081 }); // 10) withGatewayPort
        await emulator.withDataExplorer({ port: 11234 }); // 11) withDataExplorer
    },
});

// 12) runAsClassicEmulator + 13) withPartitionCount
const cosmosClassic = await builder.addAzureCosmosDB("cosmos-classic-emulator");
await cosmosClassic.runAsClassicEmulator({
    configureContainer: async (emulator) => {
        await emulator.withPartitionCount(25);
    },
});

const app = await builder.build();
await app.run();
