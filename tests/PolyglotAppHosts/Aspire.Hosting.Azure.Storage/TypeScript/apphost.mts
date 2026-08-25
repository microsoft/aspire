import {
    AzureStorageRole,
    ProvisioningValueType,
    StorageNetworkDefaultAction,
    StoragePublicNetworkAccess,
    createBuilder,
} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const storageSku = await builder.addParameter("storageSku");

const storage = await builder.addAzureStorage("storage");
await storage.configureInfrastructure(async infrastructure => {
    const account = await infrastructure.getStorageAccount();
    const tags = await account.tags.get();
    await tags.set("provisioning-proxy", "typescript");

    const bicep = infrastructure.bicep();
    const sku = await infrastructure.createStorageSku();
    await sku.name.set(bicep.parameter(storageSku));
    await account.sku.set(sku);
    await account.allowSharedKeyAccess.set(true);
    await account.isHnsEnabled.set(true);
    await account.publicNetworkAccess.set(StoragePublicNetworkAccess.Enabled);

    const networkRules = await account.networkRuleSet.get();
    await networkRules.defaultAction.set(StorageNetworkDefaultAction.Allow);

    const accountReference = await bicep.resourceIdentifier(account);
    const accountName = await bicep.member(accountReference, "name");
    const output = await infrastructure.addBicepOutput("storageAccountName", ProvisioningValueType.String);
    await output.value.set(accountName);
    const _accounts = await infrastructure.getStorageAccounts();
});
await storage.runAsEmulator();
await storage.withStorageRoleAssignments(storage, [AzureStorageRole.StorageBlobDataContributor, AzureStorageRole.StorageQueueDataContributor]);

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
