# Go app hosting integration

Use this integration to model, configure, and orchestrate Go applications in an Aspire solution.

## Getting started

### Prerequisites

The **Go toolchain** (`go`) must be available on the PATH of the machine running the AppHost.
For GoLand remote debugging, [Delve](https://github.com/go-delve/delve) (`dlv`) must also be on the PATH.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Go` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Go
```

## Usage example

In the AppHost, add a Go application resource:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddGoApp("api", "../go-api")
    .WithHttpEndpoint(port: 8080)
    .WithExternalHttpEndpoints()
    .WithOtlpExporter();

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const api = await builder.addGoApp("api", "../go-api")
    .withHttpEndpoint({ port: 8080 })
    .withExternalHttpEndpoints()
    .withOtlpExporter();

await builder.build().run();
```

The method executes the package as `go run .` from the directory containing `go.mod`.
Pass runtime arguments via `.WithAppArgs(...)` and pre-start module commands via `.WithModTidy()`, `.WithModVendor()`, `.WithModDownload()`, or `.WithVetTool()`.

### Build flags

Build-time compiler options are parameters of `AddGoApp` itself:

```csharp
builder.AddGoApp("api", "../go-api",
    buildTags: ["integration", "netgo"],
    ldFlags: "-X main.version=1.2.3 -s -w",
    gcFlags: "all=-N -l",
    raceDetector: true);
```

Pass runtime arguments to the program:

```csharp
builder.AddGoApp("api", "../go-api")
    .WithAppArgs("--config", "prod.yaml");
```

### Debugging

Delve is the only Go debugger — both VS Code and GoLand use it under the hood, just in different
modes. `Aspire.Hosting.Go` supports both modes.

#### VS Code (automatic, default)

VS Code debugging is enabled automatically by `AddGoApp`. Install the
[Go extension](https://marketplace.visualstudio.com/items?itemName=golang.go) and use the normal
Aspire "Start Debugging" flow. The extension launches its own `dlv-dap` process; the application
continues to run as `go run .` and no extra setup is required.

#### GoLand or VS Code attach mode (headless Delve server)

Use `WithDelveServer` when you need GoLand or a VS Code "attach to remote" configuration. The
application is replaced by a headless Delve server:

```csharp
builder.AddGoApp("api", "../go-api")
    .WithDelveServer()
    .WithHttpEndpoint(port: 8080);
```

This launches:
```sh
dlv --headless=true --listen=127.0.0.1:2345 --api-version=2 debug .
```

The default accepts one debugger client. To keep the server available after a debugger disconnects
or allow multiple debugger clients, opt in with `AcceptMultiClient`. Configure additional Delve
server flags with `DelveServerOptions`:

```csharp
builder.AddGoApp("api", "../go-api")
    .WithDelveServer(new DelveServerOptions
    {
        AcceptMultiClient = true,
        ContinueOnStart = true,
        Log = true,
        LogOutput = "rpc,dap,debugger"
    });
```

For a TypeScript AppHost, pass the same options as an object:

```typescript
const api = await builder.addGoApp("api", "../go-api");
await api.withDelveServer({
    acceptMultiClient: true,
    continueOnStart: true,
    log: true,
    logOutput: "rpc,dap,debugger",
});
```

| Option | Default | Delve behavior |
|---|---|---|
| `Port` | `2345` | Sets the loopback listener port. |
| `AcceptMultiClient` | `false` | Passes `--accept-multiclient` when enabled. |
| `OnlySameUser` | `null` | Passes `--only-same-user=true` or `--only-same-user=false` when set. |
| `ContinueOnStart` | `false` | Passes `--continue` when enabled. |
| `Log` | `false` | Passes `--log` when enabled. |
| `LogOutput` | `null` | Passes `--log-output=<components>` when logging is enabled and components are specified. |

Set `ContinueOnStart` to `true` when you want the Go application to run immediately under Delve and
attach a debugger later.

If an IDE fails to attach, enable Delve logging first and inspect the resource logs. Some IDE and
remote-environment combinations can cause Delve to reject the connection because it appears to come
from a different operating system user. If the logs show that the same-user check is failing, you
can disable that check:

```csharp
builder.AddGoApp("api", "../go-api")
    .WithDelveServer(new DelveServerOptions
    {
        OnlySameUser = false,
        Log = true,
        LogOutput = "rpc,dap,debugger"
    });
```

`OnlySameUser = false` maps to `--only-same-user=false`. Use it only when needed; the Delve listener
remains bound to `127.0.0.1`.

**GoLand** — create a **Go Remote** run/debug configuration (**Edit | Run Configurations**):
- **Host**: `localhost`
- **Port**: `2345`

**VS Code** — add to `launch.json`:
```json
{
  "name": "Attach to api",
  "type": "go",
  "request": "attach",
  "mode": "remote",
  "host": "localhost",
  "port": 2345
}
```

**Zed** — add to `debug.json`:
```json
[
  {
    "label": "Attach to api",
    "adapter": "Delve",
    "request": "attach",
    "mode": "remote",
    "tcp_connection": {
      "host": "127.0.0.1",
      "port": 2345
    },
    "stopOnEntry": false
  }
]
```

Start the debug configuration after the resource appears as running in the Aspire dashboard.
See the [JetBrains docs](https://www.jetbrains.com/help/go/attach-to-running-go-processes-with-debugger.html) for details.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- https://aspire.dev/integrations/frameworks/go/go-host/
- [Aspire documentation](https://aspire.dev/)
- [Delve debugger](https://github.com/go-delve/delve)

## Feedback & contributing

https://github.com/microsoft/aspire
