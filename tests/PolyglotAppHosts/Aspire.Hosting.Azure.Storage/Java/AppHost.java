import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var storage = builder.addAzureStorage("storage");
        storage.runAsEmulator();
        storage.withRoleAssignments(storage, new AzureStorageRole[] { AzureStorageRole.STORAGE_BLOB_DATA_CONTRIBUTOR, AzureStorageRole.STORAGE_QUEUE_DATA_CONTRIBUTOR });
        // Callbacks are currently not working
        // storage.runAsEmulator({
        //     configureContainer: (emulator) -> {
        //         emulator.withBlobPort(10000);
        //         emulator.withQueuePort(10001);
        //         emulator.withTablePort(10002);
        //         emulator.withDataVolume();
        //         emulator.withApiVersionCheck(new WithApiVersionCheckOptions().enable(false));
        //     }
        // });
        storage.addBlobs("blobs");
        storage.addTables("tables");
        storage.addQueues("queues");
        storage.addQueue("orders");
        storage.addBlobContainer("images");
        builder.build().run();
    }
