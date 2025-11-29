using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Hosting;

Console.WriteLine("[Aspire.Client] Starting WebAssembly client...");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

Console.WriteLine("[Aspire.Client] WebAssemblyHostBuilder created");

// First, print environment variables directly to verify they're available
Console.WriteLine("[Aspire.Client] === Environment Variables (direct) ===");
foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    var key = entry.Key?.ToString();
    if (key is not null && key.StartsWith("services", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[Aspire.Client] ENV: {key} = {entry.Value}");
    }
}

// In WebAssembly, environment variables are injected via JS initializer into MonoConfig.environmentVariables.
// They are available via Environment.GetEnvironmentVariable() but NOT automatically in IConfiguration.
// Service Discovery reads from IConfiguration, so we add environment variables to configuration.
// The EnvironmentVariables configuration provider converts "services__weatherapi__https__0" 
// to "services:weatherapi:https:0" which matches what ConfigurationServiceEndpointProvider expects.
Console.WriteLine("[Aspire.Client] Adding environment variables to configuration...");
builder.Configuration.AddEnvironmentVariables();

// Trace configuration from the Services section to verify mapping worked
Console.WriteLine("[Aspire.Client] === Configuration (after AddEnvironmentVariables) ===");
var servicesSection = builder.Configuration.GetSection("services");
if (servicesSection.Exists())
{
    Console.WriteLine("[Aspire.Client] Found 'services' section in configuration");
    foreach (var child in servicesSection.GetChildren())
    {
        Console.WriteLine($"[Aspire.Client]   Service: {child.Key}");
        foreach (var endpoint in child.GetChildren())
        {
            Console.WriteLine($"[Aspire.Client]     {endpoint.Key}: {endpoint.Value}");
            foreach (var idx in endpoint.GetChildren())
            {
                Console.WriteLine($"[Aspire.Client]       [{idx.Key}] = {idx.Value}");
            }
        }
    }
}
else
{
    Console.WriteLine("[Aspire.Client] No 'services' section found in configuration");
}

// Also check with capital S (IConfiguration is case-insensitive, but let's verify)
var servicesSectionCapital = builder.Configuration.GetSection("Services");
Console.WriteLine($"[Aspire.Client] 'Services' section exists (capital S): {servicesSectionCapital.Exists()}");

// Log OTEL configuration
Console.WriteLine("[Aspire.Client] === OpenTelemetry Configuration ===");
var otelEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var otelProtocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"];
Console.WriteLine($"[Aspire.Client] OTEL_EXPORTER_OTLP_ENDPOINT: {otelEndpoint ?? "(not set)"}");
Console.WriteLine($"[Aspire.Client] OTEL_EXPORTER_OTLP_PROTOCOL: {otelProtocol ?? "(not set)"}");
Console.WriteLine($"[Aspire.Client] OTEL_SERVICE_NAME: {otelServiceName ?? "(not set)"}");

// Add Aspire service defaults (service discovery, telemetry, resilience)
Console.WriteLine("[Aspire.Client] Adding Blazor client service defaults...");
builder.AddBlazorClientServiceDefaults();
Console.WriteLine("[Aspire.Client] Blazor client service defaults added");

// Add HttpClient for the Weather API - uses service discovery to resolve "weatherapi"
Console.WriteLine("[Aspire.Client] Adding weatherapi HttpClient...");
builder.Services.AddHttpClient("weatherapi", client =>
{
    // Use service discovery - this will be resolved via configuration
    client.BaseAddress = new Uri("https+http://weatherapi");
});
Console.WriteLine("[Aspire.Client] weatherapi HttpClient added");

Console.WriteLine("[Aspire.Client] Building host...");
var host = builder.Build();
Console.WriteLine("[Aspire.Client] Host built, running...");

await host.RunAsync();
Console.WriteLine("[Aspire.Client] Host stopped");
