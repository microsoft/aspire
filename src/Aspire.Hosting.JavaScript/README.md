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

builder.AddJavaScriptApp("frontend", "../frontend", "app.js");

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addJavaScriptApp("frontend", "../frontend", "app.js");

await builder.build().run();
```

### Deno apps

Add a Deno application by specifying its application directory and entrypoint. Deno must be installed
and available on `PATH` for local development.

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDenoApp("api", "../api", "main.ts")
       .WithHttpEndpoint(env: "PORT");

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addDenoApp("api", "../api", "main.ts")
    .withHttpEndpoint({ env: "PORT" });

await builder.build().run();
```

`AddDenoApp` runs the entrypoint directly by default. Use `WithDenoTask` or `WithRunScript` for tasks
defined in `deno.json`, `WithDenoServe` for `deno serve` handlers, and the other `WithDeno*` methods
to configure permissions, resolution, watch, inspector, and runtime arguments.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/frameworks/javascript/
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node
https://docs.deno.com/runtime/

## Feedback & contributing

https://github.com/microsoft/aspire
