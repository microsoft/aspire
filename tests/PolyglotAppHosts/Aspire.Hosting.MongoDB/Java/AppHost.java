import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost
        // For more information, see: https://aspire.dev
        var builder = DistributedApplication.CreateBuilder();
        // Test 1: Basic MongoDB resource creation (addMongoDB)
        var mongo = builder.addMongoDB("mongo");
        // Test 2: Add database to MongoDB (addDatabase)
        mongo.addDatabase("mydb");
        // Test 3: Add database with custom database name
        mongo.addDatabase("db2", "customdb2");
        // Test 4: Test withDataVolume
        builder.addMongoDB("mongo-volume")
            .withDataVolume();
        // Test 5: Test withDataVolume with custom name
        builder.addMongoDB("mongo-volume-named")
            .withDataVolume(new WithDataVolumeOptions().name("mongo-data"));
        // Test 6: Test withHostPort on MongoExpress
        builder.addMongoDB("mongo-express")
            .withMongoExpress(new WithMongoExpressOptions().configureContainer((container) -> {
                    container.withHostPort(8082.0);
                }));
        // Test 7: Test withMongoExpress with container name
        builder.addMongoDB("mongo-express-named")
            .withMongoExpress(new WithMongoExpressOptions().containerName("my-mongo-express"));
        // Test 8: Custom password parameter with addParameter
        var customPassword = builder.addParameter("mongo-password", new AddParameterOptions().secret(true));
        builder.addMongoDB("mongo-custom-pass", new AddMongoDBOptions().password(customPassword));
        // Test 9: Chained configuration - multiple With* methods
        var mongoChained = builder.addMongoDB("mongo-chained");
        mongoChained.withPersistentLifetime();
        mongoChained.withDataVolume(new WithDataVolumeOptions().name("mongo-chained-data"));
        // Test 10: Add multiple databases to same server
        mongoChained.addDatabase("app-db");
        mongoChained.addDatabase("analytics-db", "analytics");
        // Test 11: Test withBindIpAll
        builder.addMongoDB("mongo-bind-all")
            .withBindIpAll();
        // Test 12: Test withReplicaSet
        var mongoRs = builder.addMongoDB("mongo-rs")
            .withReplicaSet("rs0");
        // Test 13: Test withTls with default mode
        builder.addMongoDB("mongo-tls")
            .withTls();
        // Test 14: Test withTls with specific mode
        builder.addMongoDB("mongo-tls-allow")
            .withTls(new WithTlsOptions().mode("allowTls"));
        // Test 15: Test withKeyFile for replica set member
        var keyFileParam = builder.addParameter("rs-keyfile", new AddParameterOptions().secret(true).value("my-secret-key"));
        builder.addMongoDB("mongo-rs-secured")
            .withReplicaSet("rs-secure")
            .withKeyFile(keyFileParam, "/etc/rs.key");
        // Test 16: Complete replica set with security - TLS + KeyFile + ReplicaSet
        var tlsKeyFileParam = builder.addParameter("rs-tls-key", new AddParameterOptions().secret(true).value("tls-secret"));
        builder.addMongoDB("mongo-rs-full")
            .withReplicaSet("rs-full")
            .withKeyFile(tlsKeyFileParam, "/etc/rs.key")
            .withTls(new WithTlsOptions().mode("requireTls"));
        // ---- Property access on MongoDBServerResource ----
        var _endpoint = mongo.primaryEndpoint();
        var _host = mongo.host();
        var _port = mongo.port();
        var _uri = mongo.uriExpression();
        var _userName = mongo.userNameReference();
        // Build and run the app
        var _cstr = mongo.connectionStringExpression();
        var _databases = mongo.databases();
        builder.build().run();
    }
