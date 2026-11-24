# JavaScript app hosting integration

Use this integration to model, configure, and orchestrate JavaScript projects in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.JavaScript` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.JavaScript
```

## Usage example

In the AppHost, add a JavaScript app resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaScriptApp("frontend", "../frontend", "dev");

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addJavaScriptApp("frontend", "../frontend", { runScriptName: "dev" });

await builder.build().run();
```

### Runtime and framework-specific apps

Use the most specific helper for the runtime or framework hosting the app:

| Runtime or framework | C# / TypeScript APIs | Use |
|---|---|---|
| Node.js | `AddNodeApp` / `addNodeApp` | Run a JavaScript or TypeScript entry point with Node.js. |
| Bun | `AddBunApp` / `addBunApp` | Run an entry point with Bun. |
| Deno | `AddDenoApp` / `addDenoApp` | Run an entry point with Deno. |
| Vite | `AddViteApp` / `addViteApp` | Run a Vite development server and publish the app as a container. |
| Next.js | `AddNextJsApp` / `addNextJsApp` | Run and publish a Next.js application. |

The runtime used for local development must be installed and available on `PATH`. Deno apps run their
entry point directly by default; use `WithDenoTask`, `WithDenoServe`, and the other `WithDeno*` APIs
to select another mode or configure permissions and runtime flags.

### OpenTelemetry

The Node.js, Bun, Vite, Next.js, and generic JavaScript resources receive the standard OpenTelemetry
environment variables, such as `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_SERVICE_NAME`. These variables
only take effect when the app registers an OpenTelemetry SDK. Without one, the app sends no traces,
metrics, or logs, and the dashboard, `aspire otel`, and the MCP server show no telemetry for it.
`aspire describe` and the MCP `list_resources` tool flag running resources that are configured for
OpenTelemetry but haven't sent any telemetry. Deno resources use Deno's built-in OpenTelemetry
support and don't need an SDK.

For a Node.js-based app, a minimal setup installs the auto-instrumentation package in the app:

```bash
npm install @opentelemetry/auto-instrumentations-node
```

Then load its `register` module when Node starts, for example through `NODE_OPTIONS` on the resource:

**C#**

```csharp
builder.AddViteApp("frontend", "../frontend")
       .WithEnvironment("NODE_OPTIONS", "--require @opentelemetry/auto-instrumentations-node/register");
```

**TypeScript**

```typescript
await builder.addViteApp("frontend", "../frontend")
    .withEnvironment("NODE_OPTIONS", "--require @opentelemetry/auto-instrumentations-node/register");
```

See the [OpenTelemetry JavaScript zero-code instrumentation documentation](https://opentelemetry.io/docs/zero-code/js/)
for other registration options.

### Publishing

Next.js hosting and the JavaScript publishing helpers are stable APIs and do not require suppressing
`ASPIREJAVASCRIPT001`. Choose the publish shape that matches the framework's production output:

| API | Published container |
|---|---|
| `AddNextJsApp` | Node.js running Next.js standalone output, including `public/` and `.next/static/`. |
| `PublishAsStaticWebsite` | YARP serving the built static files, with optional API reverse-proxying. `PublishAsStaticWebsiteOptions.OutputPath` defaults to `dist`. |
| `PublishAsNodeServer` | Node.js running the specified built entry point directly. |
| `PublishAsPackageScript` | The configured package manager running a production script, with its runtime dependencies. |

`AddNextJsApp` requires `output: "standalone"` in the Next.js configuration and a `public/` directory
for the generated Dockerfile. Use `DisableBuildValidation()` only when the configuration is
provided dynamically and cannot be statically inspected; it does not change the required build output.
User-authored Dockerfiles are preserved rather than replaced by generated Dockerfiles.

### Deno runtime and publishing

The Deno hosting APIs are stable and do not require suppressing `ASPIREDENO001`.
Aspire tests Deno with version 2.9.0, and generated Dockerfiles default to
`denoland/deno:2.9.0` for both build and runtime stages. Local execution uses the Deno
installation on `PATH`; `WithDockerfileBaseImage` can override the container images.
Use a runtime version that supports the flags and features your application selects.
Stability of the Aspire APIs does not make Deno's opt-in `--unstable-*` features stable.

| Configuration | Local execution | Generated container |
|---|---|---|
| Default entry point | `deno run -A <script>` | `deno run --allow-net --allow-env --cached-only <script>` with dependencies cached during the build. |
| Permissions | `WithDenoAllow` and `WithDenoDeny` configure explicit permissions; `WithDenoAllowAll(false)` disables the blanket grant. | Explicit permissions are preserved; deny-only configuration narrows the network/environment defaults. `WithDenoAllowAll(true)` explicitly grants all permissions in both modes. |
| Watch and inspector | `WithDenoWatch` and `WithDenoInspect` enable development flags for run/serve modes. | Managed watch and inspector flags are omitted. |
| Serve mode | `WithDenoServe` runs `deno serve` with an Aspire-managed HTTP endpoint. | Binds to `0.0.0.0` and the endpoint's target port (8000 by default). |
| Task mode | `WithDenoTask` or `WithRunScript` runs `deno task`; the task defines its own permissions and development flags. | Runs the selected task without inferring or pre-caching its module graph. Tasks must provide their own production-appropriate command. |

Generated Deno Dockerfiles do not support manual `node_modules` mode, alternate package
managers, `--env-file`, or configuration paths outside the app directory. Use a custom
Dockerfile when these are required.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/frameworks/javascript/
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node
https://docs.deno.com/runtime/

## Feedback & contributing

https://github.com/microsoft/aspire
