import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        // Basic Go app — go run .
        var api = builder.addGoApp("api", "../go-api");

        // Go app with build tags and linker flags via AddGoAppOptions
        var worker = builder.addGoApp("worker", "../go-worker",
            new AddGoAppOptions()
                .buildTags(new String[] { "netgo", "osusergo" })
                .ldFlags("-s -w -X main.version=1.0.0"));

        // Go app with pre-start lifecycle helpers and all build options
        var managed = builder.addGoApp("managed", "../go-managed",
                new AddGoAppOptions()
                    .buildTags(new String[] { "integration" })
                    .ldFlags("-s -w")
                    .gcFlags("all=-N -l")
                    .raceDetector(true))
            .withModTidy()
            .withModVendor()
            .withModDownload()
            .withVetTool()
            .withAppArgs(new String[] { "--config", "prod.yaml" });

        // Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
        var debugService = builder.addGoApp("debug-service", "../go-debug-service")
            .withDelveServer();

        builder.build().run();
    }
