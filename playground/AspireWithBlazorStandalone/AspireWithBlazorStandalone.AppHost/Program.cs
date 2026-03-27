var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.AspireWithBlazorStandalone_WeatherApi>("weatherapi");

// Register the standalone Blazor WASM app as a resource.
// The resource name becomes the URL path prefix (e.g., "app" → served at /app/).
// WithReference declares service dependencies that the gateway will proxy via YARP.
var blazorApp = builder.AddBlazorWasmProject<Projects.AspireWithBlazorStandalone>("app")
    .WithReference(weatherApi);

// The Blazor Gateway serves WASM static files and proxies API/OTLP traffic.
// WithClient reads service references from the WASM resource and automatically
// configures YARP routes and service discovery on the gateway.
var gateway = builder.AddBlazorGateway("gateway")
    .WithExternalHttpEndpoints()
    .WithOtlpExporter(Aspire.Hosting.OtlpProtocol.HttpProtobuf)
    .WithClient(blazorApp);

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
