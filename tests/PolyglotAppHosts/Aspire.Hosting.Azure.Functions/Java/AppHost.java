import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Functions validation
        // Exercises every exported member of Aspire.Hosting.Azure.Functions
        var builder = DistributedApplication.CreateBuilder();
        // ── 1. addAzureFunctionsProject (path-based overload) ───────────────────────
        var funcApp = builder.addAzureFunctionsProject(
            "myfunc",
            "../MyFunctions/MyFunctions.csproj"
        );
        // ── 2. withHostStorage - specify custom Azure Storage for Functions host ────
        var storage = builder.addAzureStorage("funcstorage");
        funcApp.withHostStorage(storage);
        // ── 3. Fluent chaining - verify return types enable chaining ────────────────
        var chainedFunc = builder
            .addAzureFunctionsProject("chained-func", "../OtherFunc/OtherFunc.csproj")
            .withHostStorage(storage);
        chainedFunc.withEnvironment("MY_KEY", "my-value");
        chainedFunc.withHttpEndpoint(new WithHttpEndpointOptions().port(7071.0));
        // ── 4. withReference from base builder - standard resource references ───────
        var anotherStorage = builder.addAzureStorage("appstorage");
        funcApp.withReference(anotherStorage, new WithReferenceOptions());
        builder.build().run();
    }
