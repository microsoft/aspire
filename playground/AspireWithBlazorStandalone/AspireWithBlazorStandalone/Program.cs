using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using AspireWithBlazorStandalone;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// In WebAssembly, environment variables are injected via JS initializer into MonoConfig.environmentVariables.
// They are available via Environment.GetEnvironmentVariable() but NOT automatically in IConfiguration.
// Service Discovery reads from IConfiguration, so we add environment variables to configuration.
builder.Configuration.AddEnvironmentVariables();

// Add Aspire service defaults (OpenTelemetry, service discovery, resilience)
builder.AddBlazorClientServiceDefaults();

// Default HttpClient for the app
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Named HttpClient for the weather API - uses service discovery to resolve "weatherapi"
builder.Services.AddHttpClient("weatherapi", client =>
{
    // Use service discovery - this will be resolved via configuration
    client.BaseAddress = new Uri("https+http://weatherapi");
});

var host = builder.Build();

// WebAssembly does not support IHostedService, so TelemetryHostedService is never started.
// We must force initialization of MeterProvider and TracerProvider manually.
// See: https://github.com/dotnet/aspire/issues/2816
_ = host.Services.GetService<MeterProvider>();
_ = host.Services.GetService<TracerProvider>();

await host.RunAsync();
