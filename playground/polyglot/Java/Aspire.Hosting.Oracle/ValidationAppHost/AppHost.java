import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Oracle Integration Validation
        // Validates all [AspireExport] methods for Aspire.Hosting.Oracle
        var builder = DistributedApplication.CreateBuilder();
        // ---- addOracle: factory method with defaults ----
        var oracle = builder.addOracle("oracledb");
        // ---- addOracle: factory method with custom password and port ----
        var customPassword = builder.addParameter("oracle-password", true);
        var oracle2 = builder.addOracle("oracledb2", new AddOracleOptions().password(customPassword).port(1522.0));
        // ---- addDatabase: child resource with default databaseName ----
        var db = oracle.addDatabase("mydb");
        // ---- addDatabase: child resource with explicit databaseName ----
        var db2 = oracle.addDatabase("inventory", "inventorydb");
        // ---- withDataVolume: data persistence (default name) ----
        oracle.withDataVolume();
        // ---- withDataVolume: data persistence (custom name) ----
        oracle2.withDataVolume("oracle-data");
        // ---- withDataBindMount: bind mount for data ----
        oracle2.withDataBindMount("./oracle-data");
        // ---- withInitFiles: initialization scripts ----
        oracle2.withInitFiles("./init-scripts");
        // ---- withDbSetupBindMount: DB setup directory ----
        oracle2.withDbSetupBindMount("./setup-scripts");
        // ---- withReference: connection string reference (from core) ----
        var otherOracle = builder.addOracle("other-oracle");
        var otherDb = otherOracle.addDatabase("otherdb");
        oracle.withReference(otherDb);
        // ---- withReference: with connection name option ----
        oracle.withReference(otherDb, new WithReferenceOptions().connectionName("secondary-db"));
        // ---- withReference: unified reference to another Oracle server resource ----
        oracle.withReference(otherOracle);
        // ---- Fluent chaining: multiple methods chained ----
        var oracle3 = builder.addOracle("oracledb3");
        oracle3.withLifetime(ContainerLifetime.PERSISTENT);
        oracle3.withDataVolume("oracle3-data");
        oracle3.addDatabase("chaineddb");
        // ---- Property access on OracleDatabaseServerResource ----
        var _endpoint = oracle.primaryEndpoint();
        var _host = oracle.host();
        var _port = oracle.port();
        var _userNameRef = oracle.userNameReference();
        var _uri = oracle.uriExpression();
        var _jdbc = oracle.jdbcConnectionString();
        var _cstr = oracle.connectionStringExpression();
        // ---- Property access on OracleDatabaseResource ----
        var _dbName = db.databaseName();
        var _dbUri = db.uriExpression();
        var _dbJdbc = db.jdbcConnectionString();
        var _dbParent = db.parent();
        var _dbCstr = db.connectionStringExpression();
        builder.build().run();
    }
