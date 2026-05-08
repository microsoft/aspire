import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        // Basic Go app — go run .
        var api = builder.addGoApp("api", "../go-api");

        // Go app with build tags and linker flags
        var worker = builder.addGoApp("worker", "../go-worker")
            .withBuildTags(new String[] { "netgo", "osusergo" })
            .withLdFlags("-s -w -X main.version=1.0.0");

        // Go app with pre-start lifecycle helpers and debug-friendly compiler flags
        var managed = builder.addGoApp("managed", "../go-managed")
            .withTidy()
            .withVendor()
            .withVet()
            .withRaceDetector()
            .withGcFlags("all=-N -l")
            .withAppArgs(new String[] { "--config", "prod.yaml" });

        // Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
        var debugService = builder.addGoApp("debug-service", "../go-debug-service")
            .withDelveServer(new WithDelveServerOptions().port(2345));

        builder.build().run();
    }
