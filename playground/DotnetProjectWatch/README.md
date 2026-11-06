# SPIKE: `aspire run --watch` for `AddDotnetProject` services

Preview-quality spike of [docs/plans/project-v2-csharpprogram-watch.md](../../docs/plans/project-v2-csharpprogram-watch.md).
Not production code. Shortcuts are marked `TODO(spike)` / `HACK(spike)` in the source.

## How it works

- `aspire run --watch` only sets `AppHost__Run__WatchEnabled=true` for the AppHost (or the polyglot AppHost server). The
  CLI hosts neither the proxy nor the watch server, so a plain `dotnet run` of the AppHost with that variable set behaves
  the same. Only C# services are watched; the AppHost itself is never watched or restarted.
- The proxy and the watch server live in a small watch host (`src/Aspire.Hosting.Dotnet.WatchHost`) that
  `Aspire.Hosting.Dotnet` ships under `tools/dotnet-watch-host/` next to the watch tool, found the same way (AppHost
  `aspiredotnetwatchhostdir` metadata; in-repo `artifacts/obj/Aspire.Hosting.Dotnet/dotnet-watch-host/`) and run with
  `dotnet exec`.
- In watch runs, `AddDotnetProject` skips Aspire's coordinated build and registers every service with one
  `dotnet-watch` executable resource (`Aspire.Hosting.Dotnet/Watch/DotnetWatchCoordinator.cs`). The watcher's build is the only build.
  A project with `WithBuildEnvironment` and a file-based app get a watch server of their own (`dotnet-watch-<name>`), and
  projects whose `global.json` selects a different SDK share one per SDK scope. Each resource:
  - resolves the SDK the services use (their `global.json`) and runs the `Microsoft.DotNet.HotReload.Watch.Aspire` 10.0.403
    server, so **watch mode supports .NET 10 SDKs only**. Nothing is downloaded at run time: like DCP, the tool is
    `PackageDownload`ed when `Aspire.Hosting.Dotnet` builds, packed into its nupkg under `tools/dotnet-watch/`, and
    `buildTransitive/Aspire.Hosting.Dotnet.targets` stamps that directory on the AppHost as `aspiredotnetwatchtoolsdir`
    assembly metadata (in-repo AppHosts use `artifacts/obj/Aspire.Hosting.Dotnet/dotnet-watch/`). A missing tool, missing
    metadata, or a selected SDK that isn't .NET 10 fails with an error that says how to fix it; there is no fallback.
    `ASPIRE_DOTNET_WATCH_TOOL_PATH` points at an explicit tool directory instead (for testing a different watcher build).
  - runs `dotnet exec <host> server -- dotnet exec <tool> server --sdk <sdk> --server <pipe> --status-pipe <pipe> --control-pipe <pipe> --resource <each project>`
    (the host supervises the watcher's process tree and stops it when the AppHost goes away) from a
    per-run directory `/tmp/aspw-<id>`, which is deleted on shutdown. The directory is created `0700` (an existing or
    symlinked one is rejected) and every pipe/socket is `CurrentUserOnly` (`0600` sockets, peer UID checked).
  - is **protocol-only**: all state comes from the watcher's `--status-pipe` JSON events (`building`,
    `build_complete{success}`, `hot_reload_applied`, `restarting`, `process_started`, `process_exited{exitCode}`).
    It becomes healthy after the first successful build and turns those events into short notices
    (`DotnetWatchEventInterpreter`). Notices go to the affected service's console log (prefixed `[watch]`) and to the
    `aspire run` terminal. The watcher's console output is shown as plain logs of the hidden `dotnet-watch` resource and
    is never parsed. Raw status JSON is debug-level only.
  - sends `{"type":"rebuild","projects":[...]}` on the `--control-pipe` for the dashboard **Restart** of a running
    service. Verified on the 10.0.403 tool: `rebuild` does **not** build. It emits `restarting` and relaunches every
    running process of the listed projects (`dotnet run --no-build`, new PIDs, earlier hot reload deltas replayed); a
    crashed process is skipped. So Restart of a crashed service, or of a project with several replicas, uses the normal
    DCP restart (the proxy relaunches through the watcher).
  - watches project files itself (`DotnetWatchProjectFileWatcher`: `*.*proj`, `.props`, `.targets` in each project and
    referenced-project directory, plus `Directory.Build.props/targets` and `Directory.Packages.props` in ancestors). The
    watcher re-evaluates a changed project file and restores, but doesn't rebuild or relaunch: an added PackageReference
    then fails at run time (`FileNotFoundException` for the new assembly, same PID). On such a change Aspire restarts the
    services of that watch server with a fresh watcher.
- Each service waits for `dotnet-watch`, then DCP launches `dotnet exec <host> proxy --server <pipe> --entrypoint <project> -- <args>`
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
  - Persistent lifetimes and custom launches (`WithLaunchToolArgs`, `ProjectLaunchArgsOverrideAnnotation`) fail the run
    unless they call `.WithWatch(false)`. File-based C# apps (`AddDotnetProject("x", "app.cs")`) are watched.
  - Under an IDE debug session (`DEBUG_SESSION_PORT` set) the IDE owns hot reload: nothing is watched, and the terminal and
    each service's log say so.

## What you see

`aspire run --watch` adds a **Watch** row to the summary, then prints one line per event (yellow for warnings, red for errors):

```text
       Watch:  🔥 Hot reload is on for C# services (preview). Save a file to apply changes.
               Press CTRL+C to stop the AppHost and exit.
22:14:02 👀 Watching api, worker for changes. Edits are hot reloaded; changes that need a restart restart only the affected service.
07:43:42 🔥 Hot reload processed the change for api, worker. Compile errors, if any, are in the 'dotnet-watch' resource logs.
07:44:22 ⌚ Rebuilding api…
07:44:25 🔄 Restarting api…
07:46:30 📦 WatchDemo.Api.csproj changed.
07:46:30 ♻️ Restarting api, worker with a fresh watcher to apply the project file changes.
07:45:33 💥 api exited with code 3. Save a change or use Restart to relaunch it.
07:46:02 ▶️ api is running again.
07:47:27 ♻️ Restarting ticker with a fresh watcher to rebuild the file-based app.
22:19:42 ❌ Initial build failed: see the 'dotnet-watch' resource logs for the errors. Fix it and save; api, worker start once the build succeeds.
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
  With `WithReplicas(n > 1)` no replica is marked Crashed (see Accepted losses). The dashboard summary counts Crashed as
  an error (custom states are classified by their style), not as Inactive.
- **Stop**/**Start** work as usual. **Restart** of a running single-replica service asks the watcher to relaunch it
  (`rebuild` on the control pipe, no build); otherwise it's the normal restart. **Rebuild** stops the service, runs
  `dotnet build`, and starts it again.
- If the watch tool can't be resolved (tool missing, SDK isn't .NET 10), the terminal says why and what to do; after fixing it,
  **Start** `dotnet-watch` (`aspire resource dotnet-watch start`) and the services start.

### Accepted losses (protocol-only)

The 10.0.403 status pipe carries less than the watcher's console, and the console is no longer parsed. Until the status
pipe grows the missing fields ([dotnet/sdk issue](#upstream-asks)), these are gone:

- **Error text, apply timing and changed files.** `build_complete` leaves `error` empty and `hot_reload_applied` has no
  elapsed time or file list, so notices say "see the 'dotnet-watch' resource logs" and "Hot reload processed the change
  for api" instead of the first compiler error, "in 80 ms" and "(Greeter.cs)". The watcher's output (with the errors) is
  in the `dotnet-watch` resource logs.
- **"No changes" and "blocked by compile errors" notices.** `hot_reload_applied` is emitted after every apply attempt,
  whether it applied deltas, found nothing to apply, or was blocked by compile errors, so all three show the same 🔥
  notice. A compile error during a session isn't announced; the app keeps running the last good code.
- **Per-replica crash attribution.** `process_*` events name the project, not the process. With `WithReplicas(n > 1)` a
  crash shows the 💥 notice and updates `dotnet-watch.last-event`, but no replica is marked **Crashed**.

### Upstream asks

Related: [dotnet/sdk#56966](https://github.com/dotnet/sdk/issues/56966). Asks: populate `error`; add `no_changes`, `apply_blocked` and `file_changed`
events; add `elapsedMs`; and echo a request/replica id in the `process_*` events.

## Hot reload scenario matrix

Measured on Linux (2 cores) with the playground; latency is file save → new response from `curl`. The rows with timings
were first measured on SDK 11.0.100-rc.2; the scripted matrix (method body, shared library, Razor, static file,
appsettings, rude edit, compile error, `.csproj` change, crash, Stop/Start/Restart/Rebuild, env and endpoints), the
file-based app, PackageReference add/remove, `WithReplicas(2)` crash, Release/`WithBuildEnvironment`/`WithWorkingDirectory`
and Ctrl+C were re-run on the shipped 10.0.403 tool at the PR head (all pass).
After the protocol-only change, hot reload (same PID), rude edit → restart, compile error → recovery, crash → Crashed,
Restart (running via `rebuild`, crashed via DCP), `.csproj` change, file-based rude edit/compile error and Ctrl+C were
re-run (all pass). Older transcripts under `docs/` show the earlier, console-derived notice wording.
"works" = worked without changes in this PR's later commits; "fixed" = needed a fix/UX work in this PR; "not supported" = documented limitation.

| Scenario | Status | Evidence | Related |
|---|---|---|---|
| Method body edit (api `MapGet` string) | works | 0.36–0.64 s; first edit after start 3.9–4.5 s (warm-up); same PID. [transcript](docs/transcripts/04-edit-compile-error-crash.txt) | #15710, #13924 |
| Add a member/type | works | New members/types hot reload; a new top-level `app.MapGet` route is a rude edit → api restarts (6.6 s) | #15710 |
| Razor Pages (`Pages/Hello.cshtml` markup and `@{}` code) | works | 0.36–0.46 s (first 4.5 s) | #13928 |
| Blazor (gateway / WebAssembly) | not supported | `WithBlazorGateway` resources opt out with `WithWatch(false)` and run with their own launch tool (unit-tested; no Blazor app in the playground) | #13928, #20361 |
| Static files (`wwwroot/hello.html`) | works | 0.02 s; shows the generic 🔥 notice (no `file_changed` event) | #13925 |
| `appsettings.json` | works | `/config` picks up the change in 3.4–8 s (this machine uses the polling file watcher) | |
| `.csproj`/`.props` change | fixed | Aspire's project-file watcher: `📦 WatchDemo.Api.csproj changed.` then `♻️ Restarting api, worker with a fresh watcher`; serving again in 15–22 s (revert 20–27 s) | #20997 |
| Add a PackageReference / ProjectReference | fixed | The watcher only re-evaluates a changed project file, and replays its earlier deltas to the relaunched app (an app built without the package then aborts with `MethodAccessException`). So a project-file change restarts the services on that watch server with a fresh watcher: add Humanizer → restarted in 24–29 s, an edit using it hot reloads in 4.9–5.3 s, removing it restarts in 20–26 s, api Running. | #7695 |
| Rude edit → auto-restart | fixed | Only the affected service restarts, 6.6 s; spurious "hot reload applied" after the restart removed | #7695 |
| Compile error → fix | fixed | Old code keeps serving, same PID; the error is in the `dotnet-watch` logs and the notice is the generic 🔥 one (accepted loss); fix → hot reloaded, same PID | #13928 |
| Initial build failure → fix | fixed | Terminal + service log + `dotnet-watch` Unhealthy with the error; fix → serving in 11–14 s. [screenshot](docs/08-initial-build-failure.png), [transcript](docs/transcripts/05-initial-build-failure-revert.txt) | #15710 |
| Shared class library edit | works | api and worker both updated, 0.43 s (watcher: 80 ms) | #20987 |
| Multiple projects | fixed | One watcher for api + worker (+ replicas). Projects are grouped per `global.json` SDK scope; the multi-root temp directory redirect (upstream dotnet/sdk#51311) only applies when the temp directory selects a different SDK, and services get their own temp directory back | #20997 |
| Service crash → restart | fixed | `/crash` → **Crashed**, exit code 3; save → relaunched in 6–8 s. Dashboard summary counts it as an error. [screenshot](docs/07-crashed.png) | |
| Dashboard Stop / Start / Restart / Rebuild | works | Start after Stop ~13 s; Restart of a running service → `rebuild` on the control pipe → `🔄 Restarting api…`, new PID, no build; Restart of a crashed service → `▶️ api is running again.`; Rebuild 8–10 s, hot reload afterwards 0.7 s | #20702 |
| Build configuration, `WithBuildEnvironment`, `WithWorkingDirectory` | fixed | Playground switches `WATCHDEMO_BUILD_CONFIGURATION=Release WATCHDEMO_BUILD_MARKER=marker-42 WATCHDEMO_WORKING_DIRECTORY=/tmp`: `/diag` shows `assemblyConfiguration: Release`, `buildMarker: marker-42`, `workingDirectory: /tmp`; an edit hot reloads in 4.6–5.2 s, same PID. The configuration travels as `Configuration` + `Optimize=false` (the agent needs unoptimized code); build env moves the project to its own watch server; the working directory is `-p:RunWorkingDirectory`. | |
| Ctrl+C | fixed | CLI exits in 0.2–0.5 s; services gone in 0.4–0.8 s, watcher, its host and file-based app in 1.9–2.3 s, DCP in 3.4–5.2 s (was 10–50 s); `/tmp/aspw-*` removed. The watch host gives the watcher a 1.5 s grace, then kills its supervised process group | #17590 |
| Env vars and endpoints across restarts | works | `GREETING_SOURCE`, `services__api__*` and the 5380 endpoint survive rude-edit restarts (11 s edit→served) and relaunches (same target port; worker keeps reaching api); [transcript](docs/transcripts/08-restart-preserves-env-and-endpoints.txt) | |
| App arguments (`WithArgs`) | fixed | The watcher appends arguments directly to `dotnet run`; the proxy now prefixes them with `--`, so `WithArgs("--Demo:Greeting", "from args")` reaches the app (`/config` → `from args`) | |
| `WithReplicas(2)` | fixed | Shared edit reaches both replicas (same PIDs); rude edit restarts both. `/crash` on one replica prints `💥 api exited with code 3` but marks no replica Crashed (the status pipe doesn't say which process exited; accepted loss); a save relaunches it. The watcher races concurrent first launches of the same web project ("Server not started" from its browser refresh server, unfixed in dotnet/sdk main), so the proxies launch a project's replicas one at a time until it has launched once (`--launch-gate`). [transcript](docs/transcripts/03-replicas-csproj-static.txt) | #13738 |
| File-based C# app (`AddDotnetProject("x", "app.cs")`) | fixed | Playground `ticker` (`Scripts/ticker.cs`). Method edits hot reload (3–10 s including the 3 s tick; watcher 57–83 ms after warm-up), same PID. A top-level statement edit restarts it with a fresh watcher in 15–24 s; hot reload works afterwards. Workarounds for the watcher: the proxy adds `--file`; status paths of the virtual `ticker.cs.csproj` map back to `ticker.cs`; the app gets its own watch server (a project-file change otherwise crashes the shared watcher); `StartupHookSupport`/`MetadataUpdaterSupport` are turned on (the PublishAot default turns them off, so every edit failed with ENC0097 on .NET 10); a failed `build_complete` for the virtual `ticker.cs.csproj` after startup (the watcher can't rebuild it) restarts the app with a fresh watcher. | |
| Watch tool acquisition | fixed | Shipped in `Aspire.Hosting.Dotnet` (no download); a missing tool or non-.NET 10 SDK is an actionable terminal error. | |
| Plain `dotnet run` of the AppHost / IDE debug | works | IDE: notice, the IDE owns hot reload. `AppHost__Run__WatchEnabled=true` without the CLI: services are watched and hot reloaded the same as with `aspire run --watch` | #15710 |
| Per-resource opt-out | fixed | `.WithWatch(false)`: terminal and resource-log notice, plain `dotnet run`. [transcript](docs/transcripts/11-watch-opt-out-and-rejection.txt) | #20702 |
| Run-to-completion / persistent | fixed | `WaitForCompletion` targets run once unwatched (notice); persistent lifetime is an error (unit-tested) | #9756 |
| AppHost edits (Session 8) | not supported | Restart `aspire run`. See Known gaps. | #13839 |

## Known gaps

Closed in this PR (see the PR description for commits): build configuration / `WithBuildEnvironment` /
`WithWorkingDirectory`, Crashed counted as Inactive, slow Ctrl+C, the TMPDIR/global.json hack,
file-based apps and package/project reference changes. Still open:

- **Blazor gateway (`WithBlazorGateway`)**: not feasible here. The gateway runs through its own launch tool
  (`WithLaunchToolArgs`), and the watch server can only launch `dotnet run` for a project it builds; it has no way to run a
  custom launcher or to attach to a process it didn't start. It opts itself out (`WithWatch(false)`). Needs a launch hook in
  the watch server (dotnet/sdk).
- **AppHost edits (Session 8)**: not implemented. The AppHost is never watched by design (`--watch` covers services only):
  restarting it means tearing down DCP and every resource, which needs a CLI-level restart loop around the AppHost
  process, not a change to the service watcher. Restart `aspire run` after editing the AppHost.
- **Reference changes restart a whole watch server**: a project-file change restarts every service on that watch server
  (15–29 s), because the watcher can't rebuild one root or forget its delta history. A file-based app or a project with
  `WithBuildEnvironment` has its own server, so only it restarts.
- **Windows and macOS were not run.** Only Linux was tested end to end. The code paths for the others are written and
  unit-tested where possible: named pipes vs. Unix sockets (short socket paths under `/tmp/aspw-*`, within macOS's
  104-byte limit), the watch host's supervisor (kill-on-close job object on Windows, process group on Unix, so macOS's lack
  of a subreaper doesn't matter), `PosixSignalRegistration` (mapped to console control events on Windows),
  `TMP`/`TEMP` vs. `TMPDIR` for the temp directory redirect, case-insensitive path comparison for status paths on Windows
  and macOS, and per-user private run directories.
- The shipped tool is 10.0.403; .NET 10 SDK feature bands other than 10.0.4xx use it unverified.
- Package-backed polyglot AppHosts (no MSBuild targets run for the prebuilt server) have no AppHost metadata. They load
  `Aspire.Hosting.Dotnet.dll` straight from its restored package, and then that package's `tools/dotnet-watch/` and
  `tools/dotnet-watch-host/` are the only source (chosen by load location, not probed after the metadata; unit-tested,
  not yet run end to end against a published package).
- Build settings travel in the watcher's environment (it has no per-project build arguments), so they have environment
  precedence: a project that sets `Configuration` or `Optimize` unconditionally wins. A variable that was unset before
  comes back to the service as empty rather than unset (`configuration: ""` in `/diag`). In watch mode a Release build is
  unoptimized (`Optimize=false`), which the hot reload agent requires.

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
- Add a `PackageReference` to `WatchDemo.Api.csproj`: Aspire's project-file watcher restarts api and worker with a fresh watcher.
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
