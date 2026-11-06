# SPIKE: `aspire run --watch` for `AddDotnetProject` services

Preview-quality spike of [docs/plans/project-v2-csharpprogram-watch.md](../../docs/plans/project-v2-csharpprogram-watch.md).
Not production code. Shortcuts are marked `TODO(spike)` / `HACK(spike)` in the source.

## How it works

- `aspire run --watch` sets `AppHost__Run__WatchEnabled=true` and `ASPIRE_DOTNET_WATCH_PROXY_PATH=<this CLI>` for the AppHost
  (plus `ASPIRE_DOTNET_WATCH_PROXY_HOST=<dotnet muxer>` when the CLI is a framework-dependent `aspire.dll`).
- In watch runs, `AddDotnetProject` skips Aspire's coordinated build and registers every service with one
  `dotnet-watch` executable resource (`Aspire.Hosting.Dotnet/Watch/DotnetWatchCoordinator.cs`). The watcher's build is the only build. That resource:
  - resolves the SDK the services use (their `global.json`) and runs the `Microsoft.DotNet.HotReload.Watch.Aspire` 10.0.403
    server, so **watch mode supports .NET 10 SDKs only**. Nothing is downloaded at run time: like DCP, the tool is
    `PackageDownload`ed when `Aspire.Hosting.Dotnet` builds, packed into its nupkg under `tools/dotnet-watch/`, and
    `buildTransitive/Aspire.Hosting.Dotnet.targets` stamps that directory on the AppHost as `aspiredotnetwatchtoolsdir`
    assembly metadata (in-repo AppHosts use `artifacts/obj/Aspire.Hosting.Dotnet/dotnet-watch/`). A missing tool, missing
    metadata, or a selected SDK that isn't .NET 10 fails with an error that says how to fix it; there is no fallback.
    `ASPIRE_DOTNET_WATCH_TOOL_PATH` points at an explicit tool directory instead (for testing a different watcher build).
  - runs `dotnet exec <tool> server --sdk <sdk> --server <pipe> --status-pipe <pipe> --resource <each project>` from a
    per-run directory `/tmp/aspw-<id>`, which is deleted on shutdown. The directory is created `0700` (an existing or
    symlinked one is rejected) and every pipe/socket is `CurrentUserOnly` (`0600` sockets, peer UID checked).
  - reads the status pipe and the watcher's output, becomes healthy after the first successful build, and turns
    build/hot-reload/restart/exit events into short notices (`DotnetWatchEventInterpreter`). Notices go to the affected
    service's console log (prefixed `[watch]`) and to the `aspire run` terminal. Raw status JSON is debug-level only.
- Each service waits for `dotnet-watch`, then DCP launches `aspire __dotnet-watch-proxy --server <pipe> --entrypoint <project> -- <args>`
  instead of `dotnet run`. The proxy reads the service environment from its own process environment
  (only the names listed in `ASPIRE_DOTNET_WATCH_FORWARD_ENV`; values never go on the command line), sends the
  launch request over the watcher's resource pipe (30 s connect timeout, with an actionable error), and forwards stdout/stderr. Stopping or restarting the
  resource kills the proxy, which makes the watcher terminate the app.
- No silent fallbacks. Every project is watched unless it opts out, and an unsupported one fails the run with an error that
  names it and the fix:
  - `.WithWatch(false)` opts a project out: it runs as a plain `dotnet run`, and the terminal and its log say so (the
    Blazor gateway opts itself out).
  - Projects another resource waits on with `WaitForCompletion` run once, unwatched, with a notice: a watched service would
    be relaunched instead of completing. With an explicit `.WithWatch(true)` the run fails instead.
  - File-based C# apps (`AddDotnetProject("x", "app.cs")`), persistent lifetimes and custom launches (`WithLaunchToolArgs`,
    `ProjectLaunchArgsOverrideAnnotation`) fail the run unless they call `.WithWatch(false)`.
  - Under an IDE debug session (`DEBUG_SESSION_PORT` set) the IDE owns hot reload: nothing is watched, and the terminal and
    each service's log say so.
  - `AppHost:Run:WatchEnabled=true` without the CLI (a plain `dotnet run` of the AppHost) fails the run.

## What you see

`aspire run --watch` adds a **Watch** row to the summary, then prints one line per event (yellow for warnings, red for errors):

```text
       Watch:  🔥 Hot reload is on for C# services (preview). Save a file to apply changes.
               Press CTRL+C to stop the AppHost and exit.
22:14:02 👀 Watching api, worker for changes. Edits are hot reloaded; changes that need a restart restart only the affected service.
22:14:59 🔥 Hot reload applied to api, worker in 80 ms (Greeter.cs).
22:15:06 ❌ Build error in api: Program.cs(19,32): CS1525: Invalid expression term ')'. The app keeps running the last good code; fix it and save.
22:21:32 ✅ Fixed. api already runs this code; nothing to apply.
21:51:32 ⌚ Rebuilding worker…
21:51:38 🔄 Restarting worker to apply changes that can't be hot reloaded.
21:49:50 📦 WatchDemo.Api.csproj changed. New package or project references aren't hot reloaded; use Rebuild on api (dashboard, or 'aspire resource api rebuild') to apply them.
21:50:31 📝 appsettings.json changed; no code to update in api.
22:17:42 💥 api exited with code 3. Save a change or use Restart to relaunch it.
21:50:46 ▶️ api is running again.
22:19:42 ❌ Initial build failed: Program.cs(19,37): CS0117: 'Greeter' does not contain a definition for 'Mesage'. Fix it and save; api, worker start once the build succeeds.
22:21:13 ✅ Build succeeded. 👀 Watching api, worker for changes. …
```

In the dashboard:
- `dotnet-watch` (sync icon) is the watcher. It's **hidden** infrastructure like the rebuilders (dashboard: show hidden
  resources; CLI: `aspire describe --include-hidden`), out of the manifest, and has no Stop/Restart; **Start** appears only
  while it's stopped. It's **Running (Unhealthy)** with the first build error in its health report until the first build
  succeeds; services show **Waiting** until then, and the error is in the terminal and each service's log.
- If the watcher stops unexpectedly, the terminal and every watched service's log say so
  (`❌ The hot reload watcher stopped (exit code N) … 'aspire resource dotnet-watch start'`).
- Each service's console log has the same `[watch]` notices (filter the log by `[watch]`).
- A service that exits with a non-zero code shows **Crashed** with the exit code (property `dotnet-watch.last-event`); the next save, or Restart, relaunches it.
- **Stop**/**Start**/**Restart** work as usual. **Rebuild** stops the service, runs `dotnet build`, and starts it again; use it after adding a package or project reference.
- If the watch tool can't be resolved (tool missing, SDK isn't .NET 10), the terminal says why and what to do; after fixing it,
  **Start** `dotnet-watch` (`aspire resource dotnet-watch start`) and the services start.

## Hot reload scenario matrix

Measured on Linux (2 cores) with the playground; latency is file save → new response from `curl`. The rows with timings
were first measured on SDK 11.0.100-rc.2; method body, shared library, static file, `WithReplicas(2)` and Ctrl+C were
re-run on the shipped 10.0.403 tool.
"works" = worked without changes in this PR's later commits; "fixed" = needed a fix/UX work in this PR; "not supported" = documented limitation.

| Scenario | Status | Evidence | Related |
|---|---|---|---|
| Method body edit (api `MapGet` string) | works | 0.36–0.64 s; first edit after start 3.9–4.5 s (warm-up); same PID. [transcript](docs/transcripts/04-edit-compile-error-crash.txt) | #15710, #13924 |
| Add a member/type | works | New members/types hot reload; a new top-level `app.MapGet` route is a rude edit → api restarts (6.6 s) | #15710 |
| Razor Pages (`Pages/Hello.cshtml` markup and `@{}` code) | works | 0.36–0.46 s (first 4.5 s) | #13928 |
| Blazor (gateway / WebAssembly) | not supported | `WithBlazorGateway` resources opt out with `WithWatch(false)` and run with their own launch tool (unit-tested; no Blazor app in the playground) | #13928, #20361 |
| Static files (`wwwroot/hello.html`) | works | 0.02 s; `📝 hello.html changed in api; no rebuild needed.` | #13925 |
| `appsettings.json` | works | `/config` picks up the change in 3.4–8 s (this machine uses the polling file watcher) | |
| `.csproj`/`.props` change | fixed | Watcher re-evaluates the project; terminal says `📦 … use Rebuild on api` | #20997 |
| Add a PackageReference / ProjectReference | not supported (upstream) | The watcher reloads the project but doesn't rebuild, so the app throws `FileNotFoundException` (plain `dotnet watch` on the same SDK does the same). Rebuild fixes it in 11 s and hot reload keeps working afterwards. [transcript](docs/transcripts/03-replicas-csproj-static.txt) | #7695 |
| Rude edit → auto-restart | fixed | Only the affected service restarts, 6.6 s; spurious "hot reload applied" after the restart removed | #7695 |
| Compile error → fix | fixed | `❌ Build error in api: Program.cs(19,32): CS1525 …`, old code keeps serving; fix → `✅ Fixed. 🔥 Hot reload applied`; reverting → `✅ Fixed. api already runs this code` | #13928 |
| Initial build failure → fix | fixed | Terminal + service log + `dotnet-watch` Unhealthy with the error; fix → serving in 11–14 s. [screenshot](docs/08-initial-build-failure.png), [transcript](docs/transcripts/05-initial-build-failure-revert.txt) | #15710 |
| Shared class library edit | works | api and worker both updated, 0.43 s (watcher: 80 ms) | #20987 |
| Multiple projects | works | One watcher for api + worker (+ replicas); `HACK(spike)` TMPDIR/global.json for multi-root | #20997 |
| Service crash → restart | fixed | `/crash` → **Crashed**, exit code 3; save → `▶️ api is running again` (~2 s). [screenshot](docs/07-crashed.png) | |
| Dashboard Stop / Start / Restart / Rebuild | works | Start after Stop ~13 s; Restart re-launches with the latest applied code; Rebuild 11 s | #20702 |
| Ctrl+C | works | CLI exits in 0.27–0.4 s; DCP stops the watcher and services within ~3–20 s (up to 50 s seen on rc.2); `/tmp/aspw-*` removed | #17590 |
| Env vars and endpoints across restarts | works | `GREETING_SOURCE`, `services__api__*` and the 5380 endpoint survive rude-edit restarts (11 s edit→served) and relaunches (same target port; worker keeps reaching api); [transcript](docs/transcripts/08-restart-preserves-env-and-endpoints.txt) | |
| App arguments (`WithArgs`) | fixed | The watcher appends arguments directly to `dotnet run`; the proxy now prefixes them with `--`, so `WithArgs("--Demo:Greeting", "from args")` reaches the app (`/config` → `from args`) | |
| `WithReplicas(2)` | works | Shared edit reaches both replicas (same PIDs); rude edit restarts both. The watcher races concurrent first launches of the same web project ("Server not started" from its browser refresh server, unfixed in dotnet/sdk main), so the proxies launch a project's replicas one at a time until it has launched once (`--launch-gate`). [transcript](docs/transcripts/03-replicas-csproj-static.txt) | #13738 |
| File-based C# app (`AddDotnetProject("x", "app.cs")`) | not supported | The watch server runs `dotnet run` without `--file` ("Couldn't find a project to run"), so `aspire run --watch` fails until the app calls `.WithWatch(false)`, then it runs plain. [transcript](docs/transcripts/11-watch-opt-out-and-rejection.txt) | |
| Watch tool acquisition | fixed | Shipped in `Aspire.Hosting.Dotnet` (no download); a missing tool or non-.NET 10 SDK is an actionable terminal error. | |
| Plain `dotnet run` of the AppHost / IDE debug | fixed | IDE: notice, the IDE owns hot reload. `WatchEnabled` without the CLI: error | #15710 |
| Per-resource opt-out | fixed | `.WithWatch(false)`: terminal and resource-log notice, plain `dotnet run`. [transcript](docs/transcripts/11-watch-opt-out-and-rejection.txt) | #20702 |
| Run-to-completion / persistent | fixed | `WaitForCompletion` targets run once unwatched (notice); persistent lifetime is an error (unit-tested) | #9756 |
| AppHost edits (Session 8) | not supported | Restart `aspire run` | #13839 |

## Known gaps

- New package/project references need **Rebuild** (upstream watcher doesn't rebuild on reference changes).
- Blazor gateway resources and file-based apps can't be watched and must opt out with `WithWatch(false)` (upstream launch support needed).
- DCP takes 10–50 s to reap the watcher and services after Ctrl+C (the CLI itself exits immediately). Editing files while
  the AppHost is shutting down can leave relaunched services behind (seen once).
- **Crashed** is a custom state, so the dashboard summary counts it as "Inactive" rather than "needs attention".
- Session 8 (AppHost watch) isn't implemented; the TMPDIR/global.json multi-root workaround is still a `HACK(spike)`.
- Only Linux was tested. The shipped tool is 10.0.403; .NET 10 SDK feature bands other than 10.0.4xx use it unverified.
- Package-backed polyglot AppHosts (no MSBuild targets run for the prebuilt server) have no `aspiredotnetwatchtoolsdir`
  metadata; they find `tools/dotnet-watch/` next to `Aspire.Hosting.Dotnet.dll` in the restored package instead (unit-tested,
  not yet run end to end against a published package).
- The watcher builds and launches with its default configuration and from the project directory: `AppHost:BuildConfiguration`,
  `WithBuildEnvironment` and `WithWorkingDirectory` aren't honored in watch mode yet (the launch protocol has no fields
  for them).
- With `WithReplicas`, one replica exiting marks all replicas of that project **Crashed**: watcher status events identify
  projects, not instances.

## Repro

Prereqs: Linux or macOS. The repo's local SDK is installed by restore (`./restore.sh`; the `.dotnet` folder must contain
the SDK from the root `global.json`, plus the .NET 10 runtime that restore also installs). The services select a .NET 10
SDK (`services/global.json`), which must be installed next to the `dotnet` that runs them; for the repo-local muxer:
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir .dotnet`.

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
- Edit `services/WatchDemo.Api/Program.cs` (the `MapGet` string): hot reloaded in under a second (a few seconds for the first edit) with the same PID.
- Edit `services/WatchDemo.Shared/Greeter.cs`: both api and worker pick it up (the worker logs it every 5 s).
- Edit `services/WatchDemo.Api/Pages/Hello.cshtml` and `curl :5380/hello`; edit `wwwroot/hello.html` and `curl :5380/hello.html`.
- Edit `Demo:Greeting` in `services/WatchDemo.Api/appsettings.json` and `curl :5380/config`.
- Add a new `app.MapGet(...)` route (a rude edit): the watcher restarts only the api, with the same Aspire env/endpoint.
- Introduce a compile error: the terminal and the api log show the first error with file:line and the old code keeps running; fixing it reloads.
- `curl :5380/crash`: api shows **Crashed** (exit code 3); save any api file (or Restart) and it comes back.
- Add a `PackageReference` to `WatchDemo.Api.csproj`: the terminal suggests Rebuild; `aspire resource api rebuild` applies it.
- Add `.WithReplicas(2)` to the worker in `AppHost.cs` (restart `aspire run`): edits reach both replicas.
- `ticker` (`services/Scripts/ticker.cs`) is a file-based app with `.WithWatch(false)`: it runs plain and the terminal says so.
  Remove `.WithWatch(false)` and the run fails with an error naming `ticker`.
- Dashboard **Stop**/**Start**/**Restart**/**Rebuild** on api (or `aspire resource api <command>`).
- Ctrl+C: the CLI exits immediately; the watcher and services stop shortly after and `/tmp/aspw-*` is removed.
- Start with a compile error already present: services wait with "Initial build failed"; fix it and they start.

### Polyglot AppHosts

`TypeScriptAppHost/` (`apphost.mts`) and `PythonAppHost/` (`apphost.py`) reference the same `WatchDemo.Api` with
`addDotnetProject`/`add_dotnet_project`. `--watch` puts the watch signal into the AppHost server (where Aspire.Hosting runs),
so the C# service is hot reloaded exactly as from the C# AppHost, while the guest runtime keeps watching the AppHost file
itself (nodemon / Python reload). Run from the repo source with `ASPIRE_REPO_ROOT` (the AppHost server targets net10.0, so
`.dotnet` needs the .NET 10 runtime that `./restore.sh` installs):

```bash
export ASPIRE_REPO_ROOT=$PWD   # repo root
cd playground/DotnetProjectWatch/TypeScriptAppHost && npm install
../../../dotnet.sh ../../../artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll run --watch
# Python: features__experimentalPolyglotPython=true, from playground/DotnetProjectWatch/PythonAppHost
```

`defaultWatchEnabled` (`aspire config set features.defaultWatchEnabled true`) is the same as passing `--watch`; an
explicit `--watch false` wins. `--no-build` with watch on is an error. Under an IDE (VS Code run/debug session, Visual
Studio) the IDE owns hot reload: `--watch` prints a notice and isn't forwarded, and `--no-build` stays allowed inside the
IDE session.

## Evidence

Screenshots (dashboard) and terminal transcripts (`aspire run --watch` output, dashboard tokens redacted) from the runs above:

- [Resources while watching](docs/05-resources-watch.png): api, worker and dotnet-watch Running
- [api console filtered by `[watch]`](docs/06-api-watch-notices.png): hot reload, shared-library edit, compile error
- [Crashed](docs/07-crashed.png): api exited with code 3
- [Initial build failure](docs/08-initial-build-failure.png): dotnet-watch Unhealthy, services Waiting; [api log](docs/09-initial-build-failure-api-log.png) with the error
- Transcripts: [edits, shared lib, rude edit, crash](docs/transcripts/01-edits-shared-rude-crash.txt),
  [initial build failure, appsettings](docs/transcripts/02-initial-build-failure-appsettings.txt),
  [replicas, csproj/Rebuild, static files](docs/transcripts/03-replicas-csproj-static.txt),
  [edit, compile error, crash](docs/transcripts/04-edit-compile-error-crash.txt),
  [initial failure and revert](docs/transcripts/05-initial-build-failure-revert.txt),
  [restart preserves env/endpoints](docs/transcripts/08-restart-preserves-env-and-endpoints.txt),
  [TypeScript AppHost, --no-build errors, defaultWatchEnabled](docs/transcripts/09-typescript-apphost-watch.txt),
  [Python AppHost](docs/transcripts/10-python-apphost-watch.txt),
  [opt-out and rejection](docs/transcripts/11-watch-opt-out-and-rejection.txt)
- Earlier spike screenshots: [01](docs/01-resources.png), [02](docs/02-api-console.png), [03](docs/03-worker-console.png), [04](docs/04-dotnet-watch-console.png)
