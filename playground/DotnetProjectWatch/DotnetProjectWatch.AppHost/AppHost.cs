// SPIKE playground for `aspire run --watch` (docs/plans/project-v2-csharpprogram-watch.md).
// The services live under ./services, which is isolated from the repo build (no Arcade, no central package
// management) so they look like an ordinary user solution to the watcher. Run with `aspire run --watch`.
var builder = DistributedApplication.CreateBuilder(args);

// Simulates a Release AppHost: services build with the AppHost's configuration (AppHost:BuildConfiguration).
if (builder.Configuration["WATCHDEMO_BUILD_CONFIGURATION"] is { Length: > 0 } buildConfiguration)
{
    builder.Configuration["AppHost:BuildConfiguration"] = buildConfiguration;
}

var api = builder.AddDotnetProject("api", "../services/WatchDemo.Api")
    .WithEnvironment("GREETING_SOURCE", "apphost")
    .WithExternalHttpEndpoints();

// Opt-in switches for the watch scenarios (environment variables of `aspire run`; `curl :5380/diag` shows the effect).
if (builder.Configuration["WATCHDEMO_API_REPLICAS"] is { Length: > 0 } replicas)
{
    api.WithReplicas(int.Parse(replicas, System.Globalization.CultureInfo.InvariantCulture));
}

if (builder.Configuration["WATCHDEMO_BUILD_MARKER"] is { Length: > 0 } marker)
{
    // Build-only: embedded into the api assembly, never in the api's environment.
    api.WithBuildEnvironment("WATCHDEMO_BUILD_MARKER", marker);
}

if (builder.Configuration["WATCHDEMO_WORKING_DIRECTORY"] is { Length: > 0 } workingDirectory)
{
    api.WithWorkingDirectory(workingDirectory);
}

builder.AddDotnetProject("worker", "../services/WatchDemo.Worker")
    .WithReference(api)
    .WaitFor(api);

// File-based C# apps are watched like projects. WithWatch(false) opts a resource out of hot reload.
builder.AddDotnetProject("ticker", "../services/Scripts/ticker.cs");

builder.Build().Run();
