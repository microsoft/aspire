// Aspire TypeScript AppHost
// For more information, see: https://aspire.dev

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// Test 1: Basic MongoDB resource creation (addMongoDB)
const mongo = await builder.addMongoDB("mongo");

// Test 2: Add database to MongoDB (addDatabase)
await mongo.addDatabase("mydb");

// Test 3: Add database with custom database name
await mongo.addDatabase("db2", { databaseName: "customdb2" });

// Test 4: Test withDataVolume
await builder.addMongoDB("mongo-volume")
    .withDataVolume();

// Test 5: Test withDataVolume with custom name
await builder.addMongoDB("mongo-volume-named")
    .withDataVolume({ name: "mongo-data" });

// Test 6: Test withHostPort on MongoExpress
await builder.addMongoDB("mongo-express")
    .withMongoExpress({
        configureContainer: async (container) => {
            await container.withHostPort({ port: 8082 });
        }
    });

// Test 7: Test withMongoExpress with container name
await builder.addMongoDB("mongo-express-named")
    .withMongoExpress({ containerName: "my-mongo-express" });

// Test 8: Custom password parameter with addParameter
const customPassword = await builder.addParameter("mongo-password", { secret: true });
await builder.addMongoDB("mongo-custom-pass", { password: customPassword });

// Test 9: Chained configuration - multiple With* methods
const mongoChained = await builder.addMongoDB("mongo-chained")
    .withPersistentLifetime()
    .withDataVolume({ name: "mongo-chained-data" });

// Test 10: Add multiple databases to same server
await mongoChained.addDatabase("app-db");
await mongoChained.addDatabase("analytics-db", { databaseName: "analytics" });

// Test 11: Test withBindIpAll
await builder.addMongoDB("mongo-bind-all")
    .withBindIpAll();

// Test 12: Initialize a single-member replica set with the resource name and a generated keyfile.
await builder.addMongoDB("mongo-single")
    .withReplicaSet()
    .addDatabase("single-db");

// Test 13: Initialize a single-member replica set with an explicit set name.
await builder.addMongoDB("mongo-single-named")
    .withReplicaSet({ name: "app-rs" })
    .addDatabase("single-named-db");

// Test 14: Supply a keyfile before initialization; TLS options are separate export coverage, not prerequisites.
const keyFileParam = await builder.addParameter("rs-keyfile", { secret: true, value: "bW9uZ29kYmtleWZpbGUxMjM0" });
await builder.addMongoDB("mongo-rs-configured")
    .withKeyFile(keyFileParam, { keyFilePath: "/etc/rs.key" })
    .withReplicaSet({ name: "configured-rs" })
    .withTlsMode()
    .withTlsAllowInvalidCertificates();

// Test 15: Advanced local multi-member experiments use plain servers, not withReplicaSet single-member sets.
// NOTE: The members are not given a key file of their own here. withMember gives them the replica set's shared one,
// and a member carrying a different key file is rejected.
const mongo1 = await builder.addMongoDB("mongo-rs-1");

const mongo2 = await builder.addMongoDB("mongo-rs-2");

const replicaSet = await builder.addMongoDBReplicaSet("rs0")
    .withMember(mongo1)
    .withMember(mongo2);

// ---- Property access on MongoDBServerResource ----
const _endpoint = await mongo.primaryEndpoint();
const _host = await mongo.host();
const _port = await mongo.port();
const _uri = await mongo.uriExpression();
const _userName = await mongo.userNameReference();

// Build and run the app
const _cstr = await mongo.connectionStringExpression();
const _databases = mongo.databases;
await builder.build().run();
