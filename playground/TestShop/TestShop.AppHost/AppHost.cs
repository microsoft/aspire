using Aspire.Hosting.Yarp.Transforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIRETERMINAL001 // Terminal APIs are experimental.

var builder = DistributedApplication.CreateBuilder(args);

// Parameters have the same two tags as the resource that uses them, so grouping the dashboard by tags shows them
// together: Frontend/Storefront for the frontend, Backend/API for the catalog service, and Backend/Checkout for the
// basket service.
var storefrontPromotion = builder.AddParameter("storefront-promotion", "Free shipping over $50")
                                .WithTags("Frontend", "Storefront");
var lowStockThreshold = builder.AddParameter("low-stock-threshold", "5")
                         .WithTags("Backend", "API");
var checkoutSigningKey = builder.AddParameter("checkout-signing-key", () => Guid.NewGuid().ToString(), secret: true)
                          .WithTags("Backend", "Checkout");
builder.AddParameter("support-contact-email")
       .WithDescription("The customer support email address displayed by the storefront.")
       .WithTags("Frontend", "Storefront");
builder.AddParameter("storefront-theme", "system")
       .WithDescription("Choose the storefront color theme.")
       .WithTags("Frontend", "Storefront")
       .WithCustomInput(parameter => new()
       {
           Name = parameter.Name,
           InputType = InputType.Choice,
           Label = parameter.Name,
           Description = parameter.Description,
           Options =
           [
               KeyValuePair.Create("system", "System"),
               KeyValuePair.Create("light", "Light"),
               KeyValuePair.Create("dark", "Dark")
           ]
       });
builder.AddParameter("storefront-accent", "blue")
       .WithDescription("Choose a suggested accent or enter a custom value.")
       .WithTags("Frontend", "Storefront")
       .WithCustomInput(parameter => new()
       {
           Name = parameter.Name,
           InputType = InputType.Choice,
           Label = parameter.Name,
           Description = parameter.Description,
           AllowCustomChoice = true,
           Options =
           [
               KeyValuePair.Create("blue", "Blue"),
               KeyValuePair.Create("green", "Green"),
               KeyValuePair.Create("orange", "Orange")
           ]
       });
builder.AddParameter("recommendation-limit", "12")
       .WithDescription("The maximum number of recommendations to display.")
       .WithTags("Backend", "API")
       .WithCustomInput(parameter => new()
       {
           Name = parameter.Name,
           InputType = InputType.Number,
           Label = parameter.Name,
           Description = parameter.Description,
           Placeholder = "Enter a number"
       });
builder.AddParameter("maintenance-mode", "false")
       .WithDescription("Temporarily show the storefront maintenance experience.")
       .WithTags("Frontend", "Storefront")
       .WithCustomInput(parameter => new()
       {
           Name = parameter.Name,
           InputType = InputType.Boolean,
           Label = parameter.Name,
           Description = parameter.Description
       });
builder.AddParameter("storefront-certificate")
       .WithDescription("Upload the certificate used by the storefront.")
       .WithTags("Frontend", "Storefront")
       .WithCustomInput(parameter => new()
       {
           Name = parameter.Name,
           InputType = InputType.File,
           Label = parameter.Name,
           Description = parameter.Description,
           Placeholder = "Choose certificate",
           FileFilter = ".pem,.pfx,.crt"
       });

var catalogDb = builder.AddPostgres("postgres")
                       .WithDataVolume()
                       .WithPgAdmin(resource =>
                       {
                           resource.WithUrlForEndpoint("http", u => u.DisplayText = "PG Admin");
                       })
                       .AddDatabase("catalogdb");

var basketCache = builder.AddRedis("basketcache")
                         .WithDataVolume();

#if !SKIP_DASHBOARD_REFERENCE
basketCache.WithRedisCommander(c =>
            {
                c.WithHostPort(33801);
                c.WithUrlForEndpoint("http", u => u.DisplayText = "Redis Commander");
            })
           .WithRedisInsight(c =>
            {
                c.WithHostPort(33802);
                c.WithUrlForEndpoint("http", u => u.DisplayText = "Redis Insight");
            });
#endif

var catalogDbApp = builder.AddProject<Projects.CatalogDb>("catalogdbapp")
                          .WithReference(catalogDb)
                          .WaitFor(catalogDb)
                          .WithHttpHealthCheck("/health");

if (builder.Environment.IsDevelopment() && builder.ExecutionContext.IsRunMode)
{
    var resetDbKey = Guid.NewGuid().ToString();
    catalogDbApp.WithEnvironment("DatabaseResetKey", resetDbKey)
                .WithHttpCommand("/reset-db", "Reset Database",
                    commandOptions: new()
                    {
                        Description = "Reset the catalog database to its initial state. This will delete and recreate the database.",
                        ConfirmationMessage = "Are you sure you want to reset the catalog database?",
                        IconName = "DatabaseLightning",
                        PrepareRequest = requestContext =>
                        {
                            requestContext.Request.Headers.Add("Authorization", $"Key {resetDbKey}");
                            return Task.CompletedTask;
                        }
                    });
}

var catalogService = builder.AddProject<Projects.CatalogService>("catalogservice")
                            .WithTags("Backend", "API")
                            .WithTrait("Owner", "Finance")
                            .WithTrait("Owner", "Platform")
                            .WithEnvironment("Inventory__LowStockThreshold", lowStockThreshold)
                            .WithDashboardPosition(10)
                            .WithReference(catalogDb)
                            .WaitFor(catalogDb)
                            .WaitFor(catalogDbApp)
                            // Modify the endpoint URL
                            .WithUrlForEndpoint("https", u =>
                            {
                                u.Url = "/";
                                u.DisplayText = "Catalog API";
                            })
                            // Add an endpoint URL
                            .WithUrlForEndpoint("https", _ => new()
                            {
                                Url = "/swagger",
                                DisplayText = "Swagger UI"
                            })
                            // Hide the http URL
                            .WithUrlForEndpoint("http", u => u.DisplayLocation = UrlDisplayLocation.DetailsOnly)
                            .WithHttpHealthCheck("/health")
                            .WithReplicas(2);

var messaging = builder.AddRabbitMQ("messaging")
                       .WithDataVolume()
                       .WithPersistentLifetime()
                       .WithManagementPlugin()
                       .PublishAsContainer();

var basketService = builder.AddProject("basketservice", @"..\BasketService\BasketService.csproj")
                           .WithTags("Backend", "Checkout")
                           .WithEnvironment("Checkout__SigningKey", checkoutSigningKey)
                           .WithDashboardPosition(20)
                           .WithReference(basketCache)
                           .WaitFor(basketCache)
                           .WithReference(messaging)
                           .WaitFor(messaging);

var frontend = builder.AddProject<Projects.MyFrontend>("frontend")
    .WithTags("Frontend", "Storefront")
    .WithEnvironment("Storefront__Promotion", storefrontPromotion)
    .WithDashboardPosition(0)
    .WithExternalHttpEndpoints()
    .WithReference(basketService)
    .WaitFor(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService)
    // Modify the display text of the URLs
    .WithUrls(c => c.Urls.ForEach(u => u.DisplayText = $"Online store ({u.Endpoint?.EndpointName})"))
    // Don't show the non-HTTPS link on the resources page (details only)
    .WithUrlForEndpoint("http", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    // Add health relative URL (show in details only)
    .WithUrlForEndpoint("https", ep => new() { Url = "/health", DisplayText = "Health", DisplayLocation = UrlDisplayLocation.DetailsOnly })
    .WithHttpHealthCheck("/health");

if (OperatingSystem.IsWindows())
{
    builder.AddExecutable("frontend-shell", "cmd.exe", @"..\MyFrontend")
        .WithTags("Frontend", "Development")
        .WithTerminal();

    builder.AddExecutable("catalog-shell", "cmd.exe", @"..\CatalogService")
        .WithTags("Backend", "Development")
        .WithTerminal();
}
else
{
    builder.AddExecutable("frontend-shell", "/bin/bash", @"..\MyFrontend", "-i", "-l")
        .WithTags("Frontend", "Development")
        .WithTerminal();

    builder.AddExecutable("catalog-shell", "/bin/bash", @"..\CatalogService", "-i", "-l")
        .WithTags("Backend", "Development")
        .WithTerminal();
}

builder.AddProject<Projects.OrderProcessor>("orderprocessor", launchProfileName: "OrderProcessor")
    .WithReference(messaging)
    .WaitFor(messaging);

var yarp = builder.AddYarp("apigateway");
#if YARP_USE_CONFIG_FILE
yarp.WithConfigFile("yarp.json")
    .WithReference(basketService)
    .WaitFor(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);
#else
yarp.WithReference(basketService)
    .WaitFor(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);

yarp.WithConfiguration(builder =>
{
    // catalog 
    builder.AddRoute("/catalog/{**catch-all}", catalogService.GetEndpoint("http"))
           .WithTransformPathRemovePrefix("/catalog");
    // basket
    builder.AddRoute("/basket/{**catch-all}", basketService.GetEndpoint("http"))
           .WithTransformPathRemovePrefix("/basket");
});
#endif

builder.AddProject<Projects.ActivityGenerator>("activity-generator")
    .WithReference(frontend)
    .WaitFor(frontend)
    .WithReference(yarp)
    .WaitFor(yarp)
    .WithReference(catalogService)
    .WaitFor(catalogService)
    .WithExplicitStart()
    .ExcludeFromManifest();

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardProjectReference=true` to test the end developer
// dashboard launch experience. The opt-out and project reference are defined in
// playground/Directory.Build.targets. The repo-root Directory.Build.props sets the
// default dashboard binary path to the Aspire.Dashboard output in the artifacts dir.
var dashboardBuilder = builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } dashboardOtlpEndpoint)
{
    // The AppHost normally points every project at its own dashboard. Preserve an explicitly configured
    // external endpoint for dashboard self-telemetry so its activities can be inspected separately.
    dashboardBuilder
        .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", dashboardOtlpEndpoint)
        .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] ?? "grpc")
        .WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"] ?? string.Empty);
}
#endif

builder.Build().Run();
