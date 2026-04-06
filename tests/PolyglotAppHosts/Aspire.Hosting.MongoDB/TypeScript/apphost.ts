// Aspire TypeScript AppHost
// For more information, see: https://aspire.dev

import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

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
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume({ name: "mongo-chained-data" });

// Test 10: Add multiple databases to same server
await mongoChained.addDatabase("app-db");
await mongoChained.addDatabase("analytics-db", { databaseName: "analytics" });

// ---- Property access on MongoDBServerResource ----
const _endpoint = await mongo.primaryEndpoint.get();
const _host = await mongo.host.get();
const _port = await mongo.port.get();
const _uri = await mongo.uriExpression.get();
const _userName = await mongo.userNameReference.get();

// Build and run the app
const _cstr = await mongo.connectionStringExpression.get();
const _databases = mongo.databases;
await builder.build().run();