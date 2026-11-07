import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Functions validation
        // Exercises every exported member of Aspire.Hosting.Azure.Functions
        var builder = DistributedApplication.CreateBuilder();
        var storage = builder.addAzureStorage("funcstorage");

        // ── 1. addDotnetAzureFunction and its typed fluent APIs ─────────────────────
        var functions = builder.addDotnetAzureFunction(
            "dotnetfunc",
            "../MyFunctions/MyFunctions.csproj"
        );
        functions.withHostStorage(storage);
        functions.withBuildEnvironment("BUILD_KEY", "build-value");
        functions.withReplicas(2);
        functions.disableForwardedHeaders();
        functions.withEndpointsInEnvironment(new String[] { "http" });

        // ── 2. Legacy addAzureFunctionsProject compatibility surface ────────────────
        var legacyFuncApp = builder.addAzureFunctionsProject(
            "legacyfunc",
            "../OtherFunc/OtherFunc.csproj"
        );
        legacyFuncApp.withHostStorage(storage);

        builder.build().run();
    }
