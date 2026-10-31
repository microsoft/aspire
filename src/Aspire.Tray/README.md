# Aspire Tray

An experimental **C# NativeAOT** companion for the Aspire CLI, bundled on macOS and Windows.
It uses a native AppKit status item on macOS and a Win32 notification icon on Windows
through small platform interop layers. Activating the icon opens a flyout panel, the
tray's only UI, drawn the same way on both platforms. It does not require a Swift build
step, a WebView, a macOS .NET workload, Windows Forms, or an installed .NET runtime
on the machine running the published app.

## Project layout

- `src/Aspire.Tray/Common/`: shared protocol client, controller, and lifecycle code, plus the
  flyout panel's layout, glyphs, palettes, and interaction (`TrayPanel*.cs`).
- `src/Aspire.Tray/Mac/`: native AppKit frontend and macOS app packaging.
- `src/Aspire.Tray/Windows/`: native Win32 frontend and Windows publishing.
- `tests/Aspire.Tray.Tests/`: protocol, controller, lifecycle, and panel layout tests in the normal
  test matrix, and an SVG renderer of the panel for previews.

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

On Windows, the helper uses the same-user desktop shell as the GUI's parent
instead of inheriting CLI or terminal job lifetimes. System job membership alone
does not prevent startup. A running desktop shell owned by the invoking user is
required; starting the tray with **Run as different user** is not supported.

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
Quit the tray using its original CLI or the panel's **Quit** (**Exit** on Windows) before upgrading from an
earlier preview. The state directory has been renamed; a still-running older
preview uses a separate control endpoint and must be stopped before starting this build.

## Flyout panel

Every activation of the tray icon opens a flyout panel, similar to OneDrive's: a left or
right click, a Control-click on macOS, Enter or Space on the focused icon on Windows, and
VoiceOver's press on macOS. Opening it from the keyboard or a screen reader also shows the
keyboard focus on its first control. There are no native menus. The panel is the
same on macOS and Windows: a header bar, as tall and colored as the footer, with the
Aspire logo, the title, and a one-line status
("3 running · 1 needs attention", "Connecting…") behind a colored connection dot, an
optional banner for real problems (storage, discovery, or a failed action), the AppHost
list, and a footer with three icon buttons on the right: **Settings** (a gear),
**Documentation** (a globe) and **Quit** (**Exit** on Windows, following each platform's
convention). Quit closes only the tray: its tooltip and accessibility description say
"Quit the tray. AppHosts keep running." Every icon-only button, in the footer and on the
rows, shows a native tooltip naming its action.

The list has **Running**, **Pinned**, and **Recent** sections. Recent is collapsed while
other AppHosts are listed, and its header has a **Clear** button. Each row shows a
status glyph, the AppHost name, and `{status} · {parent/folder}`; the PID is added only
when two running AppHosts share a name. Status glyphs differ by shape as well as color:
a check (healthy), an exclamation mark (needs attention), a cross (unhealthy), a question
mark (unknown), an outlined stop (stopped), and a ring (starting or stopping).
Hovering or focusing a row shows its buttons: **Open dashboard**, **Stop**, and **More**
for running AppHosts, **Start** and **More** for offline ones. **More** opens the row's
action list in the panel, under the row: **Open in**, which shows the supported installed
applications under it, each with its own icon (such as Visual Studio Code, Terminal, Ghostty
or Xcode on macOS, and Visual Studio Code, Windows Terminal or PowerShell on Windows; an
application whose icon can't be read gets a generic glyph), **Show in Finder** or **Show in File Explorer**,
**Copy path** (the folder that contains the AppHost), **Pin** or **Unpin**, and **Remove
from recent** for a recent AppHost. It is drawn by the shared scene like the rest of the
panel, so it looks the same on both platforms and screen readers see its items as buttons.
Clicking **More** again, picking an item, or acting on anything else closes it.
Clicking a row itself does nothing, whether the AppHost runs or not; only its buttons act.
The list scrolls when it is taller than the screen allows; the header and footer stay in place.
The keyboard works too: Up/Down and Home/End move between rows and action list items, Tab
and Shift-Tab also reach the row buttons, Enter or Space activates, and Escape steps back
out of the Open in applications, then the action list, then closes the panel. Command-comma on macOS or Ctrl+comma on Windows opens Settings,
and Command-Q quits on macOS. Clicking elsewhere closes the panel.

**Stop** stops the AppHost right away, without asking, like the dashboard. Clicking a
row that is starting or stopping does nothing, and its dashboard can't be opened while
it stops. The panel asks for confirmation inline, instead of opening dialogs, only for
what can't be undone: **Clear** turns the Recent header into "Clear recent AppHosts?
Pinned AppHosts are kept.", and **Start**, **Open in** or **Show in** on a recent AppHost
whose source file is gone offers to remove it. Focus starts on **Cancel**, Escape cancels,
and clicking anything else abandons the confirmation. The strip goes away by itself if its AppHost goes away
or the recent list is cleared elsewhere.

The gear opens a **Settings** view inside the panel, with a back arrow: a **Launch at
sign-in** toggle (disabled with an explanation when unavailable, and showing read or
write failures under it), and the version and About text. Escape or the
back arrow returns to the list.

Everything except drawing is shared code in `Common/`: `TrayPanel` turns the view state
and interaction state into a display list (paths, text boxes, the logo, clips) and hit
targets in device-independent pixels. All glyphs are vector paths defined there. The
macOS host draws the list with `NSBezierPath` and `NSAttributedString` in a borderless
`NSPanel`; the Windows host draws shapes with the GDI+ flat API and text with GDI
`DrawTextW` (ClearType) in a `WS_POPUP` window, rounded by DWM on Windows 11 or by a
window region on Windows 10. Text uses the system font at the same sizes (SF on
macOS, Segoe UI Variable Text or Segoe UI on Windows); layout never measures text, and
each renderer truncates it to its box. The panel follows the light or dark appearance,
the macOS **Increase contrast** setting, and the Windows high-contrast theme.

Everything lines up on one grid, defined by the constants at the top of `TrayPanel`:
- **Columns:** the leading column is centered at x 30 (the logo, status glyphs, icons and
  the Settings back arrow), and text starts at 52. Nested items' labels start at 78. The
  trailing column mirrors the leading one, with buttons centered at 330, 300 and 270.
  Row highlights and cards span 8 to 352.
- **Lines:** each kind of line has one vertical center. The header and footer bars are at
  22, a row's glyph and buttons at 26 with its name and detail at 17 and 35, section labels
  at 16, and action list items at 15.
- **Text placement:** single-line text sits on its line by its capitals. The scene gives each
  text a baseline half a cap height (0.70 em) below its line's center. Both renderers draw
  at that baseline, snapped to device pixels, rather than centering the font's line box,
  which lands differently in SF and Segoe UI. The header's status shares the title's baseline.

The scene also emits this grid as guides (`TrayPanelScene.Guides`): columns, line centers,
the shared baseline, and the box each glyph should fill. Layout tests check every sample state
against them, and the SVG previews are also written with the guides drawn over them
(`*-guides.svg`).

Screen readers see the panel as native elements. Along with the display list, `TrayPanel`
produces an accessibility list in reading order: buttons (rows, row buttons, action list
items, Back, the confirmation and footer buttons), switches with an on/off state (Launch at sign-in, with the
setting's description as help text), and read-only text (the header status, the notice, section titles, the
confirmation question, About). On macOS the panel view is an `AXGroup` whose children
are `NSAccessibilityElement` objects (`AXButton`, `AXCheckBox` with the `AXSwitch`
subrole, `AXStaticText`); on Windows the panel window answers `WM_GETOBJECT` with a UI
Automation fragment root whose children are Button (Invoke pattern), CheckBox (Toggle
pattern) and Text elements, implemented with source-generated COM so NativeAOT needs no
built-in COM interop. A press, invoke or toggle runs through `TrayPanel` exactly like a
click, and setting focus on an element moves the panel's keyboard focus (scrolling it
into view). Each host reports keyboard focus changes, elements appearing or going away
(a confirmation strip, an action list, the Settings view) and toggled values.

To preview the panel without running the tray, run the panel tests with
`ASPIRE_TRAY_PANEL_PREVIEW_DIR` set. They write SVG images of the light and dark
themes for several states (typical, problems, empty, connecting, disconnected, many
rows, the two inline confirmations, action lists, and the Settings view), drawn from the same
display list:

```sh
ASPIRE_TRAY_PANEL_PREVIEW_DIR="$PWD/artifacts/tray-panel" \
    dotnet test --project tests/Aspire.Tray.Tests/Aspire.Tray.Tests.csproj \
    --no-launch-profile -- --filter-class "*.TrayPanelTests"
```

## Tray icon status

Both platforms follow the same rules (`Common/TrayIconStatus.cs`):

| State | Icon |
| --- | --- |
| No AppHosts running | The logo alone. |
| AppHosts running or starting | The logo with a green dot. |
| Something needs attention | The logo with an amber dot: an unhealthy or degraded resource, a failed start or stop, or a storage or pin error, while AppHosts run. |
| Connecting to discovery | The logo dimmed, no dot. |
| Discovery unavailable or incompatible | The logo dimmed, no dot. The tooltip and the panel's banner explain. |

The dot sits in the lower-right corner, in a transparent cut-out of the logo, so it
stays readable on any menu bar, wallpaper tint or taskbar color. On macOS the logo is a
template image and the dot is a small view over it, colored with the system green and
orange; the dimmed state is AppKit's own disabled status-item rendering. The tooltip and
accessibility label state the status in words, so the dot's color is never the only cue.

## Publish the standalone tray

The public `PublishTray` target in `eng/Bundle.proj` publishes only the tray,
without assembling a CLI bundle. The macOS `publish.sh` helper below uses this
target; it can also be called directly:

```sh
dotnet msbuild eng/Bundle.proj -t:PublishTray -p:TargetRid=osx-arm64 -p:Configuration=Release
```

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

The helper publishes the tray without rebuilding unrelated bundle components.
A complete CLI bundle embeds the prepared app alongside the other components.
The app is verified from the actual payload archive so a valid source app alone
is not mistaken for a correctly packaged app.

See [the bundle build overview](../../docs/specs/bundle.md#build-process) for
bundle configuration and component-reuse options.

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

The menu bar shows the same Aspire logo as Windows (`src/Shared/Aspire_icon_256.png`),
converted at startup to a one-color AppKit template image, as Apple recommends for
menu bar extras. Darker parts of the logo (its outline) become opaque and lighter facets
partly transparent, so the logo keeps its inner structure in the menu bar's own color.
See [Tray icon status](#tray-icon-status) for the status dot. Any click on it opens the
[flyout panel](#flyout-panel), which hangs below the item, or from the top-right corner of
the screen when the item is hidden behind the camera housing or off a crowded menu bar.

**Stop** stops an AppHost without a confirmation, as in the dashboard; the exact process
identity is still validated, so a replaced process is never stopped by mistake.
**Quit** (or Command-Q while the panel is open) removes the icon and stops the tray's
own discovery subprocess, not any AppHost.

**Pin** keeps a project in the Pinned section after it stops, and **Unpin** removes that
preference; both are in the row's action list. An offline pin has an explicit **Start**
button. Merely opening a row's action list never starts it.
After the CLI reports a successful or timed-out start, the tray waits up to 60 seconds
for discovery to observe the AppHost. If it never appears, the tray shows an unconfirmed-start
warning and allows an explicit retry while discovery is connected and the path is not listed.
The AppHost may still start late, so check before retrying; the tray never retries automatically.
Missing pinned projects are automatically removed from saved state rather than
kept as broken entries. A discovery disconnection alone does not remove pins.

The **Recent** section lists previously observed projects that are neither running nor
already pinned. Each entry offers explicit Start, Pin, and folder actions. A missing
project file is offered for removal inline when a file-dependent action is requested.
**Clear** asks inline, with focus on Cancel, and preserves pinned projects.
History stores only project paths and preferences, never process identities or
dashboard login URLs, in the tray's per-user settings directory.

**Open in** resolves installed folder-capable applications using macOS Launch
Services, rather than assuming applications live in `/Applications`. Supported
editors/terminals include VS Code, VS Code Insiders, Terminal, Ghostty, iTerm,
Rider, and Xcode; only installed applications appear. The selected app opens the
AppHost's directory without starting the AppHost. **Show in Finder** reveals the
project file. **Copy path** copies the AppHost's containing folder path without
requiring the file to exist. These actions are available for running, pinned, and
recent AppHosts. The tray keeps its own list of applications because Finder's Services
menu requires a document selection/responder contract that a panel row does not
provide. **Documentation** opens [aspire.dev](https://aspire.dev). The gear opens the
Settings view, with launch at sign-in and About information, including the same version
and build text on both platforms. Command-comma opens it while the panel has focus; it is
not a system-wide hotkey.
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
Single-instance ownership uses an OS file lock under
`~/.aspire/tray/runtime/instance.lock`; the empty file
remains after exit, but its lock is released automatically. NativeAOT's Unix
named mutex implementation does not provide cross-process exclusion.
Before opening the lock, the state directory's owner-only permissions are
enforced even when the directory already exists.

The publish target creates an accessory `.app` with `LSUIElement`, so it has no
Dock icon. Local and GitHub builds use an ad-hoc hardened-runtime signature.
The official pipeline submits a ZIP containing the complete `.app` directory
directly to MicroBuild for hardened Developer ID signing and Apple notarization,
then restores the app and staples its notarization ticket. This avoids Arcade's
extra wrapping of macOS signing inputs. The signed app is copied into the
payload and embedded without republishing. Signing-service changes must be
validated in the official native pipeline; local and GitHub builds cannot
validate that integration because they use ad-hoc signatures.

## Recent history configuration

Recent history defaults to **10 AppHosts**. Configure it in the existing per-user
Aspire configuration file, normally `~/.aspire/aspire.config.json`, rather than in a project's
configuration or the tray's Settings view:

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

History and pins are stored in the tray's
per-user `~/.aspire/tray/apphosts.json` (`%USERPROFILE%\.aspire\tray\apphosts.json`
on Windows), separately from sign-in registration. Like the CLI's global
configuration, this uses the installation's Aspire home when defined, otherwise
`ASPIRE_HOME` when set, then `~/.aspire`. The filename is `tray/apphosts.json`
under that home, outside the versioned bundle cache. Files written by earlier
builds may also hold a `confirmStop` preference from when stopping asked first; it
is read, ignored, and dropped the next time the file is saved. History and pins from
the former OS application-data location are imported when the new file does not exist. The old
file is left intact; an existing new file always takes precedence. Quit before editing that state
file: the tray refuses to overwrite changes made by another writer while running.

## Launch at sign-in

The panel's Settings view offers a **Launch at sign-in** switch. Normally, the switch
is shown with a short description. When unavailable, the switch is disabled and the
description reads "Launch at sign-in requires a stable native CLI installation." This
checks installation metadata, executable placement, and native executable shape, not
Authenticode or a macOS signing trust chain. Read/write failures show their error
messages under the switch. Reopening Settings re-reads the registration on either
platform. Availability depends on a supported, verified native CLI installation, not just
executable signing. It is off by default. Opening Settings, reading the preference, and
leaving it never register startup or launch anything. Enabling it starts only the
companion at the next sign-in; it does not start pinned or recent AppHosts.
**Quit** (**Exit** on Windows) keeps the companion closed until the next sign-in or an explicit
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
Settings view reads the registration rather than a separate, potentially stale enabled
flag. Operating-system startup/background-app policy can still override that
registration. Read or write failures are shown explicitly without presenting a
failed change as successfully saved. Native smoke uses an in-memory preference,
so its switch never changes the real account's startup registration.

On macOS, the setting manages
`~/Library/LaunchAgents/dev.aspire.tray.login.plist`. The agent invokes
`aspire tray start --non-interactive --nologo` once at the next sign-in, without
KeepAlive or immediately starting a launchd job when the switch changes.
On Windows, the `AspireTray` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` invokes an owned GUI bootstrap
at `%LocalAppData%\Aspire\Tray\Startup\aspire-tray-login.exe`. This bootstrap calls
the installed CLI without displaying a console and exits after startup completes.
Disabling removes the registration; the idle bootstrap can remain on disk.
Unrelated or externally modified registrations are left unchanged and reported
in Settings.

## Windows companion

Windows uses the original colored `src/Shared/Aspire.ico` (the same logo as macOS, in color)
artwork, with the status dot described in [Tray icon status](#tray-icon-status).
The dot's green and amber are picked for the taskbar's light or dark theme. A left or right
click, the context menu key, or Enter or Space on the focused icon opens the
[flyout panel](#flyout-panel); keyboard selection also shows the panel's keyboard focus.
Ctrl+comma opens Settings while the panel has focus. The panel uses the same shared
health, history, pinning, explicit Start, and exact-instance Stop behavior described above.
Displayed and copied paths retain their original casing; Windows identity matching remains
case-insensitive and still requires the exact PID and process start time. **Copy path**
copies the complete original containing folder path. **Show in File Explorer** (the
Windows equivalent of **Show in Finder**) and **Open in** (Visual Studio Code, Rider,
Sublime Text, Windows Terminal or PowerShell, when installed) act on the AppHost's source location without
starting it. Missing pinned projects are pruned, while missing recent projects offer
removal inline. An action that fails shows its error in a message box. The adapter
restores the notification icon after Explorer restarts and responds to display scaling
changes. Its embedded Common Controls v6 manifest gives that message box Windows visual
styles in both managed and NativeAOT builds.
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

Smoke requires an interactive Windows desktop with Explorer. It drives the real flyout
through its window procedure: every icon notification (NIN_SELECT, WM_CONTEXTMENU,
NIN_KEYSELECT with keyboard focus) opening it, Ctrl+comma, the Settings view with
isolated startup preferences and their failure states, panel rows matching the view state,
the action list (items, keyboard and UI Automation Invoke), Stop without a dialog and
stale-instance safeguards, inline Clear and missing-file removal, exact path copying
(including missing files and Unicode), pin/history operations, distinct notification-area
connection artwork, delayed initial icon creation, Explorer recovery, activation,
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
- `TrayPanel` owns all UI layout and interaction, including inline confirmations and row
  action lists. The platform adapters own the status item or notification icon, the panel
  window and its drawing, accessibility, callbacks, browser and application launch, and the
  main-thread event loop. There is no generic UI framework, MVVM/DI
  container, reflection-based binding, or additional UI runtime.
- UI changes are posted to the native main thread and coalesced, not polled.
  While a click is in progress, the panel defers a new state until the button is
  released, so rows cannot move under the pointer.
- Actions carry path/PID/process-lifetime identity, not indices into a changing
  list. Discovery revalidates that identity before each action runs.
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
- Stop actions revalidate the selection, then invoke
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
AppKit objects and callbacks, and drives the flyout panel through the production paths:
clicks through the pointer handlers, presses the way VoiceOver does (AXPress), and actions
through the panel host. It checks that every activation of the status item opens the panel
(left and right clicks, keyboard and VoiceOver activation with keyboard focus, and the
button's own click) and that no menu is ever attached, Command-comma, the panel rows against
the view state, the row action list (items, keyboard, VoiceOver), connection badges and
accessibility labels, live health transitions, Stop without a confirmation by click and
AXPress, a stale action after process replacement, pin and unpin from the action list,
offline pins, explicit Start, Copy path (including Unicode and an injected clipboard
failure), inline Clear and missing-file removal, missing-pin pruning, Documentation, and
hidden-icon restoration. Scenarios include empty, connecting and disconnected discovery,
missing dashboards, and independent pending stops.
It also checks that the window matches the shared layout and that Escape closes it, that
the gear opens the Settings view and its launch-at-sign-in toggle round-trips through the
in-memory store, and that the window resizes with each view. Set
`ASPIRE_TRAY_SMOKE_PANEL_CAPTURE_DIR` to an absolute directory to also write PNGs
of the panel rendered by AppKit (light, dark, dark with hover, an open action list in light
and dark, and the Settings view in light and dark). In the list, an open action list and
the Settings view it also reads the panel's accessibility tree back through the
NSAccessibility getters VoiceOver uses (roles, names, switch values, screen frames and
the focused element) and presses a Settings switch the way VoiceOver does.
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
fake running AppHosts with healthy/waiting/failed states and a stopped, pinned
example, and opens the panel; use its **Quit** (**Exit** on Windows) button to close the preview. The watchdog
still bounds the automated checks but is removed when the interactive preview is ready.
The Settings view's About text identifies the preview. History remains isolated from
the normal tray. Copy Path uses the real clipboard in the interactive preview;
automated checks substitute a clipboard sink to preserve existing clipboard data.
Its Start/Stop backend remains fake; explicit Open In actions
can open the temporary fixture folder in a real installed application.
An isolated preview `.app` can set `ASPIRE_TRAY_SMOKE_INTERACTIVE=1` and
`ASPIRE_TRAY_SMOKE_CLI=<absolute CLI path>` in its `LSEnvironment` to support
Launch Services opening it without arguments. Normal product bundles do not set
these variables.

To check alignment on a real screen, the interactive preview takes three more variables:
- `ASPIRE_TRAY_SMOKE_PREVIEW_APPEARANCE`: `light` or `dark`.
- `ASPIRE_TRAY_SMOKE_PREVIEW_STATE`: `hover` (shows the first row's buttons), `actions`
  (opens its action list) or `settings`.
- `ASPIRE_TRAY_SMOKE_PREVIEW_EXPORT`: a JSON path. The preview writes there what the scene
  draws and the guides it intends. On macOS the export names the window, for
  `screencapture -o -l <windowId>`. On Windows the tray also saves the composited screen under
  the panel as PNG next to the JSON.

Run the Windows preview from the interactive desktop (Windows App or the console), not over
SSH, since an SSH session has no desktop to show or capture. Drawing the guides over a
screenshot at its scale shows where each element actually landed.

Platform-independent tests exercise protocol framing and validation, subprocess
cleanup, reconnects, lifetime replacement, concurrent stops, and controller
shutdown without AppKit or running AppHosts. Run native smoke on the target
platform and architecture: a cross-platform managed build does not validate
AppKit execution or Windows NativeAOT publishing. Native smoke uses fake AppHosts
and does not replace live CLI connectivity, real monitor-DPI transitions, or
official signing/notarization validation.
