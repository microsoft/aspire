# Godot Playground (Internal)

> **This is an internal Aspire playground, not a public sample.**
> Public samples belong in [microsoft/aspire-samples](https://github.com/microsoft/aspire-samples).
> This playground exists to validate Aspire's `AddExecutable` and `WithExplicitStart` integration paths.

## Overview

This playground demonstrates hosting a Godot 4 dedicated game server as an Aspire executable resource alongside a .NET matchmaker API. The game server is registered with `.WithExplicitStart()` so the AppHost starts normally on machines without Godot installed.

## Build and CI

**The repository build and CI does not require Godot.** The `Godot.AppHost` and `Godot.Matchmaker` projects are plain .NET projects that build with `dotnet build` like any other playground project. The `GameServer/` directory contains only GDScript and a Godot project file, neither of which participates in the .NET build.

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

2. **Start the AppHost:**

   ```bash
   dotnet run --project playground/Godot/Godot.AppHost
   ```

3. **Start the `godot-server` resource** from the Aspire dashboard (it is marked explicit-start and will not launch automatically). The Aspire dashboard URL is printed to the console on AppHost startup.

## Resources

| Resource | Type | Notes |
|---|---|---|
| `matchmaker` | .NET project | Minimal HTTP API; `/health` and `/servers` |
| `godot-server` | Executable | Headless Godot server; **explicit-start**; listens on UDP |

## Environment Variables

| Variable | Description |
|---|---|
| `GODOT_BIN` | Path to the Godot 4 binary. Defaults to `godot` (Linux/macOS) or `godot.exe` (Windows). |
| `GODOT_SERVER_PORT` | UDP port the Godot server listens on. Injected by Aspire; defaults to `7000`. |
