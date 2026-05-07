import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        // Basic Go app — go run .
        var api = builder.addGoApp("api", "../go-api");

        // Go app with build tags and linker flags
        var worker = builder.addGoApp("worker", "../go-worker");
        worker.withBuildTags(new String[] { "netgo", "osusergo" });
        worker.withLdFlags("-s -w -X main.version=1.0.0");

        // Go app with pre-start lifecycle helpers and debug-friendly compiler flags
        var managed = builder.addGoApp("managed", "../go-managed");
        managed.withTidy();
        managed.withVendor();
        managed.withVet();
        managed.withRaceDetector();
        managed.withGcFlags("all=-N -l");
        managed.withAppArgs(new String[] { "--config", "prod.yaml" });

        // Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
        var debugService = builder.addGoApp("debug-service", "../go-debug-service");
        debugService.withDelveServer(new WithDelveServerOptions().port(2345));

        builder.build().run();
    }
