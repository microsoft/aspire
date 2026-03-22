import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// Test 1: Basic SQL Server resource creation (addSqlServer)
const sqlServer = await builder.addSqlServer("sql");

// Test 2: Add database to SQL Server (addDatabase)
await sqlServer.addDatabase("mydb");

// Test 3: Test withDataVolume
await builder.addSqlServer("sql-volume")
    .withDataVolume();

// Test 4: Test withHostPort
await builder.addSqlServer("sql-port")
    .withHostPort({ port: 11433 });

// Test 5: Test password parameter with addParameter
const customPassword = await builder.addParameter("sql-password", { secret: true });
await builder.addSqlServer("sql-custom-pass", { password: customPassword });

// Test 6: Chained configuration - multiple With* methods
const sqlChained = await builder.addSqlServer("sql-chained")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume({ name: "sql-chained-data" })
    .withHostPort({ port: 12433 });

// Test 7: Add multiple databases to same server
await sqlChained.addDatabase("db1");
await sqlChained.addDatabase("db2", { databaseName: "customdb2" });

// Build and run the app
await builder.build().run();
