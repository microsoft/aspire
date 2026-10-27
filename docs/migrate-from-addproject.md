# Migrating from AddProject to AddDotnetProject

All six .NET-hosting `AddProject` overloads, and the legacy polyglot `addProject` export, are
obsolete. Use `AddDotnetProject` / `addDotnetProject` from `Aspire.Hosting.Dotnet` for ordinary
.NET services. The warnings do not remove the legacy APIs or redirect their implementation.
`ProjectResource`, `IProjectMetadata`, and `ProjectResourceOptions` remain available.

This is a resource-model migration, not just a method rename. Check the real project path,
launch-profile semantics, returned types, generic definitions and calls, and build ownership
separately. [Azure Functions and MAUI](#retained-legacy-scenarios) have specialized launch
behavior and are not converted by this migration.

## Packages and imports

From the AppHost directory, add the .NET hosting integration:

```bash
aspire add Aspire.Hosting.Dotnet
```

Keep the AppHost's `Aspire.Hosting.AppHost` reference and the integrations used by the rest of
the model. `Aspire.Hosting.AppHost` alone does not supply `AddDotnetProject`. Use compatible
Aspire package versions that include the replacement APIs described here. A prerelease
package does not mean every API in that package has an experimental compiler diagnostic.

For C# examples, the relevant namespaces are:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
```

`AddDotnetProject` and the ordinary builder extensions are in `Aspire.Hosting`.
`DotnetProjectResource` is in `Aspire.Hosting.Dotnet`; `IResourceBuilder<T>`,
`ProjectResource`, `IDotnetProgramResource`, and the resource capability interfaces are in
`Aspire.Hosting.ApplicationModel`. AppHost templates supply common implicit usings, but a
helper library may need these imports explicitly. EF resource declarations additionally use
`Aspire.Hosting.EntityFrameworkCore` and its package.

In a TypeScript AppHost, adding the integration makes its APIs available through the
generated SDK. Keep the standard combined import:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const api = await builder.addDotnetProject("api", "../Api/Api.csproj");

await builder.build().run();
```

Do not import the C# namespace as an npm package or edit generated `.aspire/modules` files.
Restore the AppHost to regenerate the SDK after changing integrations.

Removing `Aspire.AppHost.Sdk` is a related but separate step. See
[Migrating off Aspire.AppHost.Sdk](migrate-from-apphost-sdk.md) for the plain
`Microsoft.NET.Sdk`, explicit package references, CLI bundle, and Central Package Management
requirements. Keeping that SDK temporarily does not suppress `AddProject`'s own warning.

## Replace each overload

In the table, `path` is the actual project path, not a string derived from `TProject`'s name.
`configure` is an `Action<ProjectResourceOptions>`. The builder receiver is omitted for brevity.

| Legacy call | Replacement |
| --- | --- |
| `AddProject<TProject>(name)` | `AddDotnetProject(name, path)` |
| `AddProject<TProject>(name, launchProfileName)` | `AddDotnetProject(name, path, configure)` with the profile translation below |
| `AddProject<TProject>(name, configure)` | `AddDotnetProject(name, path, configure)` |
| `AddProject(name, projectPath)` | `AddDotnetProject(name, path)` |
| `AddProject(name, projectPath, launchProfileName)` | `AddDotnetProject(name, path, configure)` with the profile translation below |
| `AddProject(name, projectPath, configure)` | `AddDotnetProject(name, path, configure)` |

For example, after checking the service's actual `ProjectReference` or metadata:

**Before (intentionally legacy):**

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(database);
```

**After:**

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
    .WithReference(database);
```

Keep resource names, references, waits, endpoint configuration, and other equivalent
customizations. Named argument `projectPath:` becomes `path:`. There is no generic
`AddDotnetProject<Projects.Api>` overload.

### C# launch profiles: null is not always the default

| Legacy C# input | V2 configuration |
| --- | --- |
| No profile argument | Omit the options callback to select the default profile |
| String overload with `"https"` | `options => options.LaunchProfileName = "https"` |
| String overload with `launchProfileName: null` | `options => options.ExcludeLaunchProfile = true` |
| Options callback with `LaunchProfileName = null` | Default profile, unless `ExcludeLaunchProfile` is set |
| Options callback with `ExcludeLaunchProfile = true` | Keep the exclusion; it wins over a supplied name |
| `ExcludeKestrelEndpoints = true` | Keep this independent option |

Explicitly disabling profiles therefore looks like this:

```csharp
var api = builder.AddDotnetProject(
    name: "api",
    path: "../Api/Api.csproj",
    configure: options =>
    {
        options.ExcludeLaunchProfile = true;
        options.ExcludeKestrelEndpoints = true;
    });
```

Do not translate the old string overload's explicit null to `LaunchProfileName = null`:
that would enable default-profile selection instead. Excluding Kestrel-derived model
endpoints does not disable or rewrite the service's own Kestrel configuration.

### Polyglot options have a different contract

The legacy polyglot dispatcher treats an omitted argument **or explicit null as default
options**, unlike C#'s string-null overload. Translate a named-profile string to a plain
`DotnetProjectOptions` object; translate a mutable legacy `ProjectResourceOptions` handle
to the equivalent data properties instead of passing the handle to V2.

```typescript
const defaultApi = await builder.addDotnetProject("default-api", "../Api/Api.csproj");

const namedApi = await builder.addDotnetProject("named-api", "../Api/Api.csproj", {
    launchProfileName: "https"
});

const noProfileApi = await builder.addDotnetProject("no-profile-api", "../Api/Api.csproj", {
    excludeLaunchProfile: true,
    excludeKestrelEndpoints: true
});
```

Omitted options, an empty options object, or a null `launchProfileName` select the default
profile unless exclusion is set. The DTO properties are `launchProfileName`,
`excludeLaunchProfile`, and `excludeKestrelEndpoints`. Legacy exports remain callable for
compatibility; TypeScript marks them deprecated. Do not assume every generated language
has a native deprecation-warning mechanism.

## Resolve paths and check the service project

Paths are relative to `builder.AppHostDirectory`, **not** the shell's working directory.
Use an explicit filename when possible. A generated `Projects.Company_Api` identifier
does not guarantee a project named `Company.Api.csproj`, or any particular directory
layout. Generated names may be normalized or overridden, and a custom `IProjectMetadata`
implementation can provide an unrelated path. Inspect the original reference or metadata.

Supported inputs are:

* SDK-style C#, F#, and Visual Basic project files: `.csproj`, `.fsproj`, and `.vbproj`.
* A directory containing exactly **one total** supported project at its top level.
  One C# project plus one F# project is ambiguous; supply an explicit filename.
* A file-based C# app (`.cs`), requiring .NET 10 or later.

Extension matching is case-insensitive. Missing, unsupported, or ambiguous inputs are
reported on the resource-start path; creating a builder alone is not proof of validity.
Directory shorthand does not recursively search for a suitable service.

F# scripts (`.fsx`), non-SDK project systems, and arbitrary `*proj` files are not supported.
File-based `.cs` apps do not support `WithBuildEnvironment` or EF migration factories.
F#/VB project support does not imply script support or watch support.

For V2 VB services, launch-profile discovery checks `My Project/launchSettings.json`
first, then `Properties/launchSettings.json`. `My Project` wins if both exist. Legacy
`AddProject` service-profile discovery is unchanged. Do not infer project `.run.json`
discovery from file-app or IDE support for that format.

## Returned resources and builders

`AddProject` returns `IResourceBuilder<ProjectResource>`. `AddDotnetProject` returns
`IResourceBuilder<DotnetProjectResource>`, whose resource derives from `ExecutableResource`,
not `ProjectResource`. Neither a cast nor a type alias bridges those unrelated classes.
Using `var` avoids a declaration edit but does not fix a legacy-only downstream API.

### V2-only helper returns and stored values

Update a user-owned factory's return type and its callers together:

```csharp
static IResourceBuilder<DotnetProjectResource> AddApi(IDistributedApplicationBuilder builder)
{
    return builder.AddDotnetProject("api", "../Api/Api.csproj");
}
```

Apply the same change to resource-valued returns, fields, properties, arrays, collections,
callback parameters, and delegates such as
`Func<IDistributedApplicationBuilder, IResourceBuilder<DotnetProjectResource>>`.
Changing only the factory call leaves those constructed types incompatible.

### Mixed-model returns and covariance

If a contract needs only .NET-program identity, builder covariance permits a shared view:

```csharp
static IResourceBuilder<IDotnetProgramResource> AsProgram(
    IResourceBuilder<DotnetProjectResource> project)
{
    return project;
}

static IReadOnlyList<IResourceBuilder<IDotnetProgramResource>> GetPrograms(
    IResourceBuilder<ProjectResource> legacy,
    IResourceBuilder<DotnetProjectResource> modern)
{
    return [legacy, modern];
}
```

`IDotnetProgramResource` is a stable, empty identity marker. It does not expose
environment, endpoints, arguments, or metadata, and does not grant publishing or build
coordination. Choose the actual capability, such as `IResourceWithEndpoints`, when that
is what the caller consumes. Configure concrete-only APIs before narrowing the builder.
An `IResourceBuilder<IResource>` return does not preserve project-specific fluent chaining.

## Generic definitions are a separate migration

### Type-preserving fluent helpers

Replace a user-owned `where T : ProjectResource` constraint with what the operation
actually requires, keeping `IResourceBuilder<T>` on the return:

```csharp
public static class ServiceBuilderExtensions
{
    public static IResourceBuilder<T> WithDeploymentGroup<T>(
        this IResourceBuilder<T> builder, string group)
        where T : class, IResourceWithEnvironment
    {
        return builder.WithEnvironment("DEPLOYMENT_GROUP", group);
    }

    public static IResourceBuilder<T> WithDotnetDiagnostics<T>(
        this IResourceBuilder<T> builder, bool enabled)
        where T : class, IDotnetProgramResource, IResourceWithEnvironment
    {
        return builder.WithEnvironment("DOTNET_EnableDiagnostics", enabled ? "1" : "0");
    }
}
```

The first operation works for any environment-capable resource. The second is deliberately
.NET-specific, so it also requires the marker. The marker alone would not satisfy
`WithEnvironment`. Add endpoint/service-discovery/argument constraints only when needed.

### Generic types that hold projects

Update constraints, member types, and construction assumptions together:

```csharp
public sealed class ProgramHandle<T>(IResourceBuilder<T> builder)
    where T : class, IDotnetProgramResource
{
    public IResourceBuilder<T> Builder { get; } = builder;
    public T Resource => Builder.Resource;
}
```

Callers can construct `new ProgramHandle<DotnetProjectResource>(api)` after migrating `api`.
A wider constraint does not provide `new T(...)`, project metadata, or resource registration.
Accept a builder created by the supported factory rather than inventing a generic V2
resource constructor.

### Explicit generic calls and static extension calls

Check explicit type arguments independently of the helper definition:

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

IResourceBuilder<DotnetProjectResource> configured =
    ServiceBuilderExtensions.WithDotnetDiagnostics<DotnetProjectResource>(api, enabled: true);

var dockerfileApi =
    ExecutableResourceBuilderExtensions.PublishAsDockerFile<DotnetProjectResource>(configured);
```

Inference is usually simpler: `api.WithDotnetDiagnostics(enabled: true)`. A static call to
the old project-specific declaring class does not automatically select an executable
overload. Likewise, use `AzureContainerAppExecutableExtensions` rather than the
project-specific class for an explicit V2 `PublishAsAzureContainerApp` call.
Constraints and return types alone cannot distinguish otherwise identical C# overloads.

### Published library contracts

Do not silently change a published return type or remove a legacy overload. Preserve the
old contract and add a distinctly applicable V2 entry point, for example:

```csharp
public static class ApiResources
{
    public static IResourceBuilder<ProjectResource> AddApi(IDistributedApplicationBuilder builder)
    {
#pragma warning disable CS0618 // Retain the published legacy return contract.
        return builder.AddProject("api", "../Api/Api.csproj");
#pragma warning restore CS0618
    }

    public static IResourceBuilder<DotnetProjectResource> AddDotnetApi(
        IDistributedApplicationBuilder builder)
    {
        return builder.AddDotnetProject("api", "../Api/Api.csproj");
    }
}
```

These are alternative factories for the same resource name, not two registrations to call
in one AppHost. A new receiver overload can also be appropriate when its parameter type
distinguishes it. Do not require downcasts or add an overload differing only by return type.

## Model queries

`GetProjectResources()` continues to return only legacy `ProjectResource` instances.
It is not a mixed-model enumeration API. Given a `DistributedApplicationModel model`,
query explicitly:

```csharp
var v2Projects = model.Resources.OfType<DotnetProjectResource>();
var allDotnetPrograms = model.Resources.OfType<IDotnetProgramResource>();
var endpointResources = model.Resources.OfType<IResourceWithEndpoints>();
```

Use the first for V2-specific work, the second for .NET identity across both models,
and the third when only endpoints matter. Update operational filters and callback types,
not just declarations, or migrated services can disappear from endpoint capture, commands,
and deployment hooks. .NET identity does not prove a resource has a publishing capability.

## Fluent and integration APIs

Ordinary V2 calls have concrete, stable facades:

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
    .WithReplicas(2)
    .DisableForwardedHeaders()
    .WithEndpointsInEnvironment(endpoint => endpoint.Name is "http" or "https");

IProjectMetadata metadata = api.Resource.GetProjectMetadata();
```

These calls preserve the concrete builder type and do not require `ASPIREPROJECTS001`
suppression. The shared integration-authoring operations remain experimental.
Metadata access validates the resource's metadata; it is not a metadata replacement API.
In polyglot AppHosts, `withEndpointsInEnvironment` takes endpoint names rather than a C#
predicate.

| Existing integration use | V2 treatment |
| --- | --- |
| `PublishAsDockerFile` | Use the existing executable-constrained overload. It returns the original builder; configure container arguments explicitly because process arguments are cleared in the publish projection. |
| `PublishAsAzureContainerApp` | Use the executable-constrained overload from `Aspire.Hosting.Azure.AppContainers`; ordinary fluent calls select it for V2. Default SDK container publishing does not require opting into a Dockerfile. |
| `AddBlazorGateway` | Use `AddDotnetProjectBlazorGateway` / `addDotnetProjectBlazorGateway` from `Aspire.Hosting.Blazor`, returning a V2 builder, then the matching `WithBlazorClientApp`. |
| `ProxyBlazorService`, `ProxyBlazorTelemetry`, `WithBlazorDebuggerBrowser` | Existing type-preserving generic overloads support V2. Blazor's independent `ASPIREBLAZOR001` opt-in remains. |
| Radius `WithContainerImage` | Use the existing shared-identity overload; keep Radius's experimental opt-in and prebuilt-image requirements. Files requested with `PublishWithContainerFiles` must already be in that externally built final image. |
| Kubernetes customization/volumes, DevTunnel references, `WithBrowserLogs` | Existing capability-based APIs apply; do not add redundant project-only wrappers or change their return types. |

The Blazor gateway intentionally runs its development tool as a process; its V2 resource
type is not evidence of watcher support. A retained `AddBlazorWasmProject<Projects.Client>`
is a different API, not an obsolete `AddProject` call. Keep its metadata producer and the
client's build inputs if using that form.

## EF migrations

With `Aspire.Hosting.EntityFrameworkCore`, all four generalized `AddEFMigrations` forms
(with/without a context name and with/without tool configuration) accept V2 builders
without `ASPIREPROJECTS001`. Their return type is still `IResourceBuilder<EFMigrationResource>`.
Downstream migration-resource APIs do not need a project-type rename.

```csharp
using Aspire.Hosting.EntityFrameworkCore;

var api = builder.AddDotnetProject("api", "../Api/Api.csproj");
IResourceBuilder<EFMigrationResource> migrations =
    api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext");

IDotnetProgramResource startup = migrations.Resource.StartupProjectResource;
```

Read `StartupProjectResource`, not the already-obsolete `ProjectResource` property.
The latter still returns the legacy type and throws for a V2 startup resource; catching
that exception is not an adapter.

The public generalized constructor is for **data construction only**:

```csharp
var unregistered = new EFMigrationResource("migration-data", api.Resource, dbContextTypeName: null);
```

Neither constructor registers the resource or supplies the factory's duplicate checks,
tool initialization, environment copying, dashboard commands, parent relationship, or
publishing pipeline setup. Use `AddEFMigrations` for a working integration.
File-based `.cs` apps remain rejected; SDK-style F#/VB project support does not change that.

### A separate migrations project

`WithMigrationsProject<Projects.Data>()` is not an `AddProject` overload and remains valid.
If retaining it, preserve the generated metadata producer and necessary project/build
references. If removing those references, deliberately choose the existing path overload:

```csharp
var migrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationsProject("../Data/Data.csproj");
```

This path is AppHost-relative. A path alone does not register the migrations project with
the coordinated builder or preserve a resource's configured providers and build readiness.
Ensure the startup project's normal references build it, or build it explicitly before
run-mode EF commands. The polyglot resource-backed form preserves resource identity and
those capabilities; do not replace it mechanically with a path. There is no corresponding
new C# resource-overload contract.

EF waits for known coordinated build completion, not application startup. It still does
**not** receive Aspire-specific build values as MSBuild global properties. EF can select
different/stale output or fail for missing output despite API graduation. Preserve the
warning and review [coordinated builds and custom build inputs](../src/Aspire.Hosting.EntityFrameworkCore/README.md#coordinated-builds-and-custom-build-inputs).

## Build ownership and metadata

Generated legacy metadata normally has `SuppressBuild=true` because the AppHost's project
graph already built the service. Migration deliberately transfers service builds to V2.
This ordinary generated setting is not the same as a custom external-build contract.

Remove only resource references whose sole purpose was generated metadata and service
builds. Keep real assembly/library references, test-fixture inputs, migrations dependencies,
and references still used by intentional legacy resources. Removing a reference also
removes a CI/test-selection edge; a runtime path does not copy missing service sources or
outputs into a test payload.

An AppHost-only `dotnet build` no longer builds services whose references were removed.
Keep services in solution/CI build and publish inputs. During a V2 run, compatible SDK
projects share coordinated restore/build groups; file apps and custom build environments
use serialized direct builds. Start/Restart reuse built output; Rebuild builds and restarts.
`WithEnvironment` is runtime configuration, not a substitute for `WithBuildEnvironment`.
See the [.NET integration README](../src/Aspire.Hosting.Dotnet/README.md) for build and
publishing details.

The V2 factory owns one stable project-metadata annotation and applies defaults from it.
Do not attach a second `IProjectMetadata`, remove/replace metadata after defaults, or assume
a custom metadata object can be passed to `AddDotnetProject`. Move demonstrably equivalent
settings to the actual path, launch-profile files/options, and normal configuration APIs.
Non-equivalent in-memory launch metadata or externally owned outputs remain legacy cases.

### Compose instead of subclassing

For user-owned configuration, prefer the supported factory plus annotations:

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
    .WithAnnotation(new ServiceGroupAnnotation("payments"));

public sealed class ServiceGroupAnnotation(string group) : IResourceAnnotation
{
    public string Group { get; } = group;
}
```

There is no public V2 subclass-registration/factory seam. Directly constructing a
`DotnetProjectResource` subclass and calling `AddResource` does not register it with the
internal coordinated-build planner. Its type or marker implementation does not supply
metadata, completed defaults, or SDK publishing. Retain a legacy custom subtype when its
CLR identity is part of an existing integration contract.

## Retained legacy scenarios

* **Azure Functions:** retain `AddAzureFunctionsProject` and
  `AzureFunctionsProjectResource`; migration is tracked separately in
  [#21345](https://github.com/microsoft/aspire/issues/21345). Ordinary V2 launch is not a
  replacement for Functions host/debug behavior.
* **MAUI:** retain `AddMauiProject` and its Windows, MacCatalyst, Android device/emulator,
  and iOS device/simulator resources. Their specialized build, launch, debug, and OTLP
  behavior migrates separately. Companion backends may migrate independently only when
  their scenario remains valid.
* **Custom subtypes, custom metadata, and external builds:** keep the legacy contract when
  a path/options conversion would lose semantics. There is no metadata-injection or
  external-build-ownership replacement API in this migration.
* **Intentional legacy tests and watch scenarios:** retain compatibility coverage and
  existing `DOTNET_WATCH` launch behavior rather than replacing it with a different model.
* **Microsoft Foundry projects:** `foundry.AddProject` / `foundry.addProject` models a
  Foundry project, not a .NET service. It is unrelated and must not be renamed.

For a genuinely retained `AddProject` call, suppress `CS0618` locally with a comment
explaining the compatibility need, as in the published-contract example above. Do not
silence the warning repository-wide or assume suppressing the SDK's `ASPIRE012` also
suppresses it.

## Watch and tooling boundaries

The new service/AppHost watcher and `aspire run --watch` integration are **not implemented**.
Coordinated initial builds, Rebuild, and non-watch debugging are not watch parity.
Legacy `DOTNET_WATCH` behavior and IDE-native change tracking do not prove support for the
new watcher. F#/VB project recognition is not proof of every IDE/debugger scenario.

The design authority is [PR #21349](https://github.com/microsoft/aspire/pull/21349),
specifically [the pinned Project V2 design](https://github.com/microsoft/aspire/blob/7508edabd7318d0414fd8a63b35ccf98de9a39fa/docs/plans/project-v2-csharpprogram-watch.md).
The approved AddProject migration extends that design's language support and narrowly
stabilizes the identity marker, concrete consumer facades, and generalized EF APIs; it
does not graduate shared defaults/build/publishing authoring seams or implement watch.

The agent-assisted
[`aspire-project-v2-migration` skill](https://github.com/microsoft/aspire-skills/tree/main/skills/aspire-project-v2-migration)
is maintained in `microsoft/aspire-skills`, not in an embedded archive here. Its supported
language/limitation inventory needs to reflect these changes when published separately.
Do not assume an installed skill already understands F#/VB, the stable consumer surface,
or every retained exception, and do not mistake migration for an Aspire version upgrade.
