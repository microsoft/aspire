# Blazor hosting integration

Use this integration to model, configure, and orchestrate Blazor WebAssembly applications and a Blazor Gateway in an Aspire solution.

## Getting started

### Prerequisites

An existing Blazor WebAssembly project and any backend API projects it calls. The example below assumes the API exposes an endpoint named `http`.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Blazor` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Blazor
```

The Blazor gateway APIs are experimental. C# callers must opt in by suppressing `ASPIREBLAZOR001`, as shown below.

## Usage example

Then, in the AppHost, add a Blazor WebAssembly resource, reference its backend API, and attach it to a gateway with either C# or TypeScript. For the C# example, add project references to `ApiService` and `BlazorApp` from the AppHost.

**C#**

```csharp
#pragma warning disable ASPIREBLAZOR001

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.ApiService>("api");

var blazor = builder.AddBlazorWasmProject<Projects.BlazorApp>("web")
    .WithReference(api);

builder.AddBlazorGateway("gateway")
    .WithBlazorClientApp(blazor)
    .WithExternalHttpEndpoints();

builder.Build().Run();

#pragma warning restore ASPIREBLAZOR001
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const api = await builder.addProject("api", "../ApiService/ApiService.csproj");

const blazor = await builder.addBlazorWasmProject("web", "../BlazorApp/BlazorApp.csproj")
    .withReference(await api.getEndpoint("http"));

await builder.addBlazorGateway("gateway")
    .withBlazorClientApp(blazor)
    .withExternalHttpEndpoints();

await builder.build().run();
```

The gateway serves the client app at `/web/` and proxies requests to its referenced APIs. No separate gateway project is required.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/dotnet/blazor-get-started/
* https://aspire.dev/integrations/dotnet/blazor-hosting/
* https://aspire.dev/integrations/dotnet/blazor-connect/
* https://learn.microsoft.com/aspnet/core/blazor/

## Feedback & contributing

https://github.com/microsoft/aspire
