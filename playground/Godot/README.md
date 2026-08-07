# Godot Playground (Internal)

> **This is an internal Aspire playground, not a public sample.**
> Public samples belong in [microsoft/aspire-samples](https://github.com/microsoft/aspire-samples).
> This playground exists to validate Aspire's `AddExecutable` and `WithExplicitStart` integration paths.

## Overview

This playground demonstrates hosting a Godot 4 dedicated game server as an Aspire executable resource alongside a .NET matchmaker API. The game server is registered with `.WithExplicitStart()` so the AppHost starts normally on machines without Godot installed.

## Build and CI

**The repository build and CI do not require Godot.** The `Godot.AppHost` and `Godot.Matchmaker` projects are plain .NET projects that build with `dotnet build` like any other playground project. The `GameServer/` directory contains only GDScript and a Godot project file, neither of which participates in the .NET build.

## Run mode only

The `godot-server` resource is added **only in run mode** (`builder.ExecutionContext.IsRunMode`). `WithExplicitStart()` means "do not launch this until a user starts it from the dashboard", which has no meaning during publish or deploy. Emitting the executable into a published manifest would produce a resource nothing can start, plus a matchmaker service-discovery binding to a port that is never allocated. In publish mode, `godot-server` is therefore omitted and the matchmaker has no `godot-server` reference. Local in-repo builds can still include the dashboard project resource; CI, out-of-repo builds, and `/p:SkipDashboardProjectReference=true` omit it.

## Manual Run

Running the AppHost with a live Godot server requires:

1. **Godot 4 on PATH** — or set `GODOT_BIN` to the full path of your Godot binary, e.g.:

   ```bash
   export GODOT_BIN=/usr/local/bin/godot4
   ```

   On Windows:

   ```powershell
   $env:GODOT_BIN = "C:\Godot\Godot_v4.3-stable_win64.exe"
   ```

2. **Start the AppHost** from the repository root:

   ```bash
   aspire run --apphost playground/Godot/Godot.AppHost/Godot.AppHost.csproj
   ```

3. **Start the `godot-server` resource** from the Aspire dashboard (it is marked explicit-start and will not launch automatically). The Aspire dashboard URL is printed to the console on AppHost startup.

## Resources

| Resource | Type | Notes |
|---|---|---|
| `matchmaker` | .NET project | Minimal HTTP API; `/health` and `/configuration` |
| `godot-server` | Executable | Headless Godot server; **explicit-start**; listens on UDP; **run mode only** |

## The `/configuration` route

`GET /configuration` reports the game server's **configured** endpoint as the matchmaker received it through Aspire service discovery:

```json
{
  "resourceName": "godot-server",
  "endpointConfigured": true,
  "configuredPort": 23021,
  "configuredEndpoint": "udp://localhost:23021",
  "note": "Configured endpoint only. The godot-server resource is explicit-start, so this port may not be listening."
}
```

This route is deliberately **not** called `/servers`. Aspire allocates the endpoint's port when the application model is built, but `godot-server` is explicit-start, so in practice the port is allocated while nothing is listening on it. An allocated port is not a live server. A real matchmaker would need genuine registration or a readiness probe before advertising a server to players; this playground only demonstrates that the endpoint reaches the matchmaker as configuration.

Because `godot-server` only exists in run mode, the matchmaker can also run with no endpoint configured at all — when published, or when the project is started directly with `dotnet run`. The response then reports the absence rather than describing a port that was never allocated:

```json
{
  "resourceName": "godot-server",
  "endpointConfigured": false,
  "configuredPort": null,
  "configuredEndpoint": null,
  "note": "No godot-server endpoint is configured. The resource is available only in AppHost run mode."
}
```

## Environment Variables

| Variable | Description |
|---|---|
| `GODOT_BIN` | Path to the Godot 4 binary. Defaults to `godot` (Linux/macOS) or `godot.exe` (Windows). |
| `GODOT_SERVER_PORT` | UDP port the Godot server listens on. Injected by Aspire when the resource starts; the script defaults to `7000` when the variable is absent. |
