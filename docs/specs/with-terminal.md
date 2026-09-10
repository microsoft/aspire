# `WithTerminal()` — Aspire interactive terminal architecture

**Status:** Implemented for Aspire 13.4 (Windows executables).
**Issue:** [microsoft/aspire#16317](https://github.com/microsoft/aspire/issues/16317)
**DCP integration:** [microsoft/dcp#133](https://github.com/microsoft/dcp/pull/133)

## Goal

Let an Aspire AppHost author opt any executable or container resource into
interactive terminal access:

```csharp
builder.AddProject<Projects.MyAgent>("agent")
    .WithReplicas(2)
    .WithTerminal();
```

The dashboard then renders a Hex1b web terminal per replica, and the CLI
exposes the same session as `aspire terminal agent --replica 0`.

## Process topology

```text
                                ┌────────────────────────────┐
                                │  AppHost (dotnet run)      │
                                │  - Aspire.Hosting          │
                                │  - DCP control plane       │
                                │  - per-replica:            │
                                │    Aspire.TerminalHost     │  (1 process per replica)
                                └─────────────┬──────────────┘
                                              │ spawn
                                              ▼
┌──────────────────────┐                ┌───────────────────────┐                ┌────────────────────────┐
│  DCP-launched        │   PTY          │  TerminalHost         │   HMP v1 UDS   │  Consumers             │
│  replica process     │ ─────────────▶ │  (Hex1b HMP v1 broker) │ ─────────────▶ │  - Dashboard          │
│  (executable, repl…) │ ◀───── stdin ─ │                       │ ◀───── input ─ │    /api/terminal proxy │
└──────────────────────┘                └───────────────────────┘                │  - aspire CLI          │
                                                                                  └────────────────────────┘
```

Three actors and three socket roles:

| Actor          | Socket              | Direction                        | Lifetime |
|----------------|---------------------|----------------------------------|----------|
| **DCP**        | `producerUdsPath`   | DCP → host (PTY bytes + control) | Per replica |
| **TerminalHost** | `consumerUdsPath` | host → consumers (broadcast)     | Per replica |
| **TerminalHost** | `controlUdsPath`  | AppHost → host (lifecycle, stats)| Per replica |

The producer/consumer split lets multiple consumers (dashboard + multiple CLI
sessions) attach simultaneously without coupling DCP to consumer counts.

## Wire protocol

We do **not** define a custom protocol. The terminal traffic uses
[Hex1b](https://github.com/dotnet/hex1b)'s `HMP v1` (Hex Multiplex Protocol,
version 1), which already handles:

- VT byte streaming with backpressure
- Resize requests in both directions
- Hello/StateSync replay so a late-attaching consumer sees the current
  scrollback
- Connection lifecycle (close, disconnect, reconnect)
- Authenticated stream factory hooks for Unix sockets and the AppHost's
  dashboard gRPC terminal stream

The terminal host uses `DcpUpstreamAdapter` for DCP's minimal single-peer
protocol and `Hmp1PresentationAdapter` for its consumer-facing listener.
The dashboard and CLI attach using `Hmp1WorkloadAdapter`. The dashboard
adds a per-browser `Hex1bTerminal` mirror with `Hwt1PresentationAdapter`;
the browser receives authoritative terminal state rather than parsing ANSI.

## Property contract (gRPC `ResourceService` snapshots)

When `WithTerminal()` is applied to a resource, every replica snapshot
emitted by the dashboard service carries four properties:

| Key                       | Sensitivity     | Meaning                                      |
|---------------------------|-----------------|----------------------------------------------|
| `terminal.enabled`        | non-sensitive   | Marker. `"true"` when the replica has a PTY. |
| `terminal.replicaIndex`   | non-sensitive   | 0-based stable index from `DcpInstancesAnnotation`. |
| `terminal.replicaCount`   | non-sensitive   | Total replicas for the parent resource.      |
| `terminal.consumerUdsPath`| **sensitive**   | The local UDS that consumers connect to.     |

The consumer UDS path is marked `IsSensitive=true` so the dashboard UI masks
the value in the property list. The path still rides the gRPC stream because
the dashboard's WebSocket proxy needs it server-side to resolve
`?resource=&replica=` query parameters into a real socket; the path is never
echoed back to the browser.

## Dashboard `/api/terminal` WebSocket endpoint

Authenticated (`RequireAuthorization(FrontendAuthorizationDefaults.PolicyName)`)
endpoint at `/api/terminal?resource=<displayName>&replica=<index>`.

`TerminalWebSocketProxy` resolves the connection entirely server-side:

1. The same-origin WebSocket gate rejects missing or cross-origin `Origin`
   headers before resolving a resource, in addition to frontend authorization.
2. `ITerminalConnectionResolver.ConnectAsync(resourceName, replicaIndex, ct)`
   walks `IDashboardClient.GetResources()`, matches by `DisplayName` +
   `TryGetTerminalReplicaInfo`, and connects via
   `Hmp1Transports.ConnectUnixSocket(consumerUdsPath, ct)`.
3. The handler connects a public `Hmp1WorkloadAdapter`, attaches a per-view
   terminal and `Hwt1PresentationAdapter`, and runs the two transport pumps.
   Incoming UTF-8 JSON messages are reassembled up to 64 KiB and passed to
   `HandleMessageAsync`. Each `ReadFrameAsync` result is sent as one complete
   binary WebSocket message, without dropping or reordering frames.
4. Hex1b owns input encoding, primary-role negotiation, selection, history,
   graphics projection, acknowledgements and state resynchronization. The
   dashboard bounds handshake and send times and cancels both pumps when
   either transport ends. Disposing a view disconnects only that peer, not
   the AppHost-owned producer.

The browser never sees `consumerUdsPath` and cannot induce the dashboard
to connect to an arbitrary local socket — it can only ask for
`(resource, replica)` pairs that are present in the resource snapshot
stream.

### AppHost-owned terminal views

`/api/apphost-terminal?terminalId=<id>` uses the same frontend authorization,
same-origin validation, and HWT presentation bridge. Its upstream is the opaque
HMP byte stream from `AttachTerminal`, tunneled over the existing dashboard
gRPC connection. Closing a viewer releases only that attachment; the creator
continues to own the terminal.

An authoritative gRPC `Ended` notification closes the viewer's WebSocket with
Aspire's private application close code `4000`, including completion before the
initial HMP handshake. This is an Aspire endpoint contract, not a Hex1b close
code or an Aspire-specific HWT message. The browser observes it through native
`onClose` and leaves the tab or dialog visible without reconnecting. Completion
before the first frame can leave an empty view; an already mounted view keeps
its last available projection, without guaranteeing a final frame. Normal
closure (`1000`), abnormal transport loss (`1006`), close reason strings, and
`wasClean` do not indicate producer completion and remain retryable.

Each component registers a separate input policy with the dashboard and passes
its opaque `viewId` with the WebSocket URL. Changes to an interaction's disabled
state update that policy before updating browser input behavior. The bridge
applies `Hwt1PresentationAdapter.IsReadOnly` before dispatching each complete
command, delegating validation and input gating to Hex1b. The browser uses
`setReadOnly` without remounting; native gating also cancels held pointers,
queued gestures and pending clipboard pastes, including direct paste/action
calls. Read-only views retain output, acknowledgements, selection, copy, and
history while connected. Already accepted or in-flight commands cannot be recalled.
This is a per-view presentation policy, not a new user authorization boundary:
it does not lock the terminal, its creator's automation, or other viewers.

### Browser requirements and package pairing

The dashboard uses `@hex1b/web-terminal` and the `Hex1b` NuGet package at
exactly `0.167.0-alpha.1549.1.496ccf5`. HWT1 is experimental state transfer
between these paired packages, not a stable wire contract implemented by
Aspire. Upgrade both together. The full npm `dist` tree is vendored, including
module workers, relative imports, fonts and licenses.

The dashboard uses the package's automatic renderer selection: WebGPU is
preferred, with WebGL2 used when WebGPU capabilities or device acquisition are
unavailable. WebGPU requires a secure context (HTTPS or localhost); WebGL2 can
render on ordinary HTTP. Clipboard API restrictions still apply, and renderer
selection does not relax transport security, authorization or origin checks.
Both backends require OffscreenCanvas and module workers. Initialization and
runtime rendering failures remain visible errors; there is no xterm.js fallback.
Sixel and Kitty Graphics Protocol are rendered
from server-authoritative state. Historical rendering is text-only. The
dashboard's independent console-log view remains available.

Text selections use a translucent Aspire accent highlight. A Fluent copy button
appears below and to the right of the last visible selected line, clamping to the
canvas edges and moving above the line when there is not enough room below.
The dashboard uses Hex1b's public selection overlay and copy action; Hex1b retains
ownership of authoritative selection text, history and clipboard handling.
After a successful copy, the selection and copy overlay are cleared and focus
returns to the terminal, ready for Cmd+V or Ctrl+V. A failed copy leaves the
selection available for retry.

HMP checkpoints retain uploaded Kitty image data even when an animation
temporarily removes its placements. They also preserve partially received ANSI
sequences, so late and reconnected viewers can resume placement-only updates
without losing pixels or displaying fragments of graphics commands. See the
[graphics and partial-sequence replay fix](https://github.com/mitchdenny/hex1b/pull/496).

The package handles Ctrl/Cmd+click on authoritative OSC 8 hyperlinks in live
output and history. HMP state replay preserves link destinations across late
attachment and reconnect. It only opens absolute HTTP, HTTPS and mailto destinations
with `noopener,noreferrer`; plain clicks and drags retain selection/application
behavior. Aspire adds no custom opener or plain-text URL detection. See the
[hyperlink PR](https://github.com/mitchdenny/hex1b/pull/489),
[renderer PR](https://github.com/mitchdenny/hex1b/pull/491), and
[hyperlink replay fix](https://github.com/mitchdenny/hex1b/pull/493).

### Console / Terminal view toggle

For a terminal-enabled resource the dashboard `ConsoleLogs` page mounts
**both** `LogViewer` (the resource's standard log stream) and
`TerminalView` (the interactive Hex1b web terminal) at the same time and
flips between them via a pair of **Console logs** / **Terminal** items
rendered inside the toolbar's options (⋯) `AspireMenuButton`:

- The page defaults to **Console** on resource selection so any pre-PTY
  hosting messages — `WaitFor` notifications, startup failures, image
  pull progress — are visible immediately.
- The view is purely user-controlled: the page never auto-switches
  between Console and Terminal. The user picks the view from the ⋯
  menu and the page stays on that view until they pick the other one,
  or a different resource is selected (which resets to Console).
- Both views stay mounted across flips (visibility is toggled with
  `display:none` on a wrapper `<div>`); the log subscription and the
  Hex1b/HMP1 consumer session are kept alive so neither view loses
  scrollback or has to re-handshake on toggle. After a `display:none →
  visible` transition the page calls `refreshLayout` on the JS terminal
  to fit the terminal to the new available space.

The terminal frame keeps font decrease/increase buttons, the current font
size, and the live columns-by-rows selector together in its bottom-right
footer. A separate Fit button switches to container-sized rows and columns
without changing font size, and is disabled while the view is already the
auto-sized primary. The selector offers predefined terminal dimensions
and displays the current grid,
keeping both sizing operations available without opening the page options
menu. A terminal starts at 132×50. A viewer adopts the producer's current
dimensions. Ordinary keyboard and paste input do not take resize control;
explicit footer sizing actions request primary and wait for confirmation before
changing the grid. The bottom-left footer hint advertises <kbd>F6</kbd>, which moves
keyboard focus from terminal input to the footer controls; <kbd>Shift+F6</kbd>
moves focus to the preceding dashboard control.

Dock panes, interaction dialogs and detached windows automatically fit when
opened. A detached window takes primary once, carrying the originating view's
selected font size rather than its grid dimensions. Its font preference can
then change independently of the opener. The active dock pane requests resize
control when revealed or returned from a detached window; inactive panes do not take it.
Container resizing then changes rows and columns, not the selected font size.
Read-only views cannot take resize control, and a view that loses primary to
another viewer does not automatically reclaim it.

Hex1b enforces a minimum grid of 20 columns by 10 rows. If the container is too
small for that grid at the selected font size, the renderer still scales the
text down to fit.

The console log stream is now subscribed to for terminal-enabled
resources too (previously it was suppressed), which is what makes the
Console view non-empty for a `WithTerminal()` resource.

## CLI

`aspire terminal attach <resource> [--replica N]` (`Aspire.Cli/Commands/TerminalAttachCommand.cs`)
opens its own `Hmp1WorkloadAdapter` against the consumer UDS path
returned by `IBackchannel.GetTerminalInfoAsync(resource, replica)` and
renders frames into the host terminal via Hex1b's `Hex1bTerminal`. When the
resource has more than one replica and the CLI is interactive, it prompts
for a selection; in non-interactive mode the `--replica` flag is required.

### Tape playback

`aspire terminal tape play <resource> --tape-file <path>` uses Hex1b's
`TapeParser` and `TapePlayer` to execute a VHS `.tape` script against an
existing resource terminal. It requires the same `features.terminalCommandsEnabled` feature
flag as `terminal attach` and `terminal ps`.

```sh
aspire terminal tape play shell --tape-file ./probe.tape
aspire terminal tape play shell --tape-file ./probe.tape --replica 1 --apphost ./AppHost/AppHost.csproj --timeout 30
```

For example, against an idle Bash shell:

```text
Set TypingSpeed 0
Set WaitTimeout 10s
Wait+Line /[$#>]$/
Type "printf 'ASPIRE_TAPE_%s\n' ready"
Enter
Wait+Screen /ASPIRE_TAPE_ready/
```

Wait for meaningful application output rather than merely the echoed input.
Here the expected marker is deliberately not contiguous in the typed command.
Exit code zero means the tape completed, not that every shell command succeeded.

The command prints the final plain-text screen to stdout; discovery messages,
warnings, and source-located diagnostics go to stderr. A comment-only or empty
tape reads the current screen after the initial producer snapshot has arrived.
A failed tape command prints its failure screen and returns a nonzero exit code.
`--timeout` defaults to 120 seconds and bounds the HMP connection and playback using
cancellation; capture finalization and cleanup may continue after cancellation.
An overall timeout returns exit code 17, and user cancellation returns 130.

Playback connects as a secondary HMP peer. It does not request primary ownership,
resize the producer, create a new shell, or stop the resource on completion or
failure. Other viewers and input sources can remain attached; input is not
exclusive, so coordinate playback with other users. A disconnected transport
fails playback rather than retrying potentially non-idempotent input.

Supported commands follow the pinned Hex1b tape implementation: `Type`, keys and
chords, `Sleep`, `Wait` / `Wait+Line` / `Wait+Screen`, timing and wait settings,
`Source`, text `Output`, and `Hide` / `Show`. For example,
`Wait+Screen@5s /Ready/` sets that wait's timeout. This is not full VHS media or
presentation compatibility: screenshots/video, clipboard actions, scrolling,
font/pixel-size settings, and shell-launch/environment settings are rejected
during preflight before any input is sent.

`Source` and `.txt` / `.ascii` `Output` paths resolve relative to the root tape
file's directory on the **CLI machine**, including sources nested in other
directories. Output parent directories must already exist, and existing output
files are not overwritten. Included tapes must have the `.tape` extension;
their own `Output` directives are ignored. Text `Output` records per-command
screens, unlike stdout's final screen.

## DCP integration

For each replica of a `WithTerminal()` resource, DCP allocates a pseudo-terminal
when the executable (or container) spec carries a populated `terminal` block:

```json
{
  "terminal": {
    "udsPath":    "/run/user/1000/aspire/trmnl/<run-id>/<resource>-<idx>/producer.sock",
    "socketMode": "connect",
    "cols":       132,
    "rows":       50
  }
}
```

`socketMode: "connect"` tells DCP to dial the named UDS (the TerminalHost
process owns the listener). The dimensions are the initial PTY size; both
sides exchange resize frames over HMP afterwards.

Desktop PTY support is implemented across all three platforms (Unix98 `/dev/ptmx` on Linux and macOS; ConPTY on Windows).
Container PTYs use the container runtime's attach command. DCP currently starts the
container before attaching, so one-time startup output, including terminal capability
queries, can be lost. See the [DCP startup ordering](https://github.com/microsoft/dcp/blob/v0.25.13/controllers/container_controller.go#L1843-L1855).

The `notcurses` resource in `playground/Terminals` installs Ubuntu's `notcurses-bin`
package and starts an interactive Bash shell. Open its dashboard Terminal view, then
run `notcurses-demo` to exercise graphics, color and Unicode rendering. Starting the
demo from the attached shell avoids losing its initial capability queries. Press
`q` to return to the shell; run the command again to repeat the stress workload.

## Files of interest

| Concern                              | File                                                                |
|--------------------------------------|---------------------------------------------------------------------|
| Public API entry point               | `src/Aspire.Hosting/TerminalResourceBuilderExtensions.cs`           |
| Per-resource hidden host resource    | `src/Aspire.Hosting/ApplicationModel/TerminalHostResource.cs`       |
| DCP wire-up                          | `src/Aspire.Hosting/Dcp/ExecutableCreator.cs`                       |
| Backchannel `GetTerminalInfoAsync`   | `src/Aspire.Hosting/Backchannel/AuxiliaryBackchannelRpcTarget.cs`   |
| Snapshot stamping                    | `src/Aspire.Hosting/Dashboard/DashboardServiceData.cs`              |
| TerminalHost process                 | `src/Aspire.TerminalHost/`                                          |
| CLI command                          | `src/Aspire.Cli/Commands/TerminalCommand.cs`                        |
| Dashboard WebSocket proxy            | `src/Aspire.Dashboard/Terminal/TerminalWebSocketProxy.cs`           |
| Dashboard resolver                   | `src/Aspire.Dashboard/Terminal/DefaultTerminalConnectionResolver.cs`|
| `TerminalView` (Hex1b web host)      | `src/Aspire.Dashboard/Components/Controls/TerminalView.razor.*`     |
| Property keys                        | `src/Shared/Model/KnownProperties.cs` (`Terminal.*`)                |
| Playground sample                    | `playground/Terminals/Terminals.AppHost/AppHost.cs`                 |
