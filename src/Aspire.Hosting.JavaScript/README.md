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

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/frameworks/javascript/
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node
https://docs.deno.com/runtime/

## Feedback & contributing

https://github.com/microsoft/aspire
