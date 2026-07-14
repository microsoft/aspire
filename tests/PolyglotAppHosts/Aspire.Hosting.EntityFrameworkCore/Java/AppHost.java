import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.addProject("api", "./src/Api");
        var apiMigrations = builder.addProject("api-migrations-project", "./src/Api.Migrations");

        api.addEFMigrations("api-migrations")
            .withMigrationsProject("./src/Api.Migrations");
        api.addEFMigrations("api-dbcontext-migrations", "Api.Data.AppDbContext")
            .withMigrationsProject(apiMigrations)
            .withMigrationNamespace("Api.Data.Migrations");
        api.addEFMigrations("api-script-migrations")
            .publishAsMigrationScript();
        api.addEFMigrations("api-script-options-migrations")
            .publishAsMigrationScript(new PublishAsMigrationScriptOptions()
                .idempotent(false)
                .noTransactions(true));
        api.addEFMigrations("api-bundle-migrations")
            .publishAsMigrationBundle();
        api.addEFMigrations("api-bundle-options-migrations")
            .publishAsMigrationBundle(new PublishAsMigrationBundleOptions()
                .targetRuntime("linux-x64")
                .selfContained(true)
                .publishContainer(true)
                .baseImage("mcr.microsoft.com/dotnet/runtime:10.0"));

        builder.build().run();
    }
