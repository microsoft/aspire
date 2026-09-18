# Aspire Tray

An experimental **C# NativeAOT** companion for the Aspire CLI, bundled on macOS and Windows.
It uses native AppKit status items on macOS and Win32 notification icons and menus
on Windows through small platform interop layers. It does not require a Swift build
step, a WebView, a macOS .NET workload, Windows Forms, or an installed .NET runtime
on the machine running the published app.

## Project layout

- `src/Aspire.Tray/Common/`: shared protocol client, controller, and lifecycle code.
- `src/Aspire.Tray/Mac/`: native AppKit frontend and macOS app packaging.
- `src/Aspire.Tray/Windows/`: native Win32 frontend and Windows publishing.
- `tests/Aspire.Tray.Tests/`: protocol, controller, and lifecycle tests in the normal test matrix.

The platform projects and tests are included in `Aspire.slnx` and inherit the
repository build, analyzer, versioning, and test infrastructure. Shared code is
source-linked into each executable; no separate UI framework or shared runtime
assembly is deployed.

## Try the bundled companion

The native CLI bundle includes `tray/Aspire Tray.app` on macOS or
`tray/aspire-tray.exe` and `tray/Aspire.ico` on Windows. No separate tray installer
or private CLI installation is required:

```sh
aspire tray start
aspire tray start  # Restore the existing icon without another watcher.
aspire tray stop   # Quit the companion, not the AppHosts.
```

Use a native CLI built from this branch. To try a PR build after its native
archive job finishes, use [PR dogfooding](../../docs/dogfooding-pull-requests.md)
in archive mode and invoke that PR's CLI explicitly, rather than an older
`aspire` on PATH. Linux remains unsupported.
Managed development CLIs cannot start the bundled companion.

Start extracts the payload into the CLI installation's versioned bundle layout.
A short-lived helper launches the native GUI and waits for an acknowledgement
from its running UI loop. On macOS, the helper spawns the executable inside the
app bundle in a detached session, retaining ownership of the exact child until
readiness succeeds. The GUI acquires its own
bundle lease before acknowledging readiness; the CLI and helper retain their
leases until the handoff completes. The GUI then survives the launching command
and terminal, while its lease prevents Aspire's bundle cleanup from removing its
files. A failed handoff terminates and waits for the exact newly launched child
on either platform; it does not terminate an already-running companion.
Stop waits for that exact tray process lifetime to exit.

There is one companion per OS user across CLI installations. Starting from a
different CLI restores an already-running companion; it does not hot-swap its
backend or bundle. Stop and start again to adopt a new installation/version.
The companion uses the absolute invoking CLI path for discovery and actions,
not a copied or independently pinned CLI.

The macOS same-user control endpoint is
`~/.aspire/tray/runtime/control-v1.sock`. The socket and singleton lock use the
user profile directly, independent of `ASPIRE_HOME` and CLI installation, so all
launchers can find the same per-user tray.
Launch diagnostics remain at `~/Library/Application Support/Aspire/Tray/aspire-tray.log`, with
user-only permissions and no raw discovery payloads or dashboard URLs.
The launcher opens the log relative to no-follow directory handles and passes
the open descriptor to the GUI, rather than asking another process to reopen a
log pathname. Symlinked log files or parent directories are rejected.
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

Build the CLI from this checkout so it includes public snapshot discovery and
the experimental exact-instance stop protocol. On Apple Silicon, run:

```sh
dotnet build src/Aspire.Cli/Aspire.Cli.csproj

DOTNET_ROOT="$PWD/.dotnet" \
"artifacts/bin/Aspire.Tray.Mac/Release/net10.0/osx-arm64/app/Aspire Tray.app/Contents/MacOS/aspire-tray" \
    --cli "$PWD/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire"
```

`DOTNET_ROOT` is needed by the managed development CLI, not by the NativeAOT tray.
With a published CLI that supports `ps --output snapshot` and the exact-stop protocol, pass its absolute
executable path instead; a NativeAOT CLI does not need this runtime setting.

The foreground development entrypoint's `--cli` argument must be an existing
**absolute executable path**. Unlike the packaged start command, this development
entrypoint also accepts a managed CLI apphost when its .NET runtime is available.

The menu bar shows the 20-point single-wave artwork from `Mac/Assets/AspireTrayTemplate.png`
and `AspireTrayTemplate@2x.png` on a 22-point canvas. Excess transparent padding is
trimmed from the supplied high-resolution artwork before generating the 20- and
40-pixel standard/Retina representations, so the visible mark fills the tray space.
The composite is an AppKit template image, preserving the assets' transparent
diagonal wave and adapting its foreground to the menu-bar background. These are Aspire-inspired concept assets, not
official brand masters; their supplied provenance is in `Mac/Assets/PROVENANCE.txt`.
The app bundle and dialogs continue to use `src/Shared/Aspire_icon_256.png`.
The lower-right connection badge replaces a numeric count: the idle mark is
unbadged, dots indicate connecting, a cross indicates unavailable discovery, and
a solid dot indicates active AppHosts. The badge's transparent border reveals the menu-bar background rather
than painting an opaque outline. Native two-line AppHost
rows show the project/worktree name first and directory context and PID underneath.
The redundant branded/count header is omitted when AppHosts are listed; an empty
placeholder or actionable error/disconnection notice is shown when appropriate.
Each live AppHost has an **Open Dashboard** action and **Stop AppHost...** action.
Menu items have no
hover tooltips, so they cannot cover the action submenu. Stopping opens a native
confirmation dialog showing the project name and PID, without the full project
path, with Cancel as the default. **Don't ask again** skips future Stop warnings
only after confirming Stop. Canceling, or discovering that the selected process
has been replaced, does not save that preference. Exact process identity is still
validated when the warning is disabled. Stop uses the system's standard adaptive
button text rather than low-contrast red text on a gray button.
Long names are shortened in the middle, preserving both ends. Native subtitles
require macOS 14.4; older systems use a single-line layout with the same details.
**Quit Aspire** (Command-Q while using the menu) removes the icon and stops its
own discovery subprocess, not any AppHost.

AppHost health uses distinct native SF Symbols rather than color-only circles.
Healthy, waiting/degraded, unhealthy, unknown, and stopped states are distinguishable
without color. Updates change the existing row's icon even while the menu is open.
The menu-bar connection badge indicates AppHost presence, not aggregate health.

**Pin AppHost** keeps a project in the main list after it stops. **Unpin AppHost**
removes that preference. Both are available in the AppHost action submenu and
through a right-click context action. An offline pin has a neutral icon and an
explicit **Start AppHost** action. Merely opening an AppHost submenu never starts it.
Missing pinned projects are automatically removed from saved state rather than
kept as broken entries. A discovery disconnection alone does not remove pins.

**Open Recent** lists previously observed projects that are neither running nor
already pinned in the main list. Each entry offers explicit Start, Pin, and folder
actions. Missing project files prompt for removal when a file-dependent action is
requested. **Clear Recently Opened...** asks for confirmation with Cancel as the
default, warns that clearing is irreversible, and preserves pinned projects.
History stores only project paths and preferences, never process identities or
dashboard login URLs, in the tray's per-user settings directory.

**Open In** resolves installed folder-capable applications using macOS Launch
Services, rather than assuming applications live in `/Applications`. Supported
editors/terminals include VS Code, VS Code Insiders, Terminal, Ghostty, iTerm,
Rider, and Xcode; only installed applications appear. The selected app opens the
AppHost's directory without starting the AppHost. **Show in Finder** reveals the
project file. **Copy Path**, directly below it, copies the AppHost's containing folder path
without requiring the file to exist. These actions are available for running,
pinned, and recent AppHosts. This intentionally uses a tray-owned menu: Finder's Services menu
requires a document selection/responder contract that a status-menu row does not
provide. **Documentation** opens [aspire.dev](https://aspire.dev). **Settings...**
replaces the top-level About action and opens General and About information,
including the same version and build text on both platforms. Documentation remains in the tray menu, and a divider
separates **Quit Aspire** from the other utility actions. The Settings shortcut is Command-comma
on macOS or Control-comma on Windows while the tray's menu or window has focus;
it is not a system-wide hotkey.
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
`~/.aspire/tray/runtime/instance.lock`; the empty file
remains after exit, but its lock is released automatically. NativeAOT's Unix
named mutex implementation does not provide cross-process exclusion.
Before opening the lock, the state directory's owner-only permissions are
enforced even when the directory already exists.

The publish target creates an accessory `.app` with `LSUIElement`, so it has no
Dock icon. Local and GitHub builds use an ad-hoc hardened-runtime signature.
The official pipeline signs/notarizes the whole app before copying it into the
payload and embeds it without republishing. That official signing path still
requires release-pipeline validation; this remains a draft feedback POC.

## Recent history configuration

Recent history defaults to **10 AppHosts**. Configure it in the existing per-user
Aspire configuration file, normally `~/.aspire/aspire.config.json`, rather than in a project's
configuration or the tray Settings dialog:

```json
{
  "tray": {
    "recentAppHostLimit": 10
  }
}
```

The existing configuration command can set the same preference:

```sh
aspire config set tray.recentAppHostLimit 10 --global
```

The tray honors the CLI's Aspire home/install routing, including `ASPIRE_HOME`,
so a custom Aspire home uses its own `aspire.config.json`.

The limit accepts integers from **0 to 50**. Zero disables history; reducing the
limit keeps the most recent entries and preserves pins. Restart Aspire Tray after
editing the configuration. An invalid value is reported rather than silently
replaced by the default.

The **Don't ask again** preference is stored as `confirmStop: false` in the tray's
per-user `~/.aspire/tray/apphosts.json` (`%USERPROFILE%\.aspire\tray\apphosts.json`
on Windows), separately from sign-in registration. Like the CLI's global
configuration, this uses the installation's Aspire home when defined, otherwise
`ASPIRE_HOME` when set, then `~/.aspire`. The filename is `tray/apphosts.json`
under that home, outside the versioned bundle cache. To restore the
warning, quit the tray, set `confirmStop` to `true` (or remove that property), then
restart it. History, pins, and Stop-confirmation preferences from the former OS
application-data location are imported when the new file does not exist. The old
file is left intact; an existing new file always takes precedence. Quit before editing that state
file: the tray refuses to overwrite changes made by another writer while running.

## Launch at sign-in

The General section in **Settings...** offers **Launch Aspire Tray when I sign in**.
Normally, only the checkbox is shown. When unavailable, the optional note reads
"Launch at sign-in requires a stable native CLI installation." This checks
installation metadata, executable placement, and native executable shape, not
Authenticode or a macOS signing trust chain. Read/write failures
still show their error messages; Windows also shows **Refresh startup status** in
that state. Reopening Settings re-reads the registration on either platform. Availability
depends on a supported, verified native CLI installation, not just executable signing.
It is off by default. Opening Settings, reading the preference, and closing the
dialog never register startup or launch anything. Enabling it starts only the
companion at the next sign-in; it does not start pinned or recent AppHosts.
**Quit Aspire** keeps the companion closed until the next sign-in or an explicit
manual start.

Startup invokes the selected CLI installation's stable entry point, which then
extracts and launches its current tray payload. It does not point directly at an
extracted version directory that CLI cleanup can remove. Managed development and
other launch modes without a verified startup entry point cannot enable startup;
Settings explains that limitation. Currently, startup supports script, PR, and
localhive installations whose installation metadata identifies a stable `bin/`
entry point. Package-manager installations and installations with missing or
unrecognized metadata remain available for manual tray startup, but cannot enable
launch at sign-in. Removing or moving the selected CLI installation can break its
startup registration.

Registration is per user and does not require administrator privileges. The
dialog reads the registration rather than a separate, potentially stale enabled
flag. Operating-system startup/background-app policy can still override that
registration. Read or write failures are shown explicitly without presenting a
failed change as successfully saved. Native smoke uses an in-memory preference,
so its checkbox never changes the real account's startup registration.

On macOS, the setting manages
`~/Library/LaunchAgents/dev.aspire.tray.login.plist`. The agent invokes
`aspire tray start --non-interactive --nologo` once at the next sign-in, without
KeepAlive or immediately starting a launchd job when the checkbox changes.
On Windows, the `AspireTray` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` invokes an owned GUI bootstrap
at `%LocalAppData%\Aspire\Tray\Startup\aspire-tray-login.exe`. This bootstrap calls
the installed CLI without displaying a console and exits after startup completes.
Disabling removes the registration; the idle bootstrap can remain on disk.
Unrelated or externally modified registrations are left unchanged and reported
in Settings.

## Windows companion

Windows uses the original colored `src/Shared/Aspire.ico`, not the macOS wave
artwork. Its notification icon has no badge when discovery is connected but idle,
a purple badge for active AppHosts, a gray dash while connecting, and an amber
exclamation mark when discovery is unavailable. A stale AppHost list no longer
looks like a working connection. The AppHost menus use the same shared health, history, pinning,
explicit Start, and exact-instance Stop behavior described above. AppHost rows have small
status circles: green for healthy resources, orange for waiting/degraded or transitional
states and unavailable discovery, and red for unhealthy resources or action errors.
Stopped AppHosts and unknown health use a neutral gray circle. Other menu items,
including Documentation and Open Dashboard, remain text-only.
AppHost names are limited to 44 text elements with a middle ellipsis and no appended
status text. Each AppHost submenu ends with a divider followed by a disabled entry
containing its path and status or directory/PID details together on one line.
This entire entry is limited to 45 text elements with a middle ellipsis. Hovering
over this final entry shows its full, untruncated value in a native tooltip, which
can wrap long text. The tooltip's final native window bounds are constrained to
the monitor work area without moving keyboard focus out of the menu.
Parent AppHost rows and action items have no tooltips.
Displayed and copied paths retain their original casing;
Windows identity matching remains case-insensitive and still requires the exact PID
and process start time. **Copy Path** copies the complete original containing folder path. The final
details entry and its tooltip refresh when status changes, including while the menu is open.
Only the notification-area icon and window icons use artwork.

The Windows adapter uses native popup menus and confirmation dialogs.
Its embedded Common Controls v6 manifest enables Windows visual styles in both
managed and NativeAOT builds. Settings uses a large heading, rounded General and About
cards, a soft light background, and native buttons and checkboxes. High-contrast mode
uses the system palette. Informational labels wrap instead of using editable-looking
scroll panes. The layout expands for startup details and errors, reflows after DPI changes,
and scrolls when needed to keep all actions reachable on shorter displays. This appearance
uses Win32 and GDI only, without a Windows App SDK runtime dependency.
**Show in Explorer** (the Windows equivalent of **Show in Finder**), **Copy Path**,
and **Open In** act on the AppHost's source location without starting it.
A divider separates these file actions from **Pin**/**Unpin**.
Missing pinned projects are pruned, while missing recent projects
offer removal and clearing history requires confirmation. The adapter retains
native menu resources while a popup is being tracked, restores the notification
icon after Explorer restarts, and responds to display scaling changes.
If Explorer's notification area is still initializing at startup, icon creation
retries on the UI timer using the bounded Explorer recovery attempts. Startup is acknowledged only after
the icon has been added; an unavailable notification area still fails explicitly.

Preferences are stored in `<Aspire home>\tray\apphosts.json`, normally
`%USERPROFILE%\.aspire\tray\apphosts.json`.
Timestamped diagnostics remain in `%LocalAppData%\Aspire\Tray\aspire-tray.log`.
Windows opens and validates every directory component without following reparse
points, blocking ancestor replacement and reparse-point changes until the log handle is open.
The log is a single-link regular file opened for append-only writes, and that
validated handle is retained rather than reopening the pathname.
The current-user
secured mutex and named pipe are shared across that user's desktop sessions.
The icon stays in the session where the companion first started; another session
restores or stops that instance. Stop and start again to move it to the launching
session.

Build on Windows with PowerShell 7.4 or later, the repository-pinned SDK, Visual Studio's **Desktop
development with C++** tools, and a compatible Windows SDK installed. NativeAOT
publishing requires a Windows host; a managed cross-platform build is not a
substitute:

```powershell
.\restore.cmd -projects .\src\Aspire.Tray\Windows\Aspire.Tray.Windows.csproj
dotnet publish .\src\Aspire.Tray\Windows\Aspire.Tray.Windows.csproj -c Release -r win-x64
```

The publishing helper also validates the original icon and native PE architecture:

```powershell
.\src\Aspire.Tray\Windows\publish.ps1 -Architecture x64
# After publishing, run isolated native assertions against the existing output:
.\src\Aspire.Tray\Windows\publish.ps1 -Architecture x64 -SkipPublish `
    -CliPath C:\absolute\path\to\aspire.exe -SmokeSeconds 30
```

Smoke requires an interactive Windows desktop with Explorer. It exercises real
menus, the final divided path/status entry (45-character limit, ordering, and disabled state),
full-value details tooltips (native hover selection, live/stale refresh, focus, and dismissal),
safe-default dialogs including the native Stop suppression checkbox,
Cancel/stale-instance suppression safeguards, subsequent prompt skipping,
exact path copying (including missing files and Unicode), immutable actions,
pin/history operations, status circles, text-only actions, bounded original-case paths and compact names,
distinct notification-area connection artwork, delayed initial icon creation, Explorer recovery, activation,
single-button message dismissal, and artwork invalidation.
Synthetic invalidation is not a real monitor-DPI transition; verify display-scale
changes separately on the desktop. Smoke uses only isolated fake AppHosts and
captures output under `artifacts/log/Release/tray-win-x64`.
CI checks for an interactive session, Explorer notification area, and an accessible
input desktop before running native UI smoke. Runners without those capabilities
emit an explicit warning and summary instead of claiming UI coverage; native
publishing and payload verification still run.
On desktop-capable runners, an SDK-compiled stock-icon registration control logs
the native structure layout and process/Explorer sessions and integrity levels.
The native tray smoke also tries the same stock icon through its C# interop before
exercising the real artwork. These diagnostics distinguish layout, artwork, and
runner-shell failures; registration rejection never changes the desktop gate or
replaces the mandatory native smoke.

Use `win-arm64` and the matching native C++ toolchain for ARM64. The CLI bundle
includes the published executable and its adjacent `Aspire.ico`; copying only
the executable omits required notification artwork. Use `aspire tray start` and
`aspire tray stop` from the native Windows CLI bundle for the normal lifetime and
bundle-lease handoff. A foreground development run can use an explicit absolute
`--cli` path, including a locally built managed CLI when its .NET runtime is
installed.

## Architecture and boundaries

- One global companion per OS user, independent of the launch directory.
- One long-lived child process:
  `aspire ps --follow --format json --output snapshot --non-interactive --nologo`.
- `IAppHostClient` is the only backend interface: watch snapshots, explicitly start
  a project, and stop an exact instance. `CliAppHostClient` owns subprocesses and protocol validation.
  No shell, per-AppHost watchers, or direct backchannel access from the tray.
- `TrayController` owns application state and independent per-AppHost operations.
  Its immutable view snapshots contain no AppKit handles or selectors.
  `AppHostPresentation` handles names, truncation, and directory context.
- The platform adapters own native menus, callbacks, confirmation, browser launch,
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
- Saved paths are separate from live process identities. Offline pins never
  reuse an old PID, and Start is disabled when discovery cannot rule out an
  already-running project. History/pinning and start state are shared,
  platform-independent code used by both native frontends.

The snapshot and experimental stop wire contracts are defined once in
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

Restarting AppHosts, resource details, search, and automatic tray
upgrade handoff are outside this POC. The companion's own projects remain
workload-free and isolated from the product's managed build configuration, but
the macOS and Windows native bundles build and ship them.

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
Quit. It also checks connection badges, live health transitions, context pinning,
offline pins, explicit Start, filtered recents, and safe clear/missing-file dialogs.
Scenarios include empty/disconnected discovery, missing dashboards,
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

For a manual inspection, use `--smoke-seconds 120 --smoke-interactive`
(or set `ASPIRE_TRAY_SMOKE_INTERACTIVE=1` with `--smoke-seconds 120`).
After the automated checks pass, the harness leaves
fake running AppHosts with healthy/waiting/failed icons and a stopped, pinned
example with a neutral icon. It enables real native
confirmation dialogs until **Quit Aspire**. The watchdog still bounds the automated
checks but is removed when the interactive preview is ready. A Settings window
identifies the preview so it is easy to find. History remains isolated from
the normal tray. Copy Path uses the real clipboard in the interactive preview;
automated checks substitute a clipboard sink to preserve existing clipboard data.
Its Start/Stop backend remains fake; explicit Open In actions
can open the temporary fixture folder in a real installed application.
An isolated preview `.app` can set `ASPIRE_TRAY_SMOKE_INTERACTIVE=1` and
`ASPIRE_TRAY_SMOKE_CLI=<absolute CLI path>` in its `LSEnvironment` to support
Launch Services opening it without arguments. Normal product bundles do not set
these variables.

Platform-independent tests exercise protocol framing and validation, subprocess
cleanup, reconnects, lifetime replacement, concurrent stops, and controller
shutdown without AppKit or running AppHosts. Run native smoke on the target
platform and architecture: a cross-platform managed build does not validate
AppKit execution or Windows NativeAOT publishing. Native smoke uses fake AppHosts
and does not replace live CLI connectivity, real monitor-DPI transitions, or
official signing/notarization validation.
