# SPIKE: `aspire run --watch` for `AddDotnetProject` services

Preview-quality spike of [docs/plans/project-v2-csharpprogram-watch.md](../../docs/plans/project-v2-csharpprogram-watch.md).
Not production code. Shortcuts are marked `TODO(spike)` / `HACK(spike)` in the source.

## How it works

- `aspire run --watch` sets `AppHost__Run__WatchEnabled=true` and `ASPIRE_DOTNET_WATCH_PROXY_PATH=<this CLI>` for the AppHost
  (plus `ASPIRE_DOTNET_WATCH_PROXY_HOST=<dotnet muxer>` when the CLI is a framework-dependent `aspire.dll`).
- In watch runs, `AddDotnetProject` skips Aspire's coordinated build and registers every service with one
  `dotnet-watch` executable resource (`Aspire.Hosting.Dotnet/Watch/DotnetWatchCoordinator.cs`). The watcher's build is the only build. That resource:
  - resolves the SDK the services use and acquires the **matching** `Microsoft.DotNet.HotReload.Watch.Aspire` version
    (the same version as the SDK; SDK 10.x uses 10.0.403) into `~/.aspire/dotnet-watch/<version>`
    (hardcoded dnceng `dotnet-public` flat-container URL, no signature check). Override with `ASPIRE_DOTNET_WATCH_TOOL_VERSION`
    or `ASPIRE_DOTNET_WATCH_TOOL_PATH`.
  - runs `dotnet exec <tool> server --sdk <sdk> --server <pipe> --status-pipe <pipe> --resource <each project>` from a
    per-run directory `/tmp/aspw-<id>`, which is deleted on shutdown.
  - reads the status pipe, becomes healthy after the first successful build, and mirrors
    build/hot-reload/restart/exit events into each service's console log.
- Each service waits for `dotnet-watch`, then DCP launches `aspire __dotnet-watch-proxy --server <pipe> --entrypoint <project> -- <args>`
  instead of `dotnet run`. The proxy reads the service environment from its own process environment
  (names listed in `ASPIRE_DOTNET_WATCH_FORWARD_ENV`, values never put on the command line), sends the
  launch request over the watcher's resource pipe (30 s connect timeout), and forwards stdout/stderr. Stopping or restarting the
  resource kills the proxy, which makes the watcher terminate the app.
- Failure behavior:
  - **Initial build fails:** `dotnet-watch` goes Unhealthy with "initial build failed", and each service logs
    `❌ Initial build failed; this service starts once the build succeeds`. Fix the code and the watcher rebuilds, then the services start.
  - **App crashes (non-zero exit):** the watcher keeps the proxy connected, so the exit code can't flow through the proxy.
    The resource state becomes **Crashed** with the exit code in its properties and console log. The next file change (or Restart) relaunches it.
  - **Not started by `aspire run --watch`** (plain `dotnet run`, or an IDE debug session where `DEBUG_SESSION_PORT` is set):
    a warning is logged once and services run normally without watch.

## Repro

Prereqs: Linux or macOS. The repo's local SDK is installed by restore (`./restore.sh`; the `.dotnet` folder must contain
the SDK from the root `global.json`, plus the .NET 10 runtime that restore also installs). No separate SDK 10 is needed.

```bash
git clone --branch spike/project-v2-csharpprogram-watch https://github.com/microsoft/aspire && cd aspire
./restore.sh
./build.sh --restore --build --projects $PWD/src/Aspire.Cli/Aspire.Cli.csproj
./build.sh --restore --build --projects $PWD/playground/DotnetProjectWatch/DotnetProjectWatch.AppHost/DotnetProjectWatch.AppHost.csproj
cd playground/DotnetProjectWatch/DotnetProjectWatch.AppHost
# aspire.dll targets net11.0, so run it with the repo-local muxer (the system `dotnet` may not have that runtime)
../../../dotnet.sh ../../../artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll run --watch
```

Troubleshooting:
- `NU1900`/`NU1905` (NuGet audit can't reach `api.nuget.org`) when the AppHost builds offline: `export NuGetAudit=false`.
- `./restore.sh` fails on unrelated package downloads: the `.dotnet` SDK and runtimes are usually installed anyway; the two
  `build.sh --restore` commands above restore only what they need.

Then, while it is running (in another terminal; `aspire` = `../../../dotnet.sh ../../../artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll`):

- `curl http://localhost:5380` shows `api v1 | Hello from Shared v1`.
- Edit `services/WatchDemo.Api/Program.cs` (the `MapGet` string): the change is hot reloaded in about 3 s with the same PID.
- Edit `services/WatchDemo.Shared/Greeter.cs`: both api and worker pick it up (the worker logs it every 5 s).
- Add a new `app.MapGet(...)` route (a rude edit): the watcher restarts only the api, with the same Aspire env/endpoint.
- Introduce a compile error: the error shows in the `dotnet-watch` resource log and the old code keeps running; fixing it reloads.
- Add `app.MapGet("/crash", () => { Environment.Exit(3); return ""; });` and `curl :5380/crash`: api shows **Crashed** (exit code 3);
  remove the line and it comes back.
- Dashboard **Restart** on api (or `aspire resource api restart`) gives a fresh process with the latest code.
- Ctrl+C: everything stops and `/tmp/aspw-*` is removed.
- Start with a compile error already present: services wait with "Initial build failed"; fix it and they start.

## Evidence (from the original spike run; the fresh-clone run is in the PR description)

- [Resources](docs/01-resources.png): api, worker and dotnet-watch all Running/Healthy
- [api console](docs/02-api-console.png)
- [worker console](docs/03-worker-console.png): `[dotnet-watch] 🔥 Hot reload applied` and the updated output
- [dotnet-watch console](docs/04-dotnet-watch-console.png): status events and the rude-edit restart
