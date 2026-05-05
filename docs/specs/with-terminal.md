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

The dashboard then renders an xterm.js terminal per replica, and the CLI
exposes the same session as `aspire terminal agent --replica 0`.

## Process topology

```
                                ┌────────────────────────────┐
                                │  AppHost (dotnet run)      │
                                │  - Aspire.Hosting          │
                                │  - DCP control plane       │
                                │  - per-resource:           │
                                │    Aspire.TerminalHost     │  (1 process / WithTerminal())
                                └─────────────┬──────────────┘
                                              │ spawn
                                              ▼
┌──────────────────────┐                ┌───────────────────────┐                ┌────────────────────────┐
│  DCP-launched        │   PTY (Win)    │  TerminalHost         │   HMP v1 UDS   │  Consumers             │
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
| **TerminalHost** | `controlUdsPath`  | AppHost → host (lifecycle, stats)| Per resource |

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

The `Hmp1WorkloadAdapter` is what the AppHost-side terminal host uses to
multiplex DCP's PTY traffic to the consumer-facing listener; the
`Hmp1PresentationAdapter` is what consumers (Dashboard WebSocket proxy and
the CLI) use to attach.

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

1. `ITerminalConnectionResolver.ConnectAsync(resourceName, replicaIndex, ct)`
   walks `IDashboardClient.GetResources()`, matches by `DisplayName` +
   `TryGetTerminalReplicaInfo`, and connects via
   `Hmp1Transports.ConnectUnixSocket(consumerUdsPath, ct)`.
2. The proxy wraps the resulting stream in `Hmp1WorkloadAdapter` and runs
   two pumps:
   - **Inbound (browser → producer):** binary frames are forwarded as HMP v1
     `Input` (keystrokes); text frames are parsed as JSON resize control
     messages (`{"type":"resize","cols":N,"rows":N}`).
   - **Outbound (producer → browser):** VT bytes from the producer become
     binary WebSocket frames; resize hints from the producer become JSON
     text frames.
3. Frame type — not content — distinguishes keystroke from control. This
   keeps the proxy's parser cheap and avoids ambiguity around binary input
   that happens to look like JSON.
4. Multi-fragment WS reads are reassembled in `ReassembledFrame` using
   `ArrayPool<byte>`.

The browser never sees `consumerUdsPath` and cannot induce the dashboard
to connect to an arbitrary local socket — it can only ask for
`(resource, replica)` pairs that are present in the resource snapshot
stream.

## CLI

`aspire terminal <resource> [--replica N]` (`Aspire.Cli/Commands/TerminalCommand.cs`)
opens its own `Hmp1PresentationAdapter` against the consumer UDS path
returned by `IBackchannel.GetTerminalInfoAsync(resource, replica)` and
renders frames into the host terminal via Hex1b's `Hex1bTerminal`. When the
resource has more than one replica and the CLI is interactive, it prompts
for a selection; in non-interactive mode the `--replica` flag is required.

## DCP integration

DCP allocates a Windows ConPTY per replica when the spec carries
`terminal.enabled=true` and the supplied `terminal.producerUdsPath`,
`terminal.consumerUdsPath`, and `terminal.controlUdsPath` properties. DCP
opens the producer-side UDS and writes PTY traffic into it; the
TerminalHost owns the consumer-side listener and broadcasts to attached
clients.

Linux / macOS / container PTY support is tracked as Phase 3 follow-ups in
the parent issue; for the 13.4 release, the playground guards
non-Windows code paths with `OperatingSystem.IsWindows()` checks.

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
| `TerminalView` (xterm.js host)       | `src/Aspire.Dashboard/Components/Controls/TerminalView.razor.*`     |
| Property keys                        | `src/Shared/Model/KnownProperties.cs` (`Terminal.*`)                |
| Playground sample                    | `playground/Terminals/Terminals.AppHost/AppHost.cs`                 |
