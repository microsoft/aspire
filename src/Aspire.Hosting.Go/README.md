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
    .WithDelveServer(port: 2345)
    .WithHttpEndpoint(port: 8080);
```

This launches:
```sh
dlv --headless=true --listen=127.0.0.1:2345 --api-version=2 debug .
```

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

Start the debug configuration after the resource appears as running in the Aspire dashboard.
See the [JetBrains docs](https://www.jetbrains.com/help/go/attach-to-running-go-processes-with-debugger.html) for details.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- https://aspire.dev/integrations/frameworks/go/go-host/
- [Aspire documentation](https://aspire.dev/)
- [Delve debugger](https://github.com/go-delve/delve)

## Feedback & contributing

https://github.com/microsoft/aspire
