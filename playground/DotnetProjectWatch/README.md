# SPIKE: `aspire run --watch` for `AddDotnetProject` services

Preview-quality spike of [docs/plans/project-v2-csharpprogram-watch.md](../../docs/plans/project-v2-csharpprogram-watch.md).
Not production code. Shortcuts are marked `TODO(spike)` / `HACK(spike)` in the source.

## How it works

- `aspire run --watch` sets `AppHost__Run__WatchEnabled=true` and `ASPIRE_DOTNET_WATCH_PROXY_PATH=<this CLI>` for the AppHost.
- In watch runs, `AddDotnetProject` registers every service with one hidden-ish `dotnet-watch` executable resource
  (`Aspire.Hosting.Dotnet/Watch/DotnetWatchCoordinator.cs`). That resource:
  - waits for Aspire's coordinated initial build,
  - acquires the pinned `Microsoft.DotNet.HotReload.Watch.Aspire` 10.0.403 into `~/.aspire/dotnet-watch/10.0.403`
    (hardcoded dotnet-public URL, no signature check),
  - runs `dotnet exec <tool> server --sdk <global.json SDK> --server <pipe> --status-pipe <pipe> --resource <each project>`,
  - reads the status pipe, becomes healthy after the first successful `build_complete`, and mirrors
    build/hot-reload/restart events into each service's console log.
- Each service waits for `dotnet-watch`, then DCP launches `aspire __dotnet-watch-proxy --server <pipe> --entrypoint <project> -- <args>`
  instead of `dotnet run`. The proxy reads the service environment from its own process environment
  (names listed in `ASPIRE_DOTNET_WATCH_FORWARD_ENV`, values never put on the command line), sends the
  launch request over the watcher's resource pipe, and forwards stdout/stderr. Stop/restart of the
  resource kills the proxy, which makes the watcher terminate the app.

## Repro

Prereqs: repo restored (`./restore.sh`); the .NET **10.0.1xx SDK** (10.0.403 tested) and the 10.0 runtime +
ASP.NET Core runtime installed where the repo's `.dotnet` muxer can see them. The services here pin SDK 10 via
`services/global.json`, because the pinned watcher is `net10.0` and can't load SDK 11 MSBuild.

```bash
./build.sh --build --projects $PWD/src/Aspire.Cli/Aspire.Cli.csproj
./build.sh --build --projects $PWD/playground/DotnetProjectWatch/DotnetProjectWatch.AppHost/DotnetProjectWatch.AppHost.csproj
cd playground/DotnetProjectWatch/DotnetProjectWatch.AppHost
dotnet ../../../artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll run --watch
```

Then, while it is running:

- `curl http://localhost:5380` shows `api v1 | Hello from Shared v1`.
- Edit `services/WatchDemo.Api/Program.cs` (the `MapGet` string): the change is hot reloaded in about 3 s with no restart.
- Edit `services/WatchDemo.Shared/Greeter.cs`: both api and worker pick it up (the worker logs it every 5 s).
- Add a new `app.MapGet(...)` route (a rude edit): the watcher restarts only the api, with the same Aspire env/endpoint.
- Introduce a compile error: the error shows in the `dotnet-watch` resource log and the old code keeps running; fixing it reloads.
- `aspire resource api restart` gives a fresh process with the latest code.
