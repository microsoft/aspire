// Aspire TypeScript AppHost - Oracle Integration Validation
// Validates all [AspireExport] methods for Aspire.Hosting.Oracle

import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// ---- addOracle: factory method with defaults ----
const oracle = await builder.addOracle("oracledb");

// ---- addOracle: factory method with custom password and port ----
const customPassword = await builder.addParameter("oracle-password", { secret: true });
const oracle2 = await builder.addOracle("oracledb2", { password: customPassword, port: 1522 });

// ---- addDatabase: child resource with default databaseName ----
const db = await oracle.addDatabase("mydb");

// ---- addDatabase: child resource with explicit databaseName ----
const db2 = await oracle.addDatabase("inventory", { databaseName: "inventorydb" });

// ---- withDataVolume: data persistence (default name) ----
await oracle.withDataVolume();

// ---- withDataVolume: data persistence (custom name) ----
await oracle2.withDataVolume({ name: "oracle-data" });

// ---- withDataBindMount: bind mount for data ----
await oracle2.withDataBindMount("./oracle-data");

// ---- withInitFiles: initialization scripts ----
await oracle2.withInitFiles("./init-scripts");

// ---- withDbSetupBindMount: DB setup directory ----
await oracle2.withDbSetupBindMount("./setup-scripts");

// ---- withReference: connection string reference (from core) ----
const otherOracle = await builder.addOracle("other-oracle");
const otherDb = await otherOracle.addDatabase("otherdb");
await oracle.withReference(otherDb);

// ---- withReference: with connection name option ----
await oracle.withReference(otherDb, { connectionName: "secondary-db" });

// ---- withReference: unified reference to another Oracle server resource ----
await oracle.withReference(otherOracle);

// ---- Fluent chaining: multiple methods chained ----
const oracle3 = await builder.addOracle("oracledb3")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume({ name: "oracle3-data" });

await oracle3.addDatabase("chaineddb");

// ---- Property access on OracleDatabaseServerResource ----
const _endpoint = await oracle.primaryEndpoint();
const _host = await oracle.host();
const _port = await oracle.port();
const _userNameRef = await oracle.userNameReference();
const _uri = await oracle.uriExpression();
const _jdbc = await oracle.jdbcConnectionString();
const _cstr = await oracle.connectionStringExpression();

// ---- Property access on OracleDatabaseResource ----
const _dbName: string = await db.databaseName();
const _dbUri = await db.uriExpression();
const _dbJdbc = await db.jdbcConnectionString();
const _dbParent = await db.parent();
const _dbCstr = await db.connectionStringExpression();

await builder.build().run();
