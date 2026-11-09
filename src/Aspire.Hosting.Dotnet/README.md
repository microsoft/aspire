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
project-based resources. Use the Rebuild command after source changes to rebuild and restart the resource.

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

F#/VB support covers SDK-style project files, not scripts or old project systems. These languages
retain ordinary build and launch in a watch run; the bundled service watcher supports C#.

### Watch AppHost and service changes

Compatible C# resources added with `AddDotnetProject` are watched during an ordinary `aspire run`
or direct AppHost run, without a CLI flag. Legacy `AddProject` resources are unchanged.
Watching is experimental and run-only; it does not change publishing.

Configure the resource-watch default when creating the AppHost. Explicit code wins over
`AppHost:Run:WatchEnabled` / `AppHost__Run__WatchEnabled`; an omitted setting defaults to `true`.
An invalid configuration value warns once and defaults to `false`. Changing the key after builder
creation does not change the resolved default. The last per-resource `WithWatch(true/false)` wins,
including an explicit opt-in when the AppHost default is disabled.

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DefaultResourceWatchEnabled = false
});
builder.AddDotnetProject("api", "../Api/Api.csproj").WithWatch(true);
builder.AddDotnetProject("worker", "../Worker/Worker.csproj");
```

**TypeScript**

```typescript
const builder = await createBuilder({ args: process.argv.slice(2), defaultResourceWatchEnabled: false });
await builder.addDotnetProject("api", "../Api/Api.csproj").withWatch({ enabled: true });
await builder.addDotnetProject("worker", "../Worker/Worker.csproj");
```

`--watch` and `features.defaultWatchEnabled` control **AppHost edits only**, not resource watching.
With AppHost watching enabled, a compatible project-based C# AppHost uses the bundled watcher's
`host` command; managed-server polyglot AppHosts use their language-native watch path.
`--watch false` disables AppHost watching while leaving resource policy intact. The feature remains
off by default. `--no-build` suppresses implicit AppHost and resource watching, but explicit
`--watch true` or `WithWatch(true)` requirements conflict with it. Ordinary resource-build behavior
is unchanged; `--no-build` is not an all-resource build switch.

C# AppHost metadata changes and replacement processes trigger complete generation replacement:
the old AppHost, its watcher groups, and their children are stopped before a fresh model and
backchannel start. The new model determines resource startup, including `ExplicitStart`.
File-based C# AppHosts are not wrapped, but their compatible C# services can still be watched.
Native protocol-v2 polyglot servers retain their selected backend and run normally without AppHost
or managed C# service watching. An effective watch request emits one warning: stop and rerun Aspire
to apply AppHost edits. Explicit `--watch false` suppresses that warning even when the default is enabled.

The package ships `Microsoft.DotNet.HotReload.Watch.Aspire` **10.0.403**, its service WatchHost,
and an SDK-bound evaluation adapter. Nothing is installed or downloaded at run time. The selected
SDK must be compatible with that .NET 10 watcher, and its muxer must provide both
`Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` **10.0.14** under the effective runtime
roll-forward policy. Installing only the repository SDK or a newer runtime does not establish
these prerequisites. An explicit `ASPIRE_DOTNET_WATCH_TOOL_PATH` selects a complete tool directory;
relative overrides are resolved from the AppHost context, and invalid overrides are not ignored.

Before handing builds to the watcher, Aspire evaluates active project references, imports, SDKs,
and output/intermediate directories, including ordinary resources. Compatible roots that share
outputs use one watcher. An unsupported or unprovable component retains ordinary build ownership
with a diagnostic before handoff; explicit `.WithWatch()` / `.withWatch()` instead requires watch
coverage. `.WithWatch(false)` / `.withWatch({ enabled: false })` opts out. Completion-sensitive resources run
ordinarily, as do implicit persistent lifetimes and custom launch tools. Explicit requirements
fail rather than replacing an incompatible launch or silently disabling required watching.

The watcher alone performs watched services' initial builds. Their build-output readiness barrier,
including EF operations, completes after a successful build, not after a proxy starts. An initial
source error can recover after a fix. Before releasing that barrier, Aspire revalidates the restored
graph and observes its newly active imports without performing a second build. Queued edits remain
fenced across observation-inventory replacement. After handoff, infrastructure failure fences and cleans the
affected group without silently reverting to ordinary execution; restart the complete watch run
after fixing the reported problem.

Normal AppHost shutdown waits for contained process cleanup before removing private watch run
directories. If WatchHost is forcibly killed without a chance to acknowledge cleanup, Aspire may
preserve its private directory for safety even after the guardian stops its children. Restarting
the complete run uses fresh owners and directories; a root process exit alone is not cleanup proof.

| Resource operation | Effectively watched service |
|---|---|
| Source edit | The watcher owns automatic builds, hot reload, and required restarts. |
| Initial Start / `WithExplicitStart` | Supported; explicitly parked resources wait for Start. |
| Rebuild | Disabled and rejected by the command callback; ordinary resources retain Rebuild. |
| Stop | Stops the selected proxy connection and records manual-stop intent. |
| Start after manual Stop | Disabled until the complete watch run is restarted. |
| Restart | Restart-only control for one unambiguous, last-observed running application; no build or crash recovery. |
| Replica/duplicate-entry Restart | Disabled because upstream control is project-wide, not instance-specific. |

Replicas and duplicate entry points remain watched. Their application events are explicitly
project-group observations, separate from each proxy's transport state and real endpoint health.
The proxy PID, exit code, and connection acknowledgement are not application identity, successful
completion, or readiness. Even a single watched application has last-observed state rather than a
complete exit feed. A processed-change notice does not prove that compilation or hot reload succeeded;
compiler diagnostics remain in the hidden `dotnet-watch` resource logs.
The CLI forwards only intentional, exact-category/EventId notices. Full watcher stdout/stderr and
exception details stay in the owning resource or CLI log, including startup and early-exit failures.
Typed, generation-scoped backchannel coverage reports pending, watching, build-failed, excluded,
and infrastructure-failed logical resources separately from the resolved default and AppHost watching.
Older or native AppHosts without that capability report unavailable coverage, not a guessed result.

Observed project/import changes cause guarded re-evaluation and, when needed, a fresh service watcher.
Internal refresh preserves manually stopped and explicit-start instances. SDK/output/context changes
or newly overlapping ownership fence affected groups and require a complete run restart. Ordinary
initial builds and Rebuild also recheck their actual evaluated ownership before writing outputs. This is guarded observation,
not an atomic upstream pause or live graph repartition. Only active evaluated imports are inventoried;
new currently-inactive or wildcard import paths are not automatically guaranteed coverage.

Preflight evaluates build-environment callbacks before plan materialization to establish ownership.
Ordinary builds still capture their own per-attempt values, which are checked before writing outputs.
Guarded refresh can evaluate watched callbacks again; changing the observed build assumptions requires
run restart rather than silently selecting another context.

Representable non-Debug configurations, file-based C# services, working directories, launch profiles,
arguments, and build environments are retained. The watcher requires managed hot-reload agents;
unrepresentable contexts are diagnosed in preflight rather than silently selecting different output.
For Release configurations, explicitly set `Optimize` to `false` and `MetadataUpdaterSupport` to
`true` in the service project. Project properties that disable startup hooks or metadata updating
cannot be repaired through lower-precedence watcher environment variables.
Keep a file-based service and its content in a dedicated directory. The pinned SDK also observes
new files under that directory; unrelated projects' generated outputs can trigger its known
virtual-project restore failure. A resulting watcher failure fences the group rather than
silently switching to ordinary execution.
Some SDK-accepted edits, including method return-type delete/reinsert changes, can instead replay
a deleted-method delta on each automatic child restart. That upstream limitation is not classified
as a rude edit by the project-only status protocol, and this integration does not infer a cause or
automatically rebuild from repeated start events. Stop the complete run with `aspire stop`, then
rerun `aspire run --watch` with the edited source retained. The fresh run builds that source with
new owners; an instance Restart is not the workaround. Genuine ENC rude edits of a single file app
retain the dedicated fresh-watcher recovery path.
Private AppHost ownership and notification settings are not inherited by application children,
including ordinary or opted-out services.
Certificate trust paths (`SSL_CERT_DIR` and `SSL_CERT_FILE`) are also supplied through `dotnet run -e`
before service startup because native OpenSSL cannot see the watch agent's managed-only environment
updates. Only these public trust-bundle paths use that launch channel; passwords and other application
settings remain on the private resource pipe.
Real IDE-owned runs keep IDE build/debug/watch ownership. An explicit conflicting CLI watch request
is rejected; watch debugging is not supplied by this integration.

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
