import { createBuilder } from './.modules/aspire.mjs';

const builder = await createBuilder();

const api = await builder.addProject("api", "./src/Api");
const apiMigrations = await builder.addProject("api-migrations-project", "./src/Api.Migrations");

await api.addEFMigrations("api-migrations")
    .withMigrationsProject({ migrationsProject: "./src/Api.Migrations" });
await api.addEFMigrations("api-dbcontext-migrations", { dbContextTypeName: "Api.Data.AppDbContext" })
    .withMigrationsProject({ migrationsProject: apiMigrations })
    .withMigrationNamespace("Api.Data.Migrations");
await api.addEFMigrations("api-script-migrations")
    .publishAsMigrationScript();
await api.addEFMigrations("api-script-options-migrations")
    .publishAsMigrationScript({ idempotent: false, noTransactions: true });
await api.addEFMigrations("api-bundle-migrations")
    .publishAsMigrationBundle();
await api.addEFMigrations("api-bundle-options-migrations")
    .publishAsMigrationBundle({ targetRuntime: "linux-x64", selfContained: true, publishContainer: true, baseImage: "mcr.microsoft.com/dotnet/runtime:10.0" });

await builder.build().run();
