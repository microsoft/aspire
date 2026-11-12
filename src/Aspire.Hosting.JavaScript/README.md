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
