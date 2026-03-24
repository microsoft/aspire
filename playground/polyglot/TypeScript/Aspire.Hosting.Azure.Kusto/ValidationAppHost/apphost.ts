import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const kusto = await builder.addAzureKustoCluster("kusto").runAsEmulator({
    configureContainer: async (emulator) => {
        await emulator.withHostPort(8088);
    }
});

const defaultDatabase = await kusto.addReadWriteDatabase("samples");
const customDatabase = await kusto.addReadWriteDatabase("analytics", { databaseName: "AnalyticsDb" });

await defaultDatabase.withCreationScript(".create database Samples ifnotexists");
await customDatabase.withCreationScript(".create database AnalyticsDb ifnotexists");

const _isEmulator: boolean = await kusto.isEmulator.get();
const _clusterUri = await kusto.uriExpression.get();
const _clusterConnectionString = await kusto.connectionStringExpression.get();
const _clusterNameOutput = await kusto.nameOutputReference.get();
const _clusterUriOutput = await kusto.clusterUri.get();

const _defaultDatabaseName: string = await defaultDatabase.databaseName.get();
const _defaultDatabaseParent = await defaultDatabase.parent.get();
const _defaultDatabaseConnectionString = await defaultDatabase.connectionStringExpression.get();
const _defaultDatabaseCreationScript = await defaultDatabase.getDatabaseCreationScript();

const _customDatabaseName: string = await customDatabase.databaseName.get();
const _customDatabaseParent = await customDatabase.parent.get();
const _customDatabaseConnectionString = await customDatabase.connectionStringExpression.get();
const _customDatabaseCreationScript = await customDatabase.getDatabaseCreationScript();

await builder.build().run();
