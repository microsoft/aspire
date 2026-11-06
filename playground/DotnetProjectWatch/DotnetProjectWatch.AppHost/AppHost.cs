// SPIKE playground for `aspire run --watch` (docs/plans/project-v2-csharpprogram-watch.md).
// The services live under ./services, which is isolated from the repo build (no Arcade, no central package
// management) so they look like an ordinary user solution to the watcher. Run with `aspire run --watch`.
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../services/WatchDemo.Api")
    .WithEnvironment("GREETING_SOURCE", "apphost")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../services/WatchDemo.Worker")
    .WithReference(api)
    .WaitFor(api);

// File-based C# apps can't be hot reloaded yet: without WithWatch(false), `aspire run --watch` fails with an error naming
// this resource. With it, ticker runs as a plain `dotnet run` and the terminal and its log say so.
builder.AddDotnetProject("ticker", "../services/Scripts/ticker.cs")
    .WithWatch(false);

builder.Build().Run();
