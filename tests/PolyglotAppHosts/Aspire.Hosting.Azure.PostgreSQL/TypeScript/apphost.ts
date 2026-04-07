import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// 1) addAzurePostgresFlexibleServer — main factory method
const pg = await builder.addAzurePostgresFlexibleServer("pg");

// 2) addDatabase — child resource
const db = await pg.addDatabase("mydb", { databaseName: "appdb" });

// 3) withPasswordAuthentication — configures password auth (auto KeyVault)
const pgAuth = await builder.addAzurePostgresFlexibleServer("pg-auth");
await pgAuth.withPasswordAuthentication();

// 4) runAsContainer — run as local PostgreSQL container
const pgContainer = await builder.addAzurePostgresFlexibleServer("pg-container");
await pgContainer.runAsContainer({
    configureContainer: async (container) => {
        // Exercise PostgresServerResource builder methods within the callback
        await container.withLifetime(ContainerLifetime.Persistent);
    },
});

// 5) addDatabase on container-mode server
const dbContainer = await pgContainer.addDatabase("containerdb");

const app = await builder.build();
await app.run();
