import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const storage = await builder.addAzureStorage("storage");

// VNet with subnet for deployment script (validates #15373 fix)
const vnet = await builder.addAzureVirtualNetwork("vnet");
const aciSubnet = await vnet.addSubnet("aci-subnet", "10.0.2.0/29");

const sqlServer = await builder.addAzureSqlServer("sql");
const db = await sqlServer.addDatabase("mydb");
const db2 = await sqlServer.addDatabase("inventory", { databaseName: "inventorydb" });
await db2.withDefaultAzureSku();
await sqlServer.runAsContainer({ configureContainer: async _ => {} });
await sqlServer.withAdminDeploymentScriptStorage(storage);
await sqlServer.withAdminDeploymentScriptSubnet(aciSubnet);
const _db3 = await sqlServer.addDatabase("analytics").withDefaultAzureSku();

const _hostName = await sqlServer.hostName.get();
const _port = await sqlServer.port.get();
const _uriExpression = await sqlServer.uriExpression.get();
const _connectionStringExpression = await sqlServer.connectionStringExpression.get();
const _jdbcConnectionString = await sqlServer.jdbcConnectionString.get();
const _isContainer: boolean = await sqlServer.isContainer.get();
const _databaseCount = await sqlServer.databases.count();
const _hasMyDb: boolean = await sqlServer.databases.containsKey("mydb");

const _parent = await db.parent.get();
const _dbConnectionStringExpression = await db.connectionStringExpression.get();
const _databaseName = await db.databaseName.get();
const _dbIsContainer: boolean = await db.isContainer.get();
const _dbUriExpression = await db.uriExpression.get();
const _dbJdbcConnectionString = await db.jdbcConnectionString.get();

await builder.build().run();
