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
        // Test 12: Initialize a single-member replica set with the resource name and a generated keyfile.
        builder.addMongoDB("mongo-single")
            .withReplicaSet()
            .addDatabase("single-db");
        // Test 13: Initialize a single-member replica set with an explicit set name.
        builder.addMongoDB("mongo-single-named")
            .withReplicaSet("app-rs")
            .addDatabase("single-named-db");
        // Test 14: Supply a keyfile before initialization; TLS options are separate export coverage, not prerequisites.
        var keyFileParam = builder.addParameter("rs-keyfile", new AddParameterOptions().secret(true).value("bW9uZ29kYmtleWZpbGUxMjM0"));
        builder.addMongoDB("mongo-rs-configured")
            .withKeyFile(keyFileParam, "/etc/rs.key")
            .withReplicaSet("configured-rs")
            .withTlsMode()
            .withTlsAllowInvalidCertificates();
        // Test 15: Advanced local multi-member experiments use plain servers, not withReplicaSet single-member sets.
        // NOTE: The members are not given a key file of their own here. withMember gives them the replica set's shared one,
        // and a member carrying a different key file is rejected.
        var mongo1 = builder.addMongoDB("mongo-rs-1");
        var mongo2 = builder.addMongoDB("mongo-rs-2");
        var replicaSet = builder.addMongoDBReplicaSet("rs0")
            .withMember(mongo1)
            .withMember(mongo2);
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
