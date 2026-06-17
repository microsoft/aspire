import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - PostgreSQL Integration Validation
        // Validates all [AspireExport] methods for Aspire.Hosting.PostgreSQL
        var builder = DistributedApplication.CreateBuilder();
        // ---- AddPostgres: factory method ----
        var postgres = builder.addPostgres("pg");
        // ---- AddDatabase: child resource ----
        var db = postgres.addDatabase("mydb", "testdb");
        // ---- WithPgAdmin: management UI ----
        postgres.withPgAdmin();
        postgres.withPgAdmin(new WithPgAdminOptions().containerName("mypgadmin"));
        // ---- WithPgWeb: management UI ----
        postgres.withPgWeb();
        postgres.withPgWeb(new WithPgWebOptions().containerName("mypgweb"));
        // ---- WithDataVolume: data persistence ----
        postgres.withDataVolume();
        postgres.withDataVolume(new WithDataVolumeOptions().name("pg-data").isReadOnly(false));
        // ---- WithDataBindMount: bind mount ----
        postgres.withDataBindMount("./data");
        postgres.withDataBindMount("./data2", true);
        // ---- WithInitFiles: initialization scripts ----
        postgres.withInitFiles("./init");
        // ---- WithHostPort: explicit port for PostgreSQL ----
        postgres.withHostPort(5432.0);
        // ---- WithCreationScript: custom database creation SQL ----
        db.withCreationScript("CREATE DATABASE \"testdb\"");
        // ---- WithPassword / WithUserName: credential configuration ----
        var customPassword = builder.addParameter("pg-password", new AddParameterOptions().secret(true));
        var customUser = builder.addParameter("pg-user");
        var pg2 = builder.addPostgres("pg2");
        pg2.withPassword(customPassword);
        pg2.withUserName(customUser);
        // ---- Property access on PostgresServerResource ----
        var _endpoint = postgres.primaryEndpoint();
        var _nameRef = postgres.userNameReference();
        var _uri = postgres.uriExpression();
        var _jdbc = postgres.jdbcConnectionString();
        var _cstr = postgres.connectionStringExpression();
        // ---- Property access on PostgresDatabaseResource ----
        var _dbName = db.databaseName();
        var _dbUri = db.uriExpression();
        var _dbJdbc = db.jdbcConnectionString();
        var _dbCstr = db.connectionStringExpression();
        builder.build().run();
    }
