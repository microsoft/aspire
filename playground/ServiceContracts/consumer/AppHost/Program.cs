using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

// A headless host keeps the unattended experiment independent of the CLI and browser.
// These are ordinary project/container resources; orchestration still runs through DCP.
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DisableDashboard = true,
    EnableResourceLogging = true,
    ProjectDirectory = Directory.GetCurrentDirectory()
});
var region = builder.AddParameter("catalog-region");
var apiKey = builder.AddParameter("catalog-api-key", secret: true);
var catalog = builder.AddServiceContract(
    "catalog", "../contracts/catalog.json", "contoso.catalog", 1,
    new Dictionary<string, IResourceBuilder<ParameterResource>>
    {
        ["region"] = region,
        ["api-key"] = apiKey
    },
    builder.Configuration["CatalogSource"]);

builder.AddProject("storefront", "../Storefront/Storefront.csproj", launchProfileName: null)
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int?>("StorefrontPort"), name: "http")
    .WithReference(catalog.GetEndpoint("http"))
    .WithEnvironment("CATALOG_API_KEY", apiKey)
    .WaitFor(catalog);

builder.Build().Run();
