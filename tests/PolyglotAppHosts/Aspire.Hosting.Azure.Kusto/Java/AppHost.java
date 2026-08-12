import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var kusto = builder.addAzureKustoCluster("kusto").runAsEmulator((emulator) -> {
                emulator.withHostPort(8088.0);
            });
        var defaultDatabase = kusto.addReadWriteDatabase("samples");
        var customDatabase = kusto.addReadWriteDatabase("analytics", "AnalyticsDb");
        defaultDatabase.withCreationScript(".create database Samples ifnotexists");
        customDatabase.withCreationScript(".create database AnalyticsDb ifnotexists");
        var _isEmulator = kusto.isEmulator();
        var _clusterUri = kusto.uriExpression();
        var _clusterConnectionString = kusto.connectionStringExpression();
        var _defaultDatabaseName = defaultDatabase.databaseName();
        var _defaultDatabaseParent = defaultDatabase.parent();
        var _defaultDatabaseConnectionString = defaultDatabase.connectionStringExpression();
        var _defaultDatabaseCreationScript = defaultDatabase.getDatabaseCreationScript();
        var _customDatabaseName = customDatabase.databaseName();
        var _customDatabaseParent = customDatabase.parent();
        var _customDatabaseConnectionString = customDatabase.connectionStringExpression();
        var _customDatabaseCreationScript = customDatabase.getDatabaseCreationScript();
        builder.build().run();
    }
