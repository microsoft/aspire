import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // Test 1: Basic SQL Server resource creation (addSqlServer)
        var sqlServer = builder.addSqlServer("sql");
        // Test 2: Add database to SQL Server (addDatabase)
        sqlServer.addDatabase("mydb");
        // Test 3: Test withDataVolume
        builder.addSqlServer("sql-volume")
            .withDataVolume();
        // Test 4: Test withHostPort
        builder.addSqlServer("sql-port")
            .withHostPort(11433.0);
        // Test 5: Test password parameter with addParameter
        var customPassword = builder.addParameter("sql-password", true);
        builder.addSqlServer("sql-custom-pass", new AddSqlServerOptions().password(customPassword));
        // Test 6: Chained configuration - multiple With* methods
        var sqlChained = builder.addSqlServer("sql-chained");
        sqlChained.withLifetime(ContainerLifetime.PERSISTENT);
        sqlChained.withDataVolume(new WithDataVolumeOptions().name("sql-chained-data"));
        sqlChained.withHostPort(12433.0);
        // Test 7: Add multiple databases to same server
        sqlChained.addDatabase("db1");
        sqlChained.addDatabase("db2", "customdb2");
        // ---- Property access on SqlServerServerResource ----
        var _endpoint = sqlServer.primaryEndpoint();
        var _host = sqlServer.host();
        var _port = sqlServer.port();
        var _uri = sqlServer.uriExpression();
        var _jdbc = sqlServer.jdbcConnectionString();
        var _userName = sqlServer.userNameReference();
        // Build and run the app
        var _cstr = sqlServer.connectionStringExpression();
        var _databases = sqlServer.databases();
        builder.build().run();
    }
