---
name: run-playground-apphosts
description: Run Aspire repository playground AppHosts using the CLI built from the current worktree. Use for local smoke tests, isolated runs across worktrees, SDK setup, port conflicts, and locked build outputs.
---

# Run playground AppHosts from source

1. Set up the worktree's pinned SDK on the first run; reuse it on repeat runs. Use that worktree's `run-aspire` wrapper to exercise the checkout's source CLI. No full solution build or CLI install is needed for the C# examples below.
2. Pick the exact AppHost file under `playground/`. Use the one the user named; ask if several match. If no specific app is needed for a smoke test, `DotnetProject` is a container-free C# playground. Check `ps` before starting so you don't replace an existing run at the same path.
3. For local-only testing, default to `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` for the launch. Omit it (or set it to `false` if inherited) when testing auth or when the dashboard is not loopback-only. Don't commit this setting or use it on a tunnel or shared network endpoint. `--allow-anonymous` is for `aspire dashboard run`, not AppHost startup.
4. Use `start` for a background run. Use the dashboard URL printed by `start` and `describe` for resource URLs; don't assume the launch profile's ports. Use `wait` for the resource you need (`--status up` if you only need it running), and report the dashboard URL and any failed resources.

## SDK setup and restore

- **First run in a worktree:** From its root, run `.\restore.cmd` (Windows) or `./restore.sh` (Linux/macOS) to set up the pinned SDK and restore the repository.
- **Repeat runs:** Reuse the worktree's SDK and skip the solution-wide restore. The `run-aspire` wrapper builds the source CLI, and `start` builds the selected AppHost unless you pass `--no-build`.
- **If solution restore fails after SDK setup:** For a NuGet failure outside the selected AppHost's dependency graph, try a project-scoped restore from the same worktree: `.\dotnet.cmd restore $app` in PowerShell or `./dotnet.sh restore "$app"` in Bash. Set `$app` to the exact AppHost path as shown below. Continue only if that restore succeeds; this does not repair the solution-wide restore.

## Windows: TestShop dashboard (PowerShell, from the worktree root)

TestShop uses containers, so have a container engine running. Check whether this exact TestShop project is running in this worktree before starting it. Keep the JSON in memory: it may include dashboard login tokens.

```powershell
$app = '.\playground\TestShop\TestShop.AppHost\TestShop.AppHost.csproj'
$appPath = (Resolve-Path -LiteralPath $app -ErrorAction Stop).Path
$output = .\run-aspire.cmd ps --format Json --non-interactive
if ($LASTEXITCODE -ne 0)
{
    throw "Aspire ps failed (exit code $LASTEXITCODE)."
}

# The source wrapper can print build/SDK messages before the JSON array ([...] or []).
$json = [regex]::Match(($output -join "`n"), '(?ms)^\[(?=\s*(?:\{|\])).*\]\s*\z')
if (-not $json.Success)
{
    throw 'Aspire ps did not return a JSON array.'
}
try
{
    $appHosts = ConvertFrom-Json -InputObject $json.Value -ErrorAction Stop
}
catch
{
    throw 'Aspire ps returned invalid JSON.'
}

$running = $appHosts | Where-Object { $_.appHostPath -eq $appPath -and $_.status -eq 'running' }
if ($running)
{
    Write-Output "Running: $appPath"
}
else
{
    Write-Output "Not running: $appPath"
}
```

Continue in the same shell only if the check reports `Not running`. The wrapper records the selected SDK directory in `artifacts\toolset\sdk.txt`, but doesn't update the calling shell's `PATH`. Prepend that directory for `start` so Aspire's SDK check can find it even if no system SDK is installed.

```powershell
$sdkRoot = Get-Content -LiteralPath '.\artifacts\toolset\sdk.txt' -TotalCount 1 -ErrorAction Stop
$previousPath = $env:PATH
$previousAnonymous = $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS
try
{
    $env:PATH = "$sdkRoot;$previousPath"
    $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS = 'true'
    .\run-aspire.cmd start --non-interactive --isolated --apphost $app
    if ($LASTEXITCODE -ne 0)
    {
        throw "Aspire start failed (exit code $LASTEXITCODE)."
    }
}
finally
{
    $env:PATH = $previousPath
    $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS = $previousAnonymous
}
.\run-aspire.cmd wait aspire-dashboard --timeout 90 --non-interactive --apphost $app
if ($LASTEXITCODE -ne 0)
{
    throw "TestShop dashboard did not become healthy (exit code $LASTEXITCODE)."
}
.\run-aspire.cmd describe --non-interactive --apphost $app
# When done with this run:
.\run-aspire.cmd stop --non-interactive --apphost $app
```

If TestShop is already running from this worktree and only the dashboard project changed, follow [Rebuilding the dashboard in a running AppHost](../../../.github/instructions/dashboard.instructions.md#rebuilding-the-dashboard-in-a-running-apphost). Rebuilding just the `aspire-dashboard` project resource keeps TestShop running at the same origin, so you can retest browser preferences without clearing them or starting a second AppHost.

## Linux/macOS (Bash, from the worktree root)

```bash
app=./playground/DotnetProject/DotnetProject.AppHost/DotnetProject.AppHost.csproj
./run-aspire.sh ps --non-interactive
# After confirming this path isn't already running:
ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true ./run-aspire.sh start --non-interactive --isolated --apphost "$app"
./run-aspire.sh wait apiservice --timeout 60 --non-interactive --apphost "$app"
./run-aspire.sh describe --non-interactive --apphost "$app"
# When done with this run:
./run-aspire.sh stop --non-interactive --apphost "$app"
```

Use the exact AppHost path for every app-scoped command. For editor-owned runs, follow the Aspire orchestration skill's editor lifecycle and stop rules. Don't use `dotnet run` directly on the AppHost, `stop --all`, or kill processes by name.

## Troubleshooting

| Situation | What to do |
|-----------|------------|
| Same playground on multiple branches | Use a separate Git worktree and its own build outputs for each branch; don't switch branches underneath a running AppHost. Run each worktree's wrapper there with `--isolated`. It randomizes Aspire-managed ports and isolates .NET user secrets, but doesn't separate bind mounts, explicitly named volumes, or external services. Use separate data for those if needed. |
| Port already in use | `start` can report success even when the dashboard can't bind its port. Check the AppHost log printed by `start`, or `logs <resource> --apphost <path>` for a resource error. Stop your affected run if it's registered, leave the other listener alone, and retry with `--isolated`. Use `--isolated false` only when you need the configured ports (e.g. for a callback). If isolation can't change the conflicting port, choose an available port and update the matching local settings, or coordinate with the owner of the conflicting run. |
| Build output is locked (e.g. `MSB3027`, `MSB3021`, `MSB3491`, `CS2012` with a file-in-use error) | Match the locked path to its checkout and identify the owner with `ps`. Cleanly stop your exact AppHost, then retry the build or rerun `start` without `--no-build`. Don't delete `bin`, `obj`, or `artifacts` to free locks. If another run must stay up, build from a separate worktree. |
| Source changed while the app is running | If only one resource changed, prefer its rebuild command (if exposed) or its watch workflow. For AppHost or shared Aspire source changes, stop your run before rebuilding and starting again. Only use `--no-build` for a deliberately unchanged AppHost already built in that worktree. |
| AppHost starts but a resource doesn't | Use `describe --apphost <path>` and `logs <resource> --apphost <path>`. Check the playground's README and AppHost for required parameters, language runtimes, or a container engine. For a missing SDK, run the worktree's restore script and apply the Windows `PATH` step above when applicable. Use `doctor` for other setup diagnostics. |
