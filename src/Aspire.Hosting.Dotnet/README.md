# .NET app hosting integration

Use this integration to model, configure, and orchestrate SDK-style C#, F#, and Visual Basic
projects and file-based C# apps (added **by path**) in an Aspire solution. It is the .NET peer of `Aspire.Hosting.Go`,
`Aspire.Hosting.Python`, and `Aspire.Hosting.JavaScript`.

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

In the AppHost, add a .NET app resource by path. The path can point at an SDK-style project
file (`.csproj`, `.fsproj`, or `.vbproj`), a directory containing exactly one supported project
in total, or a file-based C# app (`.cs`). Paths are relative to the AppHost directory, not the
shell's working directory; replace the example paths with your actual service paths.

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
each traversal group share one NuGet graph restore and can build in parallel. File-based apps use serialized direct builds so their
`#:project` references cannot build shared outputs concurrently. Launch-profile and `WithEnvironment`
values are runtime configuration and do not prevent projects from sharing a traversal build. Each
traditional project is then launched with the `RunCommand` and `RunArguments` resolved from the
already-built project, so runtime environment variables cannot change which output is selected.
File-based apps launch with `dotnet run --file <path> --no-build` after their direct build; uncoordinated
file-based launches use `--no-cache` instead. Start and Restart reuse the coordinated output, matching
project-based resources. For ordinary resources, use the Rebuild command after source changes to rebuild
and restart the resource. The experimental watch mode below owns automatic builds instead.

Endpoints, environment variables, and service discovery are configured from the project's
`launchSettings.json` and Kestrel configuration. V2 VB services prefer
`My Project/launchSettings.json`, falling back to `Properties/launchSettings.json`.
Legacy service-profile discovery is unchanged.

### Migrating from AddProject or AddCSharpApp

All six legacy `AddProject` overloads, `AddCSharpApp`, and their polyglot exports are obsolete.
Add this integration and use `AddDotnetProject` / `addDotnetProject` with the actual project path,
not a guessed conversion of a generated `Projects.*` name. The extension methods are in
`Aspire.Hosting`; explicitly typed resources also need `using Aspire.Hosting.Dotnet`.

The new API returns `IResourceBuilder<DotnetProjectResource>`, not a
`ProjectResource` / `CSharpAppResource` builder. Update returns, stored values, generic definitions,
explicit generic calls, and model queries separately. Ordinary concrete `WithReplicas`,
`DisableForwardedHeaders`, `WithEndpointsInEnvironment`, and `GetProjectMetadata` calls are stable;
shared integration-authoring seams remain experimental.

Use the canonical [AddProject migration guide](../../docs/migrate-from-addproject.md) for all six
overload mappings, C#/polyglot null differences, compile-correct typed/fluent examples,
build ownership, EF, composition, and retained legacy exceptions. Existing legacy calls keep
their launch/publishing behavior; Functions and MAUI migrate separately.

F#/VB support covers SDK-style project files, not scripts or old project systems. Those resources
keep ordinary launch behavior in the experimental C# watch mode.

### Experimental watch

Explicitly opt into the preview with:

```bash
aspire run --watch
aspire run --watch --detach
```

This is an experimental CLI-owned mode, not watch debugging or a replacement for existing
IDE watch/debug sessions. Omitting `--watch` preserves ordinary execution and the existing
`features.defaultWatchEnabled` implementation. It does not acquire or start the package-backed
watcher. Ordinary launches explicitly disable inherited private preview ownership
and generation settings without changing legacy `DOTNET_WATCH` or IDE debugging.
Discovery, SDK generation, restore, build, and publish do not activate it either.

For compatible single-instance C# projects and managed file-based apps, Aspire retains the initial
coordinated build, starts hidden watch servers for isolated SDK/build components, and launches
Aspire-owned resource proxies. The watcher performs an additional initial build/workspace load.
Service endpoints, intended model environment, launch-profile arguments, and application arguments
remain attached to the original resources. Compiler diagnostics from a hidden server are forwarded
to its sharing resources' logs.
Each server uses the CLI's existing owned-process guardian, so an abrupt server/owner failure
also terminates its service descendants rather than leaving orphaned applications.

Project-based C# AppHosts use the selected tool's `host` command when their SDK/runtime and launch
context are compatible. Metadata updates invalidate the model: the CLI tears down the old run and creates a
fresh AppHost generation and backchannel before reconstructing resources. Polyglot AppHosts retain
language-native watching; their managed Hosting server owns C# service watch. Native protocol-v2
servers cannot load this managed integration and report reduced coverage instead.
File-based C# AppHosts run normally in this preview; their independently compatible services can
still watch, but AppHost source edits do not replace the model automatically.

Private preview ownership and generation-control settings are excluded from ambient DCP inheritance,
so an ordinary child that loads Hosting cannot announce a false AppHost start or invalidate its parent.

C# AppHost watching requires a DCP `fork-process` that supports `--enable-child-cleanup`,
`--skip-descendants`, cleanup-mode application output forwarding, and a helper-owned private Unix
session for graceful startup-descendant cleanup. The CLI checks PID framing and output support
with the resolved SDK before starting the watcher, then monitors the **long-lived CLI** rather than
the detached foreground launcher. The first fork PID line is consumed internally; application
output and exit codes remain observable. The managed AppHost retains its orphan detector and DCP
workload cleanup. Failed retirement is an error, not permission to start another generation.

The pinned DCP **0.27.3 does not provide this contract**. A supporting DCP release and bundle update
are required; compatible local prototypes can be selected with `ASPIRE_DCP_PATH` (the DCP directory).
There is no watch/status-file protocol or separate watcher supervisor.

Inspect `watch.mode` (`experimental` or `ordinary`) and `watch.reason` in resource details or
`aspire describe --format json`. Unsupported resources run normally with a per-resource warning.
If an unsupported resource shares project/output directories with other roots, the entire sharing
component remains ordinary; independent compatible components can still watch. Unknown output
isolation leaves the C# resources ordinary instead of blocking application startup.

Unsupported contexts include replicas or duplicate registrations of one entrypoint, persistent and
completion-sensitive resources, multi-targeted/non-Debug configurations, custom launch tools or
SDK run commands, AOT/trimmed configurations, and configurations without managed metadata/startup-hook
support. File-based apps
normally default to `PublishAot=true`, which disables those hooks; opt into a managed file app with
`#:property PublishAot=false` when it should watch. Custom build environments and incompatible SDK
runtime requirements can also prevent safe watch coverage.

Manual **Rebuild** is disabled only for effectively watched resources. Source edits drive automatic
hot reload or rebuild/restart. Manual **Restart** requests a process restart, not a build. To force a
fresh build, stop and restart the watch run. An initial explicit **Start** is supported, but after a
manual **Stop**, **Start** stays disabled until the whole watch run is restarted.

These restrictions reflect the selected upstream protocol: it reports last-observed process state,
not successful application exits, real application PIDs, or acknowledged completion of disconnect
cleanup. The proxy's PID/exit is not reported as the application's; separately labeled
`watch.transport.*` properties describe transport diagnostics. Do not use this preview as ordinary
application-completion or Stop/Start parity. Pre-start watch infrastructure failures fall back to
ordinary execution only after the CLI owner confirms cleanup of its whole process group/job.
This group-scope confirmation does not supply the upstream per-service completion or Stop/Start
acknowledgements. If cleanup cannot be confirmed, the group stays
failed rather than risking duplicate applications or competing builds. Source build failures and
cancellation do not select fallback. A fatal active
group fails and stops that group's owned processes without silently downgrading it or stopping
unrelated resources. Restart the watch run to recover.

### Experimental watcher acquisition

The integration provides preview acquisition infrastructure for
`Microsoft.DotNet.HotReload.Watch.Aspire`, pinned by default to **10.0.403**.
The infrastructure is experimental (`ASPIREWATCH001`); acquiring a tool does not enable
service or AppHost watching.

An already-restored C# AppHost that references this integration can explicitly resolve the
tool through its MSBuild target:

```bash
dotnet msbuild AppHost.csproj -target:EnsureAspireDotnetWatchTool -getItem:AspireDotnetWatchTool
```

The returned item identifies the entry DLL and includes `Version`, `RuntimeMajor`,
`SdkDirectory`, and `DotnetPath` metadata. The target is not attached to ordinary Restore,
Build, Run, or Publish. Those operations ignore the acquisition overrides and do not install
or start the watcher.

Tool selection uses these experimental environment variables, in priority order:

| Variable | Meaning |
|---|---|
| `ASPIRE_DOTNET_WATCH_TOOL_PATH` | Use an existing entry DLL or complete tool directory. Relative paths are resolved against the AppHost directory. |
| `ASPIRE_DOTNET_WATCH_TOOL_VERSION` | Acquire an exact NuGet package version, including a prerelease version. Version ranges and floating versions are not supported. |

An explicit path wins over a version override. Invalid or incomplete explicit installations
produce an error; they never cause a different tool to be downloaded. Keep the entry DLL,
runtime/dependency configuration, supporting assemblies, and `hotreload` agents together.
Version overrides are developer opt-ins to potentially unverified payloads, not a promise
that every package version implements a compatible runtime or protocol.

On a cache miss, Aspire uses `dotnet tool install` with the AppHost directory's NuGet
configuration, sources, and source mappings. The private cache is beneath the Aspire home
directory, honoring `ASPIRE_HOME`, with separate entries for package version and host runtime
identifier. This is not a global tool installation or a project tool manifest.

A complete cache hit or explicit installation is resolved using filesystem checks only:
there is no installer invocation, source query, update check, or `--help` probe. This offline
guarantee covers tool acquisition/resolution, not application restore/build or unrelated CLI
network operations. Incomplete published cache entries are not repaired in place; stop runs
using that entry before removing it and retrying.

The default tool requires both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`
**10.0.14**, independently of the SDK used to evaluate a service. Acquiring the package does
not install these runtimes. The pinned tool cannot load the repository's .NET 11 MSBuild
runtime dependencies; a successful acquisition alone does not establish SDK compatibility.
Its `--help` commands also fail, so functional protocol checks, not help output, establish
compatibility. SDK incompatibility is reported as ordinary coverage, not an automatic pin upgrade.
The implementation decisions and protocol limitations are recorded in the
[Project V2 watch plan](../../docs/plans/project-v2-csharpprogram-watch.md).

### Restoring projects individually

Coordinated project builds restore all projects in a compatible group together, without introducing
solution-specific MSBuild properties. This avoids repeatedly restoring overlapping project graphs.
Like solution-level restore, it does not invoke each root project's `Restore` target separately.
Custom targets attached directly to that target, such as `BeforeTargets="Restore"` or
`AfterTargets="Restore"`, therefore do not run for each project.

Individual restore is a supported alternative for projects that depend on those hooks.
Set `Aspire:Dotnet:RestoreProjectsIndividually` to `true` in the **AppHost's configuration**.
For example, set `Aspire__Dotnet__RestoreProjectsIndividually=true` in the
environment before starting the AppHost, or add the following to its `appsettings.json`:

```json
{
  "Aspire": {
    "Dotnet": {
      "RestoreProjectsIndividually": true
    }
  }
}
```

The setting defaults to `false` and is read when the build plan is created. It applies to every
generated traversal in that AppHost; it is not a resource runtime environment variable. With
individual restore, each root project's restore runs serially while builds remain parallel.

A traversal also uses the per-project restore path when the selected NuGet restore targets are
unavailable or its top-level MSBuild context enables `RestoreUseStaticGraphEvaluation`. NuGet's
static restore requires a restore-capable entry project, which the generated SDK-less traversal is
not. The reason for selecting individual restore is reported in the build resource's logs. Restore
failures are not retried using another strategy.

Direct builds for file-based apps and project-specific build environments, individual Rebuild
commands, and publishing are unchanged.

### Select a launch profile

Configure a .NET service's launch profile independently of the AppHost language:

**C#**

```csharp
builder.AddDotnetProject("api", "../api/api.csproj",
    options => options.LaunchProfileName = "https");
```

**TypeScript**

```typescript
await builder.addDotnetProject("api", "../api/api.csproj", {
    launchProfileName: "https"
});
```

Omitting the options, or leaving `launchProfileName` null, selects the default profile.
Set `excludeLaunchProfile: true` to disable profiles; this takes precedence over a supplied
profile name. Set `excludeKestrelEndpoints: true` to stop deriving model endpoints from Kestrel
configuration; this does not rewrite or disable the service's own Kestrel configuration.
The corresponding C# `ProjectResourceOptions` properties have the same behavior.

When migrating the obsolete C# string overload, `launchProfileName: null` instead meant
**disable profiles**: translate it to `options => options.ExcludeLaunchProfile = true`.
Legacy polyglot omitted/null arguments mean default options, not profile exclusion.
See the [migration guide](../../docs/migrate-from-addproject.md#c-launch-profiles-null-is-not-always-the-default).

The plain-data `DotnetProjectOptions` input is specific to the polyglot `addDotnetProject` API.
It does not change the shared `ProjectResourceOptions` handle used by the legacy polyglot
`addProject` and `addCSharpApp` APIs.

### Configure the build environment

Use `WithBuildEnvironment` when an environment variable must affect MSBuild evaluation for an SDK-style
`.csproj`, `.fsproj`, or `.vbproj` resource. File-based `.cs` apps do not support build-only environment variables and are rejected when
`WithBuildEnvironment` is called. Projects with build-specific environment variables use serialized direct
builds instead of a shared traversal build. Build environment variables are not added to the launched process;
configure the same variable with `WithEnvironment` as well when it is needed at runtime. The same build-only
values are applied when the project is published through the .NET SDK container targets.

Container publishing rejects build environment properties that can redirect the artifact tracked by Aspire:
`ContainerRepository`, `ContainerImageTag`, `ContainerImageTags`, `ContainerRegistry`,
`ContainerImageName`, `PublishImageTag`, `AutoGenerateImageTag`, `RegistryUrl`,
`ContainerArchiveOutputPath`, `ContainerImageFormat`, `LocalRegistry`, `RuntimeIdentifier`,
`RuntimeIdentifiers`, `ContainerRuntimeIdentifier`, and `ContainerRuntimeIdentifiers`. Configure
supported image identity, destination, format, and target platform settings with
`WithContainerBuildOptions` instead.

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

### EF Core operations

EF Core migration operations wait for the coordinated build in run mode, without waiting for the application
to start. However, `dotnet-ef` does not receive `WithBuildEnvironment` customizations as MSBuild global properties.
The EF integration warns once per requested operation and continues, including when generating publish scripts
or bundles. EF may use suitable output, select different or stale output, or fail because the expected output
is missing. Ordinary run-mode EF commands retain `--no-build`; publish-time script and bundle generation
allow EF to build the participating projects.

Where equivalent, use shared project-file or `Directory.Build.props` settings so the coordinated build and EF
evaluate the same values. Runtime `WithEnvironment` is not an equivalent workaround. See the
[EF Core integration guidance](../Aspire.Hosting.EntityFrameworkCore/README.md#coordinated-builds-and-custom-build-inputs)
for the remaining compatibility risk.

## Publishing

Resources created with `AddDotnetProject` participate in the same .NET SDK container publishing pipeline as
legacy project resources. Project files, directories containing a single supported project file, and file-based
C# apps are emitted as project resources in the Aspire manifest and can be deployed through supported compute
environments.

Constructing `DotnetProjectResource` directly does not supply metadata, SDK publishing, or registration
with the coordinated-build planner. Use the factory and compose annotations/configuration rather than
subclassing to obtain those behaviors.

File-based apps preserve the .NET SDK's publishing defaults, including Native AOT. Native AOT cannot compile
across operating systems, so publishing a file-based app from Windows or macOS to a Linux container can fail.
To publish a framework-dependent container instead, add this directive to the C# file:

```csharp
#:property PublishAot=false
```

Alternatively, run publishing on the target operating system to retain Native AOT. Aspire reports focused
guidance when it detects this cross-operating-system failure.

### Archive output paths

Set `Destination` to `ContainerImageDestination.Archive` in `WithContainerBuildOptions` to save an SDK-built
image as an archive. An archive-only SDK build does not require Docker or Podman to be running; layering files
from another container still requires a container runtime. Docker supports layered archives in Docker format
because the layering build must use Docker's local image store before `docker image save` exports the result.
Use Podman when container-file layering must produce an OCI-format archive.

Layered archive publishing uses private temporary image tags and does not overwrite or delete an existing
local image with the configured destination name and tag. The archive still contains that configured image
identity. Temporary images are cleaned up after publishing, including when a build fails or is canceled.

For .NET SDK publishing, a non-existent `OutputPath` with any filename extension is an explicit archive
filename. This includes custom extensions such as `image.custom`, not only `.tar` or `.tar.gz`. A path without
an extension is interpreted as a directory. End a dotted directory path with the platform's directory separator
to make its intent explicit, for example `artifacts.v1\` on Windows. Prefer an explicit archive filename when
configuring output consumed by other tools.

These are the .NET SDK's path conventions; Dockerfile/container-runtime publishing has its own output
conventions. See the [SDK archive publishing documentation](https://learn.microsoft.com/dotnet/core/containers/sdk-publish#publish-net-app-to-a-tarball).

### Other publishing options

Call `PublishAsDockerFile(...)` or `publishAsDockerFile(...)` to use an explicit Dockerfile instead of .NET SDK
container publishing. Call `ExcludeFromManifest()` or `excludeFromManifest()` when the resource is intentionally
available only during local orchestration.

## Migrating from legacy project resources

Start with the [AddProject migration guide](../../docs/migrate-from-addproject.md).
For C# and TypeScript AppHosts targeting a version that includes Project V2, the
[`aspire-project-v2-migration` agent skill](https://github.com/microsoft/aspire-skills/tree/main/skills/aspire-project-v2-migration)
assesses existing project resources and proposes a migration for approval before editing.
It preserves supported resource configuration and identifies unsupported patterns. Its inventory must
be updated separately for SDK-style F#/VB support, stable consumer APIs, and retained Functions, MAUI,
custom-metadata/subtype, and external-build exceptions; an installed skill may predate those changes.
It does not upgrade the AppHost's Aspire version.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- [Aspire documentation](https://aspire.dev/)

## Feedback & contributing

https://github.com/microsoft/aspire
