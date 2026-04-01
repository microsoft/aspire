// Aspire TypeScript AppHost - PostgreSQL Integration Validation
// Validates all [AspireExport] methods for Aspire.Hosting.PostgreSQL

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// ---- AddPostgres: factory method ----
const postgres = await builder.addPostgres("pg");

// ---- AddDatabase: child resource ----
const db = await postgres.addDatabase("mydb", { databaseName: "testdb" });

// ---- WithPgAdmin: management UI ----
await postgres.withPgAdmin();
await postgres.withPgAdmin({ containerName: "mypgadmin" });

// ---- WithPgWeb: management UI ----
await postgres.withPgWeb();
await postgres.withPgWeb({ containerName: "mypgweb" });

// ---- WithDataVolume: data persistence ----
await postgres.withDataVolume();
await postgres.withDataVolume({ name: "pg-data", isReadOnly: false });

// ---- WithDataBindMount: bind mount ----
await postgres.withDataBindMount("./data");
await postgres.withDataBindMount("./data2", { isReadOnly: true });

// ---- WithInitFiles: initialization scripts ----
await postgres.withInitFiles("./init");

// ---- WithHostPort: explicit port for PostgreSQL ----
await postgres.withHostPort({ port: 5432 });

// ---- WithCreationScript: custom database creation SQL ----
await db.withCreationScript('CREATE DATABASE "testdb"');

// ---- WithPassword / WithUserName: credential configuration ----
const customPassword = await builder.addParameter("pg-password", { secret: true });
const customUser = await builder.addParameter("pg-user");
const pg2 = await builder.addPostgres("pg2");
await pg2.withPassword(customPassword);
await pg2.withUserName(customUser);

// ---- Property access on PostgresServerResource ----
const _endpoint = await postgres.primaryEndpoint.get();
const _nameRef = await postgres.userNameReference.get();
const _uri = await postgres.uriExpression.get();
const _jdbc = await postgres.jdbcConnectionString.get();
const _cstr = await postgres.connectionStringExpression.get();

// ---- Property access on PostgresDatabaseResource ----
const _dbName: string = await db.databaseName.get();
const _dbUri = await db.uriExpression.get();
const _dbJdbc = await db.jdbcConnectionString.get();
const _dbCstr = await db.connectionStringExpression.get();

await builder.build().run();