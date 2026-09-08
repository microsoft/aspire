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
- Authenticated stream factory hooks (we only use Unix-socket transport
  today)

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

### Browser requirements and package pairing

The dashboard uses `@hex1b/web-terminal` and the `Hex1b` NuGet package at
exactly `0.167.0-alpha.1519.1.b8be265`. HWT1 is experimental state transfer
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

The console log stream is now subscribed to for terminal-enabled
resources too (previously it was suppressed), which is what makes the
Console view non-empty for a `WithTerminal()` resource.

## CLI

`aspire terminal <resource> [--replica N]` (`Aspire.Cli/Commands/TerminalCommand.cs`)
opens its own `Hmp1WorkloadAdapter` against the consumer UDS path
returned by `IBackchannel.GetTerminalInfoAsync(resource, replica)` and
renders frames into the host terminal via Hex1b's `Hex1bTerminal`. When the
resource has more than one replica and the CLI is interactive, it prompts
for a selection; in non-interactive mode the `--replica` flag is required.

## DCP integration

For each replica of a `WithTerminal()` resource, DCP allocates a pseudo-terminal
when the executable (or container) spec carries a populated `terminal` block:

```json
{
  "terminal": {
    "udsPath":    "/run/user/1000/aspire/trmnl/<run-id>/<resource>-<idx>/producer.sock",
    "socketMode": "connect",
    "cols":       120,
    "rows":       30
  }
}
```

`socketMode: "connect"` tells DCP to dial the named UDS (the TerminalHost
process owns the listener). The dimensions are the initial PTY size; both
sides exchange resize frames over HMP afterwards.

Desktop PTY support is implemented across all three platforms (Unix98 `/dev/ptmx` on Linux and macOS; ConPTY on Windows). Container PTYs are tracked
as a Phase 3 follow-up on the parent issue.

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
