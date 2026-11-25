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
each traversal group share one NuGet graph restore and can build in parallel. Outside legacy project-only IDE
sessions, file-based apps use serialized direct builds so their `#:project` references cannot build shared
outputs concurrently. Launch-profile and `WithEnvironment` values are runtime configuration and do not
prevent projects from sharing a traversal build. Each traditional project is then launched with the
`RunCommand` and `RunArguments` resolved from the already-built project, so runtime environment variables
cannot change which output is selected. File-based apps launch with `dotnet run --file <path> --no-build`
after their direct build; uncoordinated file-based launches use `--no-cache` instead. Start and Restart reuse
the coordinated output, matching project-based resources. Use the Rebuild command after source changes to
rebuild and restart the resource.

Legacy project-only IDE sessions keep the existing file-app launch path. File apps wait for the final
coordinated project prebuild, preventing overlap with that prebuild, but separate file apps are not mutually
serialized and can still race when sharing `#:project` outputs. In capability-less Visual Studio sessions,
`WaitForStart` only waits for the `dotnet run` process to start, not for its implicit build to finish.
To avoid shared-output races, run outside the legacy IDE session (for example, with `aspire run`) or use an
external-build-capable IDE so Aspire coordinates file-app builds. Within a legacy session, configure an
application-readiness health check on the preceding app and use `WaitFor` on the next app; the check must
only pass after the preceding app has finished building. Without such a check, `WaitFor` does not provide
a build-completion barrier either.

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

The package ships `Microsoft.DotNet.HotReload.Watch.Aspire` **10.0.403** and **11.0.100**, one
watcher per supported SDK major, plus its service WatchHost and an SDK-bound evaluation adapter.
Nothing is installed or downloaded at run time. A watcher loads MSBuild from the SDK that the
watched projects select, so each watch group, and a C# AppHost, uses the watcher for its SDK's
major version: any .NET 10 or .NET 11 SDK, including patches released later. Projects that select
another SDK major run without watching, with a diagnostic. The selected muxer must provide the
watcher's `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` runtimes under the effective
roll-forward policy: **10.0.14** or a later 10.0 patch for the .NET 10 watcher, and **11.0.0** or a
later 11.0 patch for the .NET 11 watcher. Each SDK installs its own runtime patch, so installing only a
pre-release SDK or an older runtime does not establish these prerequisites. An explicit
`ASPIRE_DOTNET_WATCH_TOOL_PATH` selects a complete tool directory for every SDK major; relative
overrides are resolved from the AppHost context, and invalid overrides are not ignored.

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
affected group without silently reverting to ordinary execution. When the group's watcher or its transport
stopped unexpectedly, or a guarded refresh failed, Rebuild (or Start) on one of the group's services retries
with a fresh watcher. Failures that need a new partition of the projects (a changed SDK or output context, or
newly overlapping output ownership) and watchers whose cleanup wasn't confirmed can't be retried in the run:
fix the reported problem and restart the complete run.

Normal AppHost shutdown waits for contained process cleanup before removing private watch run
directories. If WatchHost is forcibly killed without a chance to acknowledge cleanup, Aspire may
preserve its private directory for safety even after the guardian stops its children. Restarting
the complete run uses fresh owners and directories; a root process exit alone is not cleanup proof.

| Resource operation | Effectively watched service |
|---|---|
| Source edit | The watcher owns automatic builds, hot reload, and required restarts. |
| Initial Start / `WithExplicitStart` | Supported; explicitly parked resources wait for Start. |
| Restart | Relaunches every running instance of the project, including replicas and duplicate entry points, without a build; hot reload changes are reapplied. Not available for a crashed or manually stopped instance. |
| Rebuild | Replaces the watch group's watcher with a fresh one that builds from source and restarts the group's services. Manually stopped instances stay stopped. |
| Stop | Stops the selected proxy connection and records manual-stop intent. A service that hasn't launched yet (for example, one waiting for its dependencies, its first build, or its watch group's recovery) isn't stopped: its start is canceled, and it shows as not started. A Start that was waiting for that start then reports that it was canceled. |
| Start after manual Stop | Restarts the watch group with a fresh build and starts that instance; other stopped instances stay stopped. |
| Start of a crashed instance, or one whose proxy exited without a Stop | Restarts the watch group with a fresh build, which starts the instance again. A new proxy connection to the running watcher isn't safe (see below). |
| Start of a running instance | Leaves it as it is and suggests Restart or Rebuild. The dashboard hides Start for a running instance, but the CLI and MCP tools don't check command state. |
| Start of a service whose first start is in progress | Leaves the start to the one in progress and reports that the service is already starting. A service that waits for its dependencies starts at once instead, as any resource does. |
| Start after the watch group failed (retryable) | Restarts the watch group with a fresh build and starts the instance. An instance that hasn't launched in this run gets its usual first start afterwards, which waits for its dependencies. |

Services that share a watcher form a watch group (one per SDK scope and configuration; a project with
a build environment callback or a file-based app gets its own). Rebuild, and Start of a service that stopped,
crashed or failed after it ran, restart every service of the group; their dashboard confirmation and result
name the other services. Both wait for the fresh build and report its result. If the code doesn't build, the group's services stay down until the errors
are fixed and saved, and the hidden watch group resource (for example `dotnet-watch`, named by each
service's `resource.watch.log-resource` property) has the compiler errors in its console logs.

When Aspire relaunches a watched service that it already launched in this run (Rebuild, Start after
Stop, CTRL+R when it rebuilds watched services, or a fresh watcher after a project file change), the
service doesn't wait for its `WaitFor` dependencies again, even if it failed before it was running: they
were satisfied when it was first launched, and a dependency that ran to completion, such as EF migrations
that run on start, doesn't run again. A service's first launch waits as usual, including after CTRL+R
replaces the AppHost, and Stop can cancel it while it waits. A canceled service isn't stopped manually: its
next Start is the usual start, which waits for its dependencies without restarting its watch group. So a
watch group's restart only relaunches services, and never waits for a dependency while it holds the group.
It starts the group's services after the services of the same group that they wait for.

The watcher can relaunch a crashed application on the next edit even after its instance was stopped,
leaving a process that no proxy owns on the instance's port. When that happens for a project whose
instances are all stopped, Aspire replaces the group's watcher to end the process and reports it with
a 🧹 notice; the stopped instances stay stopped. For the same reason, Start never connects a new proxy of
an instance that already ran to the running watcher: it starts the instance with a fresh watcher.

When a change doesn't take effect, escalate:

1. Save the file again; the watcher hot reloads it or restarts the affected service.
2. **Restart** when a change was applied but had no effect. It relaunches the project's running
   instances and reapplies the hot reload changes.
3. **Rebuild** after a crash, a change that hot reload can't apply, a change the watcher missed, or
   when Restart doesn't help. It starts a fresh watcher and build for the service's watch group.
4. Press CTRL+R in `aspire run`, or stop the run with `aspire stop` and start it again.

In an interactive `aspire run`, CTRL+R reloads what the run watches, and the hint under the dashboard
URL says which applies:

- With AppHost watching active for a C# project AppHost, CTRL+R rebuilds and restarts the AppHost and
  all its resources.
- Otherwise, when the AppHost has watch groups, CTRL+R runs Rebuild for every watch group and prints
  each group's result. The AppHost and other resources keep running, and manually stopped instances
  stay stopped.

CTRL+R isn't offered when input is redirected, in non-interactive or JSON output modes, with
`--detach`, or under the VS Code extension. When a replacement AppHost generation fails to build or
exits before it connects, `aspire run` keeps running: fix the errors and save, or press CTRL+R to
retry, or CTRL+C to stop. If the AppHost being replaced stops with an error, no new AppHost starts:
`aspire run` ends and says why, and you start it again.

Agents use the same commands: `aspire resource <name> restart`, `aspire resource <name> rebuild`,
and `aspire resource <name> start`, or the MCP `execute_resource_command` tool, which waits for the
command's result. The command descriptions (for example in the MCP `list_resources` tool or
`aspire describe --format json`) state each command's scope. Use `aspire logs <watch group> --tail 50`
for compiler errors.

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

The watcher builds a watch group with several root projects through a temporary solution in its temp
directory (`dotnet build` can't build several projects, [dotnet/sdk#51311](https://github.com/dotnet/sdk/issues/51311)),
and the temp directory's `global.json` scope can select another SDK.
When it does, the group's watcher gets a private temp directory that pins the group's SDK, and the
group's services get their own temp directory back when they launch. Preflight doesn't evaluate the
projects with that private temp directory, so a project whose evaluated outputs or imports depend on
`TMPDIR` (`TMP` or `TEMP` on Windows) isn't supported in such a group.
The temporary solution also needs distinct project names: if two of a group's projects have the same
project file name (compared without the extension and ignoring case), the group's services run without
watching, and their resource logs say which projects to rename
([dotnet/sdk#57360](https://github.com/dotnet/sdk/issues/57360)).

Representable non-Debug configurations, file-based C# services, working directories, launch profiles,
arguments, and build environments are retained. The watcher requires managed hot-reload agents;
unrepresentable contexts are diagnosed in preflight rather than silently selecting different output.
For Release configurations, explicitly set `Optimize` to `false` and `MetadataUpdaterSupport` to
`true` in the service project. Project properties that disable startup hooks or metadata updating
cannot be repaired through lower-precedence watcher environment variables.
For non-Debug configurations, the watcher's build environment sets `EnableHotReloadInRuntimeConfigDevFile`
to `true`, the SDK switch for its hot reload build outputs: the .NET 11 watcher can't launch a web
project without them. A web project that sets `StaticWebAssetsEnabled`, `JSModulesEnabled` or
`EnableHotReloadInRuntimeConfigDevFile` to `false` fails to launch under the .NET 11 watcher; opt it
out with `WithWatch(false)` ([dotnet/sdk#57358](https://github.com/dotnet/sdk/issues/57358)).
Keep a file-based service and its content in a dedicated directory. The pinned SDK also observes
new files under that directory; unrelated projects' generated outputs can trigger its known
virtual-project restore failure. A resulting watcher failure fences the group rather than
silently switching to ordinary execution.
Some SDK-accepted edits, including method return-type delete/reinsert changes, can instead replay
a deleted-method delta on each automatic child restart. That upstream limitation is not classified
as a rude edit by the project-only status protocol, and this integration does not infer a cause or
automatically rebuild from repeated start events. Use Rebuild on the service (or CTRL+R in
`aspire run`) with the edited source retained: the fresh watcher builds that source and discards the
replayed changes. Restart is not the workaround, because it reapplies them. Genuine ENC rude edits of a single file app
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

IDE debugging with `WithBuildEnvironment` requires a launcher that advertises
`project-with-external-build.v1` and consumes the coordinated build context. Aspire rejects legacy
project-only IDE sessions because those launchers cannot reliably locate customized build output.

Container publishing rejects build environment properties that can redirect the artifact tracked by Aspire:
`ContainerRepository`, `ContainerImageTag`, `ContainerImageTags`, `ContainerRegistry`,
`ContainerImageName`, `PublishImageTag`, `AutoGenerateImageTag`, `RegistryUrl`,
`ContainerArchiveOutputPath`, `ContainerImageFormat`, `LocalRegistry`, `RuntimeIdentifier`,
`RuntimeIdentifiers`, `ContainerRuntimeIdentifier`, and `ContainerRuntimeIdentifiers`. Configure
supported image identity, destination, format, and target platform settings with
`WithContainerBuildOptions` instead.

Do not use `WithBuildEnvironment` for secrets. Aspire carries these values in build process environments
and, for external-build-capable IDEs, launch metadata. Protected temporary MSBuild response files
preserve global-property semantics without exposing values in process command lines, but values can
appear in build diagnostics and are not a general-purpose secret transport.

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
from another container still requires a container runtime. With Docker, the layering build uses Docker's local
image store and `docker image save` exports the result. Docker Engine 25 or later is required for
`ContainerImageFormat.Oci`, because older engines do not write an OCI image layout; Podman supports both formats.

Layered archive publishing uses private temporary image tags and does not overwrite or delete an existing
local image with the configured destination name and tag. The archive still contains that configured image
identity. Temporary images are cleaned up after publishing, including when a build fails or is canceled.

For .NET SDK publishing, a non-existent `OutputPath` with any filename extension is an explicit archive
filename. This includes custom extensions such as `image.custom`, not only `.tar`. A path without an extension,
a path ending in the platform's directory separator, or an existing directory is a directory, and the archive is
written to `<LocalImageName>.tar` in that directory. A registry-qualified name is flattened into one file name;
for example, `team/program` is written to `team-program.tar`. Prefer an explicit archive filename when configuring
output consumed by other tools.

Archives are always written as uncompressed tar files, matching the .NET SDK. A `.tar.gz` or `.tgz` filename
does not compress the archive, and Aspire logs a warning when one is configured. The filename, location, and
format are the same whether or not `PublishWithContainerFiles` layers files into the image.

These conventions are based on the .NET SDK's, but Aspire chooses the directory filename itself.
Dockerfile/container-runtime publishing has its own output conventions. See the [SDK archive publishing documentation](https://learn.microsoft.com/dotnet/core/containers/sdk-publish#publish-net-app-to-a-tarball).

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
