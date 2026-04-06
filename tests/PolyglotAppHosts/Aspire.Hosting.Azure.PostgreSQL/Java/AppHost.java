import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // 1) addAzurePostgresFlexibleServer - main factory method
        var pg = builder.addAzurePostgresFlexibleServer("pg");
        // 2) addDatabase - child resource
        var db = pg.addDatabase("mydb", "appdb");
        // 3) withPasswordAuthentication - configures password auth (auto KeyVault)
        var pgAuth = builder.addAzurePostgresFlexibleServer("pg-auth");
        pgAuth.withPasswordAuthentication();
        // 4) runAsContainer - run as local PostgreSQL container
        var pgContainer = builder.addAzurePostgresFlexibleServer("pg-container");
        pgContainer.runAsContainer((container) -> {
                // Exercise PostgresServerResource builder methods within the callback
                container.withLifetime(ContainerLifetime.PERSISTENT);
            });
        // 5) addDatabase on container-mode server
        var dbContainer = pgContainer.addDatabase("containerdb");
        var app = builder.build();
        app.run();
    }
