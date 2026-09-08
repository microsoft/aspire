# .NET / C# app hosting integration

Use this integration to model, configure, and orchestrate C# projects and file-based C# apps
(added **by path**) in an Aspire solution. It is the C# peer of `Aspire.Hosting.Go`,
`Aspire.Hosting.Python`, and `Aspire.Hosting.JavaScript`.

> [!NOTE]
> `AddDotnetProject` is experimental and is exposed under the `ASPIREDOTNETPROJECT001` diagnostic.
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

var api = builder.AddDotnetProject("api", "../api/api.csproj")
    .WithHttpEndpoint(port: 8080)
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const api = await builder.addDotnetProject("api", "../api/api.csproj")
    .withHttpEndpoint({ port: 8080 })
    .withExternalHttpEndpoints();

await builder.build().run();
```

Before resources start, Aspire collects projects with compatible SDK and environment contexts into
generated MSBuild traversal projects under the AppHost's intermediate output. Build groups run serially, while projects within
each traversal group can build in parallel. File-based apps use serialized direct builds so their
`#:project` references cannot build shared outputs concurrently. Launch-profile and `WithEnvironment`
values are runtime configuration and do not prevent projects from sharing a traversal build. Each
traditional project is then launched with the `RunCommand` and `RunArguments` resolved from the
already-built project, so runtime environment variables cannot change which output is selected.
File-based apps launch with `dotnet run --file <path> --no-build` after their direct build; uncoordinated
file-based launches use `--no-cache` instead. Start and Restart reuse the coordinated output, matching
project-based resources. Use the Rebuild command after source changes to rebuild and restart the resource.

Endpoints, environment variables, and service discovery are configured from the project's
`launchSettings.json` and Kestrel configuration, matching `AddProject<T>`.

### Configure the build environment

Use `WithBuildEnvironment` when an environment variable must affect MSBuild evaluation for a `.csproj`
resource. File-based `.cs` apps do not support build-only environment variables and are rejected when
`WithBuildEnvironment` is called. Projects with build-specific environment variables use serialized direct
builds instead of a shared traversal build. Build environment variables are not added to the launched process;
configure the same variable with `WithEnvironment` as well when it is needed at runtime. The same build-only
values are applied when the project is published through the .NET SDK container targets.

Do not use `WithBuildEnvironment` for secrets. Aspire carries these values in IDE launch metadata and
process environments. Protected temporary MSBuild response files preserve global-property semantics
without exposing values in process command lines, but values can appear in build diagnostics and are
not a general-purpose secret transport.

**C#**

```csharp
builder.AddDotnetProject("worker", "../worker/worker.csproj")
    .WithBuildEnvironment("BUILD_FLAVOR", "custom");
```

**TypeScript**

```typescript
await builder
    .addDotnetProject("worker", "../worker/worker.csproj")
    .withBuildEnvironment("BUILD_FLAVOR", "custom");
```

## Publishing

Resources created with `AddDotnetProject` participate in the same .NET SDK container publishing pipeline as
resources created with `AddProject`. Project files, directories containing a single project file, and file-based
C# apps are emitted as project resources in the Aspire manifest and can be deployed through supported compute
environments.

File-based apps preserve the .NET SDK's publishing defaults, including Native AOT. Native AOT cannot compile
across operating systems, so publishing a file-based app from Windows or macOS to a Linux container can fail.
To publish a framework-dependent container instead, add this directive to the C# file:

```csharp
#:property PublishAot=false
```

Alternatively, run publishing on the target operating system to retain Native AOT. Aspire reports focused
guidance when it detects this cross-operating-system failure.

Call `PublishAsDockerFile(...)` or `publishAsDockerFile(...)` to use an explicit Dockerfile instead of .NET SDK
container publishing. Call `ExcludeFromManifest()` or `excludeFromManifest()` when the resource is intentionally
available only during local orchestration.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- [Aspire documentation](https://aspire.dev/)

## Feedback & contributing

https://github.com/microsoft/aspire
