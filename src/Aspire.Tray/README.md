# Aspire Tray

An experimental **C# NativeAOT** companion for the Aspire CLI, currently bundled on macOS.
It uses native AppKit status items and menus through a small Objective-C runtime
interop layer. It does not require a Swift build step, a WebView, a macOS .NET workload, or an
installed .NET runtime on the machine running the published app.

## Project layout

- `src/Aspire.Tray/Common/`: shared protocol client, controller, and lifecycle code.
- `src/Aspire.Tray/Mac/`: native AppKit frontend and macOS app packaging.
- `src/Aspire.Tray/Windows/`: native Windows frontend, not yet bundled.
- `tests/Aspire.Tray.Tests/`: protocol, controller, and lifecycle tests in the normal test matrix.

The platform projects and tests are included in `Aspire.slnx` and inherit the
repository build, analyzer, versioning, and test infrastructure. Shared code is
source-linked into each executable; no separate UI framework or shared runtime
assembly is deployed.

## Try the bundled companion on macOS

The macOS native CLI bundle includes `tray/Aspire Tray.app`. No separate tray
installer or private CLI installation is required:

```sh
aspire tray start
aspire tray start  # Restore the existing icon without another watcher.
aspire tray stop   # Quit the companion, not the AppHosts.
```

Use a native CLI built from this branch. To try a PR build after its native
archive job finishes, use [PR dogfooding](../../docs/dogfooding-pull-requests.md)
in archive mode and invoke that PR's CLI explicitly, rather than an older
`aspire` on PATH. Windows and Linux return an experimental macOS-only error.
Managed development CLIs cannot start the bundled companion.

Start extracts the payload into the CLI installation's versioned bundle layout.
A short-lived helper launches the app through macOS Launch Services and waits
for an acknowledgement from its running UI loop. The GUI acquires its own
bundle lease before acknowledging readiness; the CLI and helper retain their
leases until the handoff completes. The GUI then survives the launching command
and terminal, while its lease prevents Aspire's bundle cleanup from removing its
files. Stop waits for that exact tray process lifetime to exit.

There is one companion per OS user across CLI installations. Starting from a
different CLI restores an already-running companion; it does not hot-swap its
backend or bundle. Stop and start again to adopt a new installation/version.
The companion uses the absolute invoking CLI path for discovery and actions,
not a copied or independently pinned CLI.

The same-user control endpoint is
`~/Library/Application Support/Aspire/Tray/control-v1.sock`.
Launch diagnostics go to `aspire-tray.log` in the same directory, with
user-only creation permissions and no raw discovery payloads or dashboard URLs.
Quit the tray using its original CLI or **Quit Aspire** before upgrading from an
earlier preview. The state directory has been renamed; a still-running older
preview uses a separate control endpoint and must be stopped before starting this build.

## Build and run on macOS

Building requires a compatible .NET SDK and Xcode Command Line Tools. From the
repository root, bootstrap the repository SDK if necessary:

```sh
./restore.sh -projects "$PWD/src/Aspire.Tray/Mac/Aspire.Tray.Mac.csproj"
```

Publish for the current machine, or pass `osx-arm64` or `osx-x64` explicitly:

```sh
bash src/Aspire.Tray/Mac/publish.sh
```

This uses the same publish/package target as the CLI bundle. To build a complete
Apple Silicon CLI into an isolated output directory:

```sh
./dotnet.sh msbuild eng/Bundle.proj \
    -p:Configuration=Release -p:TargetRid=osx-arm64 \
    -p:CliPublishDir="$PWD/artifacts/tray-cli-validation"
```

The resulting `artifacts/tray-cli-validation/aspire` embeds the app together with
the existing managed and DCP payloads. `SkipNativeBuild=true` skips only the
outer CLI build, not the NativeAOT tray. The app is verified from the actual
payload archive before embedding.

Quit the running tray before replacing its `.app` or republishing the CLI
executable it uses. A running NativeAOT executable must not be overwritten.

Build the CLI from this checkout so it includes the experimental versioned
discovery and exact-instance stop protocol. On Apple Silicon, run:

```sh
dotnet build src/Aspire.Cli/Aspire.Cli.csproj

DOTNET_ROOT="$PWD/.dotnet" \
"artifacts/bin/Aspire.Tray.Mac/Release/net10.0/osx-arm64/app/Aspire Tray.app/Contents/MacOS/aspire-tray" \
    --cli "$PWD/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire"
```

`DOTNET_ROOT` is needed by the managed development CLI, not by the NativeAOT tray.
With a published CLI that supports protocol version 1, pass its absolute
executable path instead; a NativeAOT CLI does not need this runtime setting.

The foreground development entrypoint's `--cli` argument must be an existing
**absolute executable path**. Unlike the packaged start command, this development
entrypoint also accepts a managed CLI apphost when its .NET runtime is available.

The menu bar shows the full-color Aspire icon from `src/Shared/Aspire_icon_256.png`
and a compact observed AppHost count.
The menu has a branded header and native two-line AppHost rows: project/worktree
name first, directory context and PID underneath. Each AppHost has an
**Open Dashboard** action and **Stop AppHost...** action. Menu items have no
hover tooltips, so they cannot cover the action submenu. Stopping opens a native
confirmation dialog showing the full project path and PID, with Cancel as the
default.
Long names are shortened in the middle, preserving both ends. Native subtitles
require macOS 14.4; older systems use a single-line layout with the same details.
**Quit Aspire** (Command-Q while using the menu) removes the icon and stops its
own discovery subprocess, not any AppHost.
A repeated launch restores the existing tray icon instead of starting another
instance or watcher. It sends a same-user activation request, waits for native
restoration to complete, and exits successfully. An unresponsive or older
instance without activation support produces an explicit error after ten seconds;
it is never killed or silently replaced.
The menu-bar item has a stable autosave identity so macOS can preserve its
placement. Hold Command and drag the icon to rearrange it. The native smoke
harness uses a separate identity and does not share the normal item's placement.
If the icon becomes hidden after rearranging it, run the same launch command
again. Explicit restoration resets the item toward the right side using AppKit's
undocumented saved-position preference (300 points from the right edge, verified
during development); normal launches retain the user's placement. This recovery is
not a guarantee against every crowded menu-bar/display configuration.
Open tray menus or confirmation dialogs defer restoration until they close.
Single-instance ownership uses an OS file lock under
`~/Library/Application Support/Aspire/Tray/instance.lock`; the empty file
remains after exit, but its lock is released automatically. NativeAOT's Unix
named mutex implementation does not provide cross-process exclusion.
Before opening the lock, the state directory's owner-only permissions are
enforced even when the directory already exists.

The publish target creates an accessory `.app` with `LSUIElement`, so it has no
Dock icon. Local and GitHub builds use an ad-hoc hardened-runtime signature.
The official pipeline signs/notarizes the whole app before copying it into the
payload and embeds it without republishing. That official signing path still
requires release-pipeline validation; this remains a draft feedback POC.

## Architecture and boundaries

- One global companion per OS user, independent of the launch directory.
- One long-lived child process:
  `aspire ps --follow --format json --protocol-version 1 --non-interactive --nologo`.
- `IAppHostClient` is the only backend interface: watch snapshots and stop an
  exact instance. `CliAppHostClient` owns subprocesses and protocol validation.
  No shell, per-AppHost watchers, or direct backchannel access from the tray.
- `TrayController` owns application state and independent per-AppHost operations.
  Its immutable view snapshots contain no AppKit handles or selectors.
  `AppHostPresentation` handles names, truncation, and directory context.
- The macOS adapter owns native menus, callbacks, confirmation, browser launch,
  and the main-thread event loop. There is no generic UI framework, MVVM/DI
  container, reflection-based binding, or additional UI runtime.
- UI changes are posted to the native main thread and coalesced, not polled.
  While a menu is open, existing action state can update, but structural changes
  wait until it closes so rows cannot move under the pointer.
- Actions carry path/PID/process-lifetime identity, not indices into a changing
  list. Discovery revalidates that identity after native confirmation.
- An explicit initial snapshot distinguishes an empty list from connecting.
  Complete replacement snapshots prevent incremental reconnect lists.
  Heartbeats maintain liveness without creating presentation updates.
- Unexpected EOF, liveness timeout, and discovery failures disable stale actions
  and retry with backoff capped at ten seconds. A snapshot alone does not reset
  that backoff; a heartbeat must confirm the session survived a liveness window.
  Incompatible output and declared
  size limits fail closed without retrying indefinitely. Raw CLI output and
  dashboard URLs are not logged.
- Dashboard actions revalidate the selected instance and open only absolute
  HTTP(S) URLs without URI userinfo. Login query strings are preserved.
- Stop actions revalidate the selection after confirmation, then invoke
  `aspire stop --apphost <absolute path> --pid <pid> --started-at <Unix milliseconds> --format json --protocol-version 1 --non-interactive --nologo`.
  The CLI revalidates the process lifetime before connection-bound shutdown.
  There is no fallback to PID-only or project-wide stop. If process lifetime
  cannot be discovered, the row remains visible but Stop is disabled.
- Stop runs off the UI thread, with independent progress and errors per AppHost
  and a 60-second command timeout. Stopping one does not block stopping another;
  duplicate requests for the same instance are rejected. The discovery stream
  removes the row when the instance disappears. Quit cancels owned command processes;
  an already-requested AppHost shutdown may still complete.
- Persistent resources are not force-cleaned by Stop. Other instances of the
  same project are not targeted.

The experimental wire contract is defined once in
`src/Shared/TrayCliProtocol.cs`, source-linked into the CLI, tray, and tests.
It uses source-generated JSON and complete NDJSON snapshots, including
`{"version":1,"type":"snapshot","appHosts":[]}`. A heartbeat is emitted every
ten seconds after initial discovery; the client treats thirty seconds without
a complete frame as loss of liveness. Limits are 1,000 AppHosts and 1 Mi UTF-16
characters per message; an oversized list is an error, never a truncated success.
Stop returns a versioned outcome and an exit code that must agree with the
process exit. Human-readable diagnostics are not part of the protocol.
The opt-in protocol does not change existing `ps --follow --format json`
consumers. See [CLI output formats](../../docs/specs/cli-output-formats.md).

Restarting AppHosts, resource details, search, login startup, automatic tray
upgrade handoff, and Windows native UX are outside this POC. The companion's own
project remains workload-free and isolated from the product's managed build
configuration, but the macOS bundle builds and ships it.

The undocumented icon-placement recovery is not a supported macOS positioning
API, and very long home-directory paths can exceed macOS's Unix socket path
limit. Official signing/notarization, long-running log management, and upgrade
behavior need further hardening before a stable release. Keeping the backend in
the CLI also means one additional long-lived native CLI process and a versioned
subprocess protocol, rather than a single-process tray.

## Checks

The separate native smoke harness uses an injected fake backend with real
AppKit objects and callbacks. It checks menu structure, titles/subtitles, icons,
accessibility, absent tooltips, enabled state, safe-default confirmation, and
Quit. Scenarios include empty/disconnected discovery, missing dashboards,
independent pending stops, reordered rows, process replacement, retained
senders after menu closure, updates during genuine native menu tracking, and
acknowledged hidden-icon restoration deferred until menus close.
It never opens a browser or starts/stops real AppHosts and exits automatically.
Smoke bypasses the normal singleton lock, so it can run alongside the tray.

```sh
DOTNET_ROOT="$PWD/.dotnet" \
"artifacts/bin/Aspire.Tray.Mac/Release/net10.0/osx-arm64/app/Aspire Tray.app/Contents/MacOS/aspire-tray" \
    --cli "$PWD/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire" --smoke-seconds 20

dotnet test --project tests/Aspire.Tray.Tests/Aspire.Tray.Tests.csproj \
    --no-launch-profile -- \
    --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Set `ASPIRE_TRAY_SMOKE_VERIFY_CLI=1` to additionally verify a real read-only
discovery stream through the supplied CLI. Real discovered identities are
never passed to smoke actions. Without that setting, `--cli` only needs to
reference an existing absolute executable; all discovery/action data is fake.

Platform-independent tests exercise protocol framing and validation, subprocess
cleanup, reconnects, lifetime replacement, concurrent stops, and controller
shutdown without AppKit or running AppHosts. macOS ARM64 is the runtime exercised
during development. The Windows scaffold shares the typed backend/controller, but its
native UX and runtime validation remain deferred; its menu is still read-only.
macOS x64 is not runtime-validated.
