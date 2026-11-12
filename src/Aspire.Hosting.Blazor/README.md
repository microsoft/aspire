# Blazor hosting integration

Use this integration to model, configure, and orchestrate Blazor WebAssembly applications and a Blazor Gateway in an Aspire solution.

## Getting started

### Prerequisites

An existing Blazor WebAssembly project and any backend API projects it calls. The example below assumes the API exposes an endpoint named `http`.

Publishing requires the client's selected .NET SDK (8 through 11) to be available locally. The build image downloads that SDK alongside the gateway SDK, preserving client SDK selection; network access is required.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Blazor` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Blazor
aspire add Aspire.Hosting.Dotnet
```

The Blazor gateway APIs are experimental. C# callers must opt in by suppressing `ASPIREBLAZOR001`, as shown below.

## Usage example

Then, in the AppHost, add a Blazor WebAssembly resource, reference its backend API, and attach it to a gateway with either C# or TypeScript.

The C# example retains the `BlazorApp` project reference for `AddBlazorWasmProject<Projects.BlazorApp>`
metadata and client build inputs. The API service uses its real path rather than generated metadata.
See the [project migration guide](../../docs/migrate-from-addproject.md#fluent-and-integration-apis).

**C#**

```csharp
#pragma warning disable ASPIREBLAZOR001

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../ApiService/ApiService.csproj");

var blazor = builder.AddBlazorWasmProject<Projects.BlazorApp>("web")
    .WithReference(api);

builder.AddDotnetProjectBlazorGateway("gateway")
    .WithBlazorClientApp(blazor)
    .WithExternalHttpEndpoints();

builder.Build().Run();

#pragma warning restore ASPIREBLAZOR001
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const api = await builder.addDotnetProject("api", "../ApiService/ApiService.csproj");

const blazor = await builder.addBlazorWasmProject("web", "../BlazorApp/BlazorApp.csproj")
    .withReference(await api.getEndpoint("http"));

await builder.addDotnetProjectBlazorGateway("gateway")
    .withBlazorClientApp(blazor)
    .withExternalHttpEndpoints();

await builder.build().run();
```

The gateway serves the client app at `/web/` and proxies requests to its referenced APIs. No separate gateway project is required.

`AddDotnetProjectBlazorGateway` returns a `DotnetProjectResource` builder; its matching
`WithBlazorClientApp` overload preserves that type. The older `AddBlazorGateway` keeps its
legacy return contract for compatibility. Both gateways intentionally launch the official
development tool as a process; this does not implement Project V2 service watching.
Blazor's independent `ASPIREBLAZOR001` opt-in still applies.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/dotnet/blazor-get-started/
* https://aspire.dev/integrations/dotnet/blazor-hosting/
* https://aspire.dev/integrations/dotnet/blazor-connect/
* https://learn.microsoft.com/aspnet/core/blazor/

## Feedback & contributing

https://github.com/microsoft/aspire
