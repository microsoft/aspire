import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var storage = builder.addAzureStorage("storage");
        var sqlServer = builder.addAzureSqlServer("sql");
        var db = sqlServer.addDatabase("mydb");
        var db2 = sqlServer.addDatabase("inventory", "inventorydb");
        db2.withDefaultAzureSku();
        sqlServer.runAsContainer((container) -> { });
        sqlServer.withAdminDeploymentScriptStorage(storage);
        var _db3 = sqlServer.addDatabase("analytics").withDefaultAzureSku();
        var _hostName = sqlServer.hostName();
        var _port = sqlServer.port();
        var _uriExpression = sqlServer.uriExpression();
        var _connectionStringExpression = sqlServer.connectionStringExpression();
        var _jdbcConnectionString = sqlServer.jdbcConnectionString();
        var _isContainer = sqlServer.isContainer();
        var _databases = sqlServer.databases();
        var _parent = db.parent();
        var _dbConnectionStringExpression = db.connectionStringExpression();
        var _databaseName = db.databaseName();
        var _dbIsContainer = db.isContainer();
        var _dbUriExpression = db.uriExpression();
        var _dbJdbcConnectionString = db.jdbcConnectionString();
        builder.build().run();
    }
