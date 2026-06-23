# .NET / C# app hosting integration

Use this integration to model, configure, and orchestrate C# projects and file-based C# apps
(added **by path**) in an Aspire solution. It is the C# peer of `Aspire.Hosting.Go`,
`Aspire.Hosting.Python`, and `Aspire.Hosting.JavaScript`.

> [!NOTE]
> `AddCSharpApp` is experimental and is exposed under the `ASPIRECSHARPAPPS001` diagnostic.
> Its API surface may change in future releases.

## Getting started

### Prerequisites

The **.NET SDK** must be available on the PATH of the machine running the AppHost. File-based C# apps
(`.cs`) require **.NET 10 or later**.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Dotnet` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Dotnet
```

## Usage example

In the AppHost, add a C# app resource by path. The path can point at a project file (`.csproj`),
a directory containing a single `.csproj`, or a file-based app (`.cs`):

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddCSharpApp("api", "../api/api.csproj")
    .WithHttpEndpoint(port: 8080)
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const api = await builder.addCSharpApp("api", "../api/api.csproj")
    .withHttpEndpoint({ port: 8080 })
    .withExternalHttpEndpoints();

await builder.build().run();
```

The resource is launched with `dotnet run --project <path>` (or `dotnet run --file <path>` for a
file-based app). Endpoints, environment variables, and service discovery are configured from the
project's `launchSettings.json` and Kestrel configuration, matching `AddProject<T>`.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- [Aspire documentation](https://aspire.dev/)

## Feedback & contributing

https://github.com/microsoft/aspire
