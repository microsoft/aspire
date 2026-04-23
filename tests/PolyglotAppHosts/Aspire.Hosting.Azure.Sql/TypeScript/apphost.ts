import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const storage = await builder.addAzureStorage("storage");

// VNet with subnet for deployment script (validates #15373 fix)
const vnet = await builder.addAzureVirtualNetwork("vnet");
const deploymentSubnet = await vnet.addSubnet("deployment-subnet", "10.0.1.0/24");
const aciSubnet = await vnet.addSubnet("aci-subnet", "10.0.2.0/29");

const sqlServer = await builder.addAzureSqlServer("sql");
const db = await sqlServer.addDatabase("mydb");
const db2 = await sqlServer.addDatabase("inventory", { databaseName: "inventorydb" });
await db2.withDefaultAzureSku();
await sqlServer.runAsContainer({ configureContainer: async _ => {} });
await sqlServer.withAdminDeploymentScriptSubnet(deploymentSubnet);
await sqlServer.withAdminDeploymentScriptStorage(storage);
await sqlServer.withAdminDeploymentScriptSubnet(aciSubnet);
const _db3 = await sqlServer.addDatabase("analytics").withDefaultAzureSku();

const _hostName = await sqlServer.hostName();
const _port = await sqlServer.port();
const _uriExpression = await sqlServer.uriExpression();
const _connectionStringExpression = await sqlServer.connectionStringExpression();
const _jdbcConnectionString = await sqlServer.jdbcConnectionString();
const _fullyQualifiedDomainName = await sqlServer.fullyQualifiedDomainName();
const _nameOutputReference = await sqlServer.nameOutputReference();
const _resourceId = await sqlServer.id();
const _isContainer: boolean = await sqlServer.isContainer();
const _databaseCount = await sqlServer.databases.count();
const _hasMyDb: boolean = await sqlServer.databases.containsKey("mydb");
const _azureSqlDatabase = await sqlServer.azureSqlDatabases.get("mydb");

const _parent = await db.parent();
const _dbConnectionStringExpression = await db.connectionStringExpression();
const _databaseName = await db.databaseName();
const _dbIsContainer: boolean = await db.isContainer();
const _dbUriExpression = await db.uriExpression();
const _dbJdbcConnectionString = await db.jdbcConnectionString();

await builder.build().run();
