// SPIKE playground for `aspire run --watch` (docs/plans/project-v2-csharpprogram-watch.md).
// The services live under ./services, which is isolated from the repo build (own global.json pinned to SDK 10,
// no Arcade, no central package management) so the pinned net10.0 watcher can evaluate them.
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../services/WatchDemo.Api")
    .WithEnvironment("GREETING_SOURCE", "apphost")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../services/WatchDemo.Worker")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
