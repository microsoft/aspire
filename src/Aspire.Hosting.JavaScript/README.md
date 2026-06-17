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

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/frameworks/javascript/
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node

## Feedback & contributing

https://github.com/microsoft/aspire
