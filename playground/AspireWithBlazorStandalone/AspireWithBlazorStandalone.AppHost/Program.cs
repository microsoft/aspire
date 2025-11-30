var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.AspireWithBlazorStandalone_WeatherApi>("weatherapi");

// The standalone WASM project references the Gateway package which:
// - Removes DevServer dependency
// - Adds a JS initializer that fetches config from /_blazor/_configuration
// 
// When running via Aspire, the Gateway will be a separate process that:
// - Serves the WASM static files
// - Exposes /_blazor/_configuration with OTEL and service discovery config
var standaloneWasm = builder.AddProject<Projects.AspireWithBlazorStandalone>("standalonewasm")
    .WithReference(weatherApi);

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
