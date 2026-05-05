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

const _isEmulator: boolean = await kusto.isEmulator();
const _clusterUri = await kusto.uriExpression();
const _clusterConnectionString = await kusto.connectionStringExpression();
const _clusterNameOutput = await kusto.nameOutputReference();
const _clusterUriOutput = await kusto.clusterUri();

const _defaultDatabaseName: string = await defaultDatabase.databaseName();
const _defaultDatabaseParent = await defaultDatabase.parent();
const _defaultDatabaseConnectionString = await defaultDatabase.connectionStringExpression();
const _defaultDatabaseCreationScript = await defaultDatabase.getDatabaseCreationScript();

const _customDatabaseName: string = await customDatabase.databaseName();
const _customDatabaseParent = await customDatabase.parent();
const _customDatabaseConnectionString = await customDatabase.connectionStringExpression();
const _customDatabaseCreationScript = await customDatabase.getDatabaseCreationScript();

await builder.build().run();
