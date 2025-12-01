using AspireWithBlazor.Client.Pages;
using AspireWithBlazor.Components;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Add HttpClient for the Weather API
builder.Services.AddHttpClient("weatherapi", client =>
{
    client.BaseAddress = new Uri("https+http://weatherapi");
});

// Configure YARP proxy if enabled
var useProxy = builder.Configuration.GetValue<bool>("Proxy:UseProxy");
if (useProxy)
{
    builder.Services.AddReverseProxy()
        .LoadFromMemory(
            routes: GetProxyRoutes(builder.Configuration),
            clusters: GetProxyClusters(builder.Configuration))
        .AddServiceDiscoveryDestinationResolver();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAntiforgery();

app.MapDefaultEndpoints(useProxy);

// Map YARP reverse proxy if enabled
if (useProxy)
{
    app.MapReverseProxy();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Counter).Assembly);

app.Run();

// Helper methods to build YARP configuration from discovered services
static IReadOnlyList<RouteConfig> GetProxyRoutes(IConfiguration configuration)
{
    var routes = new List<RouteConfig>();
    var servicesSection = configuration.GetSection("services");
    
    if (!servicesSection.Exists())
    {
        return routes;
    }

    foreach (var serviceSection in servicesSection.GetChildren())
    {
        var serviceName = serviceSection.Key;
        
        // Create a route for each service: /_api/{serviceName}/{**catch-all}
        routes.Add(new RouteConfig
        {
            RouteId = $"route-{serviceName}",
            ClusterId = $"cluster-{serviceName}",
            Match = new RouteMatch
            {
                Path = $"/_api/{serviceName}/{{**catch-all}}"
            },
            Transforms = new List<Dictionary<string, string>>
            {
                new() { ["PathRemovePrefix"] = $"/_api/{serviceName}" }
            }
        });
    }

    return routes;
}

static IReadOnlyList<ClusterConfig> GetProxyClusters(IConfiguration configuration)
{
    var clusters = new List<ClusterConfig>();
    var servicesSection = configuration.GetSection("services");
    
    if (!servicesSection.Exists())
    {
        return clusters;
    }

    foreach (var serviceSection in servicesSection.GetChildren())
    {
        var serviceName = serviceSection.Key;
        
        // Use service discovery URL format for the destination
        // YARP will use the configured HttpClient with service discovery to resolve the actual URL
        clusters.Add(new ClusterConfig
        {
            ClusterId = $"cluster-{serviceName}",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["destination1"] = new DestinationConfig
                {
                    Address = $"https+http://{serviceName}"
                }
            }
        });
    }

    return clusters;
}
