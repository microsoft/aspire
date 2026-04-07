# Full-solution C# AppHosts

Use this reference when `aspire init` created a **full project mode** AppHost because a `.sln` or `.slnx` was discovered.

This is the high-friction path: solution-backed repos often have older bootstrap patterns, SDK pins, existing ServiceDefaults-like code, and build constraints that do not exist in single-file mode.

## What this reference is for

Load this reference when any of the following are true:

- `appHost.path` points to a directory containing `apphost.cs` and a `.csproj`
- a `.sln` or `.slnx` exists near the AppHost
- the repo has a root `global.json`
- selected .NET services still use `Program.cs` + `Startup.cs`, `Host.CreateDefaultBuilder`, `ConfigureWebHostDefaults`, `UseStartup`, or other `IHostBuilder` patterns

## Core rule: solution-backed AppHosts are not single-file AppHosts

Treat these repos as **solution-aware C# init**, not as generic AppHost setup.

- The AppHost may need project references
- The AppHost may need its own SDK boundary
- The solution may or may not be able to own the AppHost safely
- ServiceDefaults changes may require bootstrap modernization

Do not apply single-file assumptions here.

## Mixed SDK repos

Some repos pin the root `global.json` to an older SDK such as .NET 8. A `.csproj`-based Aspire AppHost should still stay on the current Aspire-supported SDK (for example, .NET 10), while existing service projects can remain on `net8.0`.

**Do not downgrade the AppHost project to match the repo's root SDK pin.**

Preferred approach:

1. Keep the repo root `global.json` unchanged.
2. Keep the AppHost in its own directory.
3. Add a **nested `global.json` next to the AppHost** that pins the newer SDK.
4. Leave existing services targeting their current TFM unless the user explicitly asks to migrate them.

Example nested `global.json` beside the AppHost:

```json
{
  "sdk": {
    "version": "10.0.100"
  }
}
```

### Important solution caveat

If the repo's normal root build runs under SDK 8, do **not** assume it can safely own a `net10.0` AppHost project.

When that's likely to break the repo's normal build:

- tell the user explicitly
- prefer keeping the AppHost isolated in its own folder
- only add it to the root solution if the user wants that tradeoff

## Solution membership

A discovered solution means the AppHost was created in project mode, but that does **not** always mean every new project should be added to the root solution automatically.

Use this decision order:

1. If the root solution already includes the services being modeled and is the normal local entry point, prefer adding the AppHost and ServiceDefaults there.
2. If the root solution is tightly coupled to an older SDK/toolchain and adding a `net10.0` AppHost is likely to break routine builds, keep the AppHost outside the solution or in a safer sibling solution boundary.
3. If you're unsure, ask instead of guessing.

## ServiceDefaults in solution-backed repos

Before creating or wiring ServiceDefaults:

1. Look for an existing ServiceDefaults project or equivalent shared bootstrap code.
2. Check whether selected services already have tracing, health checks, or service discovery setup.
3. Check whether the service bootstrap is modern enough for `AddServiceDefaults()` and `MapDefaultEndpoints()`.

If a ServiceDefaults project already exists, reuse it instead of creating another one.

## Legacy bootstrap detection: `IHostBuilder` vs `IHostApplicationBuilder`

This is the easy-to-forget gotcha.

The generated ServiceDefaults extensions typically target **`IHostApplicationBuilder`** and **`WebApplication`** patterns:

```csharp
builder.AddServiceDefaults();
app.MapDefaultEndpoints();
```

That drops cleanly into modern code such as:

- `var builder = WebApplication.CreateBuilder(args);`
- `var builder = Host.CreateApplicationBuilder(args);`

It does **not** automatically map onto older patterns such as:

- `Host.CreateDefaultBuilder(args)`
- `ConfigureWebHostDefaults(...)`
- `UseStartup<Startup>()`
- `IHostBuilder`-only worker/bootstrap code

### What to do when you find legacy hosting

Do **not** silently jam ServiceDefaults into the old shape.

Present the user with the decision:

1. **Keep the service code unchanged for now**
   - model the service in the AppHost
   - skip ServiceDefaults injection for that project
   - use AppHost-side environment wiring only
   - note that full Aspire service-defaults behavior is deferred

2. **Modernize the bootstrap**
   - convert the service to `WebApplication.CreateBuilder(args)` or `Host.CreateApplicationBuilder(args)`
   - then add `builder.AddServiceDefaults()`
   - for ASP.NET Core apps, add `app.MapDefaultEndpoints()` before `app.Run()`

If the repo is conservative or large, default to **asking**, not migrating automatically.

## Modernization guidance

### ASP.NET Core app using `IHostBuilder` / `Startup`

If the user wants full ServiceDefaults support, migrate toward a `WebApplicationBuilder` shape.

Target pattern:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();
app.MapDefaultEndpoints();

app.Run();
```

Preserve existing service registrations and middleware ordering carefully. Move only what is required to land on a `WebApplicationBuilder`/`WebApplication` pipeline.

### Worker/background service using `IHostBuilder`

If the service is a worker and the user wants ServiceDefaults, migrate toward `Host.CreateApplicationBuilder(args)`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
```

For non-web workers, `MapDefaultEndpoints()` usually does not apply unless the app exposes HTTP endpoints.

## AppHost project references

For full project mode, prefer explicit project references from the AppHost to selected .NET services:

```bash
dotnet add <AppHost.csproj> reference <Api.csproj>
```

This keeps solution-backed AppHosts easier to navigate and build.

## Validation checklist for full-solution mode

Before declaring success:

1. The AppHost project builds under its intended SDK boundary.
2. The root solution still behaves the way the user expects, or the user has explicitly accepted any tradeoff.
3. Any ServiceDefaults changes compile in the selected services.
4. `aspire start` works from the AppHost context, and long-lived app resources are healthy rather than merely `Finished`.
5. Legacy `IHostBuilder` services were either modernized intentionally or explicitly left unchanged.

## When to ask the user instead of deciding

Ask when:

- adding the AppHost to the root solution might break the repo's normal SDK/build
- a service uses `Startup.cs` / `IHostBuilder` and would need real bootstrap surgery
- there are multiple plausible ServiceDefaults/shared-bootstrap projects to reuse
- the repo has mixed solution boundaries and it's unclear which one is the real developer entry point
