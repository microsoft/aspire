import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // 1) addAzureCosmosDB
        var cosmos = builder.addAzureCosmosDB("cosmos");
        // 2) withDefaultAzureSku
        cosmos.withDefaultAzureSku();
        // 3) addCosmosDatabase
        var db = cosmos.addCosmosDatabase("app-db", "appdb");
        // 4) addContainer (single partition key path)
        db.addContainer("orders", "/orderId", "orders-container");
        // 5) addContainer (IEnumerable<string> partition key paths)
        db.addContainer("events", new String[] { "/tenantId", "/eventId" }, "events-container");
        // 6) withAccessKeyAuthentication
        cosmos.withAccessKeyAuthentication();
        // 7) withAccessKeyAuthentication(keyVault)
        var keyVault = builder.addAzureKeyVault("kv");
        cosmos.withAccessKeyAuthentication(keyVault);
        // 8) runAsEmulator + emulator container configuration methods
        var cosmosEmulator = builder.addAzureCosmosDB("cosmos-emulator");
        cosmosEmulator.runAsEmulator((emulator) -> {
            emulator.withDataVolume("cosmos-emulator-data"); // 9) withDataVolume
            emulator.withGatewayPort(18081.0); // 10) withGatewayPort
            emulator.withPartitionCount(25); // 11) withPartitionCount
        });
        // 12) runAsPreviewEmulator + 13) withDataExplorer
        var cosmosPreview = builder.addAzureCosmosDB("cosmos-preview-emulator");
        cosmosPreview.runAsPreviewEmulator((emulator) -> {
                emulator.withDataExplorer(11234.0);
            });
        var app = builder.build();
        app.run();
    }
