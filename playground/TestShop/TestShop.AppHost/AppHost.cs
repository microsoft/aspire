using Aspire.Hosting.Yarp.Transforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddLaunchGroups("catalog", "basket", "frontend", "orders");

var catalogDb = builder.AddPostgres("postgres")
                       .WithLaunchGroup("catalog")
                       .WithDataVolume()
                       .WithPgAdmin(resource =>
                       {
                           resource.WithLaunchGroup("catalog");
                           resource.WithUrlForEndpoint("http", u => u.DisplayText = "PG Admin");
                       })
                       .AddDatabase("catalogdb");

var basketCache = builder.AddRedis("basketcache")
                         .WithLaunchGroup("basket")
                         .WithDataVolume();

#if !SKIP_DASHBOARD_REFERENCE
basketCache.WithRedisCommander(c =>
            {
                c.WithLaunchGroup("basket");
                c.WithHostPort(33801);
                c.WithUrlForEndpoint("http", u => u.DisplayText = "Redis Commander");
            })
           .WithRedisInsight(c =>
            {
                c.WithLaunchGroup("basket");
                c.WithHostPort(33802);
                c.WithUrlForEndpoint("http", u => u.DisplayText = "Redis Insight");
            });
#endif

var catalogDbApp = builder.AddDotnetProject("catalogdbapp", @"..\CatalogDb\CatalogDb.csproj")
                          .WithLaunchGroup("catalog")
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

var catalogService = builder.AddDotnetProject("catalogservice", @"..\CatalogService\CatalogService.csproj")
                            .WithLaunchGroups("catalog", "frontend")
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
                       .WithLaunchGroups("basket", "orders")
                       .WithDataVolume()
                       .WithPersistentLifetime()
                       .WithManagementPlugin()
                       .PublishAsContainer();

var basketService = builder.AddDotnetProject("basketservice", @"..\BasketService\BasketService.csproj")
                           .WithLaunchGroups("basket", "frontend")
                           .WithReference(basketCache)
                           .WaitFor(basketCache)
                           .WithReference(messaging)
                           .WaitFor(messaging);

var frontend = builder.AddDotnetProject("frontend", @"..\MyFrontend\MyFrontend.csproj")
    .WithLaunchGroup("frontend")
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

builder.AddDotnetProject("orderprocessor", @"..\OrderProcessor\OrderProcessor.csproj", options => options.LaunchProfileName = "OrderProcessor")
    .WithLaunchGroup("orders")
    .WithReference(messaging)
    .WaitFor(messaging);

#if YARP_USE_CONFIG_FILE
builder.AddYarp("apigateway")
    .WithLaunchGroup("frontend")
    .WithConfigFile("yarp.json")
    .WithReference(basketService)
    .WaitFor(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);
#else
var yarp = builder.AddYarp("apigateway").WithLaunchGroup("frontend");
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
