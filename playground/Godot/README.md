# Godot playground

This is an internal Aspire playground used to exercise a Godot-shaped executable resource. Public, scenario-driven samples belong in [microsoft/aspire-samples](https://github.com/microsoft/aspire-samples).

The AppHost models a headless Godot process with:

- `AddExecutable`
- a UDP endpoint that flows through `GODOT_SERVER_PORT`
- `WithExplicitStart`, so the repository can build and test without Godot installed

The `godot-server` resource is added only in run mode. Explicit-start resources are launched manually from the dashboard, and publish/deploy has no equivalent for "start this later by hand".

## Manual run

Install Godot 4 on PATH, or set `GODOT_BIN` to the binary path:

```bash
export GODOT_BIN=/usr/local/bin/godot4
aspire run --apphost playground/Godot/Godot.AppHost/Godot.AppHost.csproj
```

Then start the `godot-server` resource from the Aspire dashboard.
