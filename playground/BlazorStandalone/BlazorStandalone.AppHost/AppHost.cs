var builder = DistributedApplication.CreateBuilder(args);

// A named endpoint isn't required. It's added here to test referencing specific endpoints from the Blazor app.
// The gateway can also reference services without named endpoints using scheme-based resolution.
var weatherApi = builder.AddProject<Projects.BlazorStandalone_WeatherApi>("weatherapi");

var timeApi = builder.AddProject<Projects.BlazorStandalone_TimeApi>("timeapi")
    .WithHttpsEndpoint(name: "api");

// Register the standalone Blazor WASM app as a resource.
// The resource name becomes the URL path prefix (e.g., "app" → served at /app/).
// WithReference declares service dependencies that the gateway will proxy via YARP.
// Using a specific named endpoint ensures only that endpoint is forwarded to the gateway.
var blazorApp = builder.AddBlazorWasmProject<Projects.BlazorStandalone>("app")
    .WithReference(weatherApi)
    .WithReference(timeApi.GetEndpoint("api"));

// The Blazor Gateway serves WASM static files and proxies API/OTLP traffic.
// WithBlazorClientApp reads service references from the WASM resource and automatically
// configures YARP routes and service discovery on the gateway.
var gateway = builder.AddBlazorGateway("gateway")
    .WithExternalHttpEndpoints()
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithBlazorClientApp(blazorApp);

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
