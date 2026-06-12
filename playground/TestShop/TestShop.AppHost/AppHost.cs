using Aspire.Hosting.Yarp.Transforms;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIREINTERACTION001 // Interaction APIs are experimental.

var builder = DistributedApplication.CreateBuilder(args);

EndpointReference? pgAdminEndpoint = null;

var catalogDb = builder.AddPostgres("postgres")
                       .WithDataVolume()
                       .WithPgAdmin(resource =>
                       {
                           resource.WithUrlForEndpoint("http", u => u.DisplayText = "PG Admin");
                           // Allow pgAdmin to be embedded in an iframe by disabling X-Frame-Options,
                           // Content-Security-Policy frame-ancestors, and CSRF origin checks.
                           // These config values are Python expressions evaluated by pgAdmin.
                           resource.WithEnvironment("PGADMIN_CONFIG_X_FRAME_OPTIONS", "\"\"");
                           resource.WithEnvironment("PGADMIN_CONFIG_CONTENT_SECURITY_POLICY", "\"default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:;\"");
                           resource.WithEnvironment("PGADMIN_CONFIG_WTF_CSRF_ENABLED", "False");
                           pgAdminEndpoint = resource.GetEndpoint("http");
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
                           .WithReference(basketCache)
                           .WaitFor(basketCache)
                           .WithReference(messaging)
                           .WaitFor(messaging);

var frontend = builder.AddProject<Projects.MyFrontend>("frontend")
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

// Register dashboard pages that embed apps in IFrames.
var frontendEndpoint = frontend.GetEndpoint("https");
new IFrameInteractionPages(frontendEndpoint, pgAdminEndpoint).Register(builder);

new RoguelikeInteraction().Register(builder);

builder.AddProject<Projects.OrderProcessor>("orderprocessor", launchProfileName: "OrderProcessor")
    .WithReference(messaging)
    .WaitFor(messaging);

#if YARP_USE_CONFIG_FILE
builder.AddYarp("apigateway")
    .WithConfigFile("yarp.json")
    .WithReference(basketService)
    .WaitFor(basketService)
    .WithReference(catalogService)
    .WaitFor(catalogService);
#else
var yarp = builder.AddYarp("apigateway");
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
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
