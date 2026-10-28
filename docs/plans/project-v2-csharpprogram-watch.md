# Project V2 stage 1 - .NET language integration and `aspire run --watch`

Implementation reviewed against repository commit `ef092af06f` on **2026-10-27**.
Session status below describes implementation and coverage present in source, not a fresh execution of
every historical manual check or deployment test. Watch integration is still future work.

Earlier session records report successful C# playground and VS Code F5 checks, TypeScript-first/C#-second
coordinated-build runs, and package-backed TypeScript Blazor gateway validation. Those remain historical
verification records, not newly executed checks in this review.

## 1. Goal and motivation

Project V2 makes a C# program an ordinary language-integrated Aspire resource, rather than coupling its
build and launch to a C# AppHost's project-reference graph and generated `Projects.*` metadata. Services
are added **by path**, just as Go, Python, and JavaScript services are. The language integration package
owns how its services run and, eventually, watch; the same model works from C# and polyglot AppHosts.

The broader [Project V2 vision][vision] must remain possible as this vertical slice is implemented:

- **Programmatic build and launch configuration:** integration defaults and AppHost overrides can
  influence builds, launch/debug behavior, and deployment.
- **Reliable watch:** service edits and shared-library edits update the affected services, with the
  watch infrastructure automatically managed as system resources.
- **Partial runs:** select resources, compute their dependency closure, build that closure efficiently,
  and allow additional resources to start on demand.
- **Persistent execution:** services can survive an AppHost shutdown and be adopted by a later run.
- **Container execution:** a project can run in a container instead of as a local OS process, potentially
  initially limited to SDK-container-publishable programs.
- **Debugging:** preserve non-watch F5/startup debugging and attachment; investigate combinations with
  watch without assuming that watch itself supplies a debugger.
- **Explicit event/callback contracts:** define ordering, concurrency, cardinality, and which callbacks
  may execute during dependency analysis even for resources that will not run.
- **Orthogonal capabilities:** do not unnecessarily prevent combinations such as partial, persistent,
  watched runs.
- **No major capability take-backs:** preserve publishing and top-tier framework support, including
  Blazor and Azure Functions, before claiming the full migration complete.

This plan delivers the initial vertical slice:

1. **`Aspire.Hosting.Dotnet` and `DotnetProjectResource : ExecutableResource`**, with
   `AddDotnetProject` / polyglot `addDotnetProject` accepting `.csproj`, single-project directories, and
   file-based `.cs` apps. This foundation, publishing, and default-template adoption are implemented.
2. **Coordinated initial builds**, independent of the watch signal, followed by launches from the
   selected build output. This is implemented, subject to the IDE capability negotiation in Session 2.
3. **`aspire run --watch`**, with Aspire-controlled C# service watching and a separately scoped C#
   AppHost-watch integration. These are not implemented. Non-C# service packages need not implement watch
   in this slice; polyglot AppHost code keeps its language-native watching.

Partial-run selection, complete persistent/container-execution support, watch-under-debugger, and an
events/callbacks redesign remain broader-roadmap work, not delivery promises of this slice. Existing
foundation support for persistent executables does not prove all Project V2/watch combinations work.

## 2. Agreed architectural direction

These decisions preserve the original direction, incorporating requester-approved corrections from the
implementation review. Package contract facts are centralized in section 3; delivered details and
remaining work are recorded once, under their coding sessions.

| ID | Decision |
|---|---|
| D1 | **The .NET language integration is `Aspire.Hosting.Dotnet`**, a peer of the other language packages, not a new special project category in core. |
| D2 | **Project V2 uses `DotnetProjectResource : ExecutableResource`.** Core retains `ProjectResource`, `AddProject<T>`, and the path-based legacy APIs. `CSharpAppResource : ProjectResource` remains; `AddCSharpApp` / `addCSharpApp` are obsolete in favor of the new package. Existing calls are not silently redirected to V2. |
| D3 | **Activation is `aspire run --watch`**, a local-run sub-mode, not a new `aspire watch` command. The target includes C# service watching and C# AppHost watching through the tool's `host` command, with the latter gated as described in Session 8. |
| D4 | **Implement only C# service watch here.** Retain a general language-package seam. For polyglot AppHosts, keep the guest runtime's native AppHost watching and separately watch its C# services; do not wrap a native AppHost server or guest program in the .NET `host` command. |
| D5 | **Core exposes run configuration as state, not watch mechanics.** Integrations consume `ExecutionContext.RunConfiguration.WatchEnabled`. The .NET package owns service-watch coordination and tool acquisition; the CLI owns command UX, the infrastructure proxy command, and invocation of the AppHost wrapper. Shared core project defaults, publishing, and executable launch infrastructure remain reusable. |
| D6 | **Acquire the pinned watcher at watch-run time, owned by `Aspire.Hosting.Dotnet`, not bundled in the CLI.** The CLI invokes the package-owned ensure operation before C# AppHost `host` launch. Session 4 defines the acquisition requirements. |
| D7 | **Retain Aspire's coordinated initial-build barrier and accept the watcher's additional build for now.** Avoiding duplicate work through upstream `--no-build` support is a future improvement, not a prerequisite or an available option. |
| D8 | **AppHost watch is a separate session.** Earlier service-watch work runs the AppHost normally. Package-based watching is for the CLI path, not IDE-owned runs; preserve IDE-native change tracking, service launch, and session-restart behavior. |
| D9 | **Non-watch F5 parity is required and implemented.** Preserve capability negotiation and older-launcher behavior rather than forcing every IDE onto the coordinated-build path. |
| D10 | **Aspire owns a hidden service proxy command in the CLI**, using private-pipe environment delivery rather than sensitive `-e` arguments. Session 6a defines its runtime and wire-contract responsibilities. |

**API maturity is not uniform.** `DotnetProjectResource`, `AddDotnetProject`, and the package's ordinary
consumer APIs no longer carry the original `ASPIREDOTNETPROJECT001` experimental diagnostic. The package
still uses `SuppressFinalPackageVersion`; integration-authoring seams such as `IDotnetProgramResource`,
`WithProjectDefaults`, and .NET publishing/build-completion APIs retain `ASPIREPROJECTS001`.
Run configuration retains `ASPIREWATCH001`; other features such as Blazor have their own maturity.
Do not impose a blanket "all new APIs experimental" requirement or hand-edit generated release
`api/*.cs`, ATS, or capability baselines. Update codegen test snapshots when behavior requires it.

## 3. Verified published watcher contract

### 3.1 Version, provenance, runtime, and acquisition constraints

As of **2026-10-27**, the latest listed stable
**`Microsoft.DotNet.HotReload.Watch.Aspire` is `10.0.403`**, published
**2026-10-25T17:09:38.56Z**.
[NuGet metadata][watch-registration] and the [actual package][watch-nupkg] identify
`dotnet/dotnet` revision **`5a2162bea6845eabc1fc721624a43568c65db3b1`**; packaged PDB SourceLink agrees.
The source links in this section are pinned to that revision, not development `main` or the old POC.

The approved **`dotnet-public`** feed in repository `NuGet.config` already lists `10.0.403`, and its
package-content endpoint was reachable. Mirroring this version is therefore not a pending prerequisite.
Future pins still need an approved-source check; do not add nuget.org as a repository package source.

The package is a **`DotnetTool`** with a complete payload under `tools/net10.0/any/`, including
`Microsoft.DotNet.HotReload.Watch.Aspire.dll`, runtimeconfig/deps files, supporting assemblies, and
hot-reload agents. It has no ordinary library assets or consumer tool-path targets. Its installed command
name is `Microsoft.DotNet.HotReload.Watch.Aspire`; direct invocation is
`dotnet exec <full-tool-dll-path> ...`, retaining the adjacent payload.

Its runtimeconfig requests **both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` `10.0.14`**,
with no explicit major-version roll-forward setting. An installed SDK or .NET 11 runtime alone is not
proof that these requirements are satisfied. Tool runtime, selected MSBuild SDK, and application target
framework are distinct compatibility checks.

An ordinary `PackageReference` plus `GeneratePathProperty` is **not** a sufficient acquisition design
for this package type ([NU1212][nu1212]). `PackageDownload` is a possible acquisition primitive, but
[does not flow transitively or become a packed dependency][package-download]. Neither mechanism alone
implements D6. Runtime acquisition must also work for package-backed polyglot hosts, which load integration
assemblies without executing a C# AppHost's MSBuild targets.

### 3.2 Commands

All commands support `--verbose` or `--quiet`, not both.

| Command | Required options | Additional surface |
|---|---|---|
| `server` | `--sdk <sdk-directory>`, `--server <resource-pipe>` | Repeatable/multi-value `--resource <path> ...`; optional `--status-pipe <name>` and `--control-pipe <name>`. Supply the intended resource roots; an empty accepted parse is not a useful supported watch graph. |
| `resource` | `--server <resource-pipe>`, `--entrypoint <path>` | `--environment` / `-e NAME=VALUE`, `--no-launch-profile`, `--launch-profile` / `-lp <name>`, positional application arguments. |
| `host` | `--sdk <sdk-directory>`, `--entrypoint <path>` | Launch-profile options and positional application arguments. |

`--sdk` is the actual SDK directory, for example `C:\Program Files\dotnet\sdk\10.0.403`, not the
installation root or a version string. The tool registers MSBuild there and derives its `dotnet`
muxer from that layout. Resolve the appropriate SDK in the relevant project/build context; do not assume
the repository-pinned SDK, the AppHost's SDK, and every service's SDK are interchangeable.

The stable definitions expose **no `--no-build`, `--no-restore`, build-configuration, target-framework,
RID, or per-resource MSBuild-property options**. See the [command definitions][watch-commands].
For example, the server shape is:

```powershell
dotnet exec $tool server --sdk $sdk --server $resourcePipe `
    --resource $projectA --resource $projectB
```

The planned Aspire CLI proxy connects to that resource pipe instead of constructing a sensitive
`resource ... -e ...` invocation. `host --sdk $sdk --entrypoint $appHostProject` remains the C# AppHost
target, subject to Session 8. Do not promise arbitrary argument forwarding just because the outer
parser accepts `--`: the tool's downstream `dotnet run` construction does not insert a new application
argument separator.

### 3.3 Builds, pipe roles, and lifecycle

**`server` and `host` perform initial restore/build and graph/workspace initialization**, then
incremental watch work. Multiple physical roots can be built through the tool's generated `.slnx`;
file apps are built separately. `resource` is a launch/output/lifetime proxy, not the initial builder.
See [watch initialization/build code][watch-build].

| Pipe | Listener/server owner | Other endpoint and format |
|---|---|---|
| `--server` resource pipe | Watcher `server` | One proxy connection per service instance; binary framing, not JSON lines. |
| `--status-pipe` | Aspire caller/AppHost | Watcher connects as an output client and writes one JSON event per line. |
| `--control-pipe` | Aspire caller/AppHost | Watcher connects as an input client and reads one JSON command per line. |

The [resource protocol][watch-resource] sends version byte `1`, then a 7-bit-length-prefixed UTF-8 JSON
launch request. Its fields are `EntryPoint`, `ApplicationArguments`, `EnvironmentVariables`, and
`LaunchProfileName`. An ACK byte precedes output records, each with type byte `1` (stdout) or `2` (stderr)
and a length-prefixed string. This is an implementation-defined, pinned wire contract, not a public .NET API
dependency.

Important semantics for the integration:

- Resource roots are supplied when starting the server; a proxy request does not dynamically add a new
  root. The requested entry point must be present in the loaded graph.
- The resource listener is created after initial build/workspace initialization. A DCP server process
  reaching `Running` is not proof that the listener or build output is ready.
- The resource ACK precedes actual application launch. It is not proof of process start, successful
  hot-reload-agent initialization, or application health.
- The package proxy's default pipe-connection timeout is 30 seconds; its timeout/cancellation path can
  return zero. Proxy startup or exit code must not become a false application-success signal.
- The server owns the actual service process. Disconnecting its proxy terminates the associated
  process, but application completion/exit code is not faithfully represented by proxy completion.

The [status schema and control implementation][watch-status] use project paths, not resource names,
instance IDs, or PIDs. Status types include `building`, `build_complete`, `hot_reload_applied`,
`restarting`, `process_started`, and `process_exited`; there is no general `ready` event, and
`process_exited` is not a complete feed of all exits. The control request
`{"type":"rebuild","projects":[...]}` **restarts currently running matching projects; it does not
perform a build**. Do not map the dashboard's Rebuild command to it as if those operations were equivalent.

### 3.4 Environment and AppHost behavior

The package's `resource` command forwards only explicit `-e` values, not the proxy's inherited
environment. The application is started by the server, which has its own inherited environment.
The pipe request and hot-reload agent can deliver service values without putting them into downstream
build/run command lines, but **`-e` still exposes those values on the outer proxy command line**.
D10 addresses that remaining exposure; merely putting DCP environment on the stock proxy does not.

The `host` command can automatically start the watcher's **Aspire execution service** when the
evaluated AppHost has the `Aspire` capability. It injects `DEBUG_SESSION_PORT`,
`DEBUG_SESSION_TOKEN`, and `DEBUG_SESSION_SERVER_CERTIFICATE`. This also applies to modern
`Microsoft.NET.Sdk` + `Aspire.Hosting.AppHost` projects and happens without a real IDE.
There is no exposed AppHost-only switch that selectively disables this service.
See [factory selection][watch-host-factory] and [the execution-service factory][watch-host-service].
This is the concrete composition issue gated in Session 8, not a requirement to use the package in IDE runs.

Supported edits can hot-reload; rude edits can trigger noninteractive restarts. A hot-reloaded AppHost
method body does not by itself rerun model-construction code or update topology. Build failures can leave
the watcher waiting for edits rather than exiting like a one-shot build. Do not infer topology refresh,
startup failure, or health semantics from the wrapper process alone.

## 4. Work breakdown - coding sessions

Session numbers are retained for continuity. Completed work is not waiting on earlier-numbered pending
sessions; dependencies are listed after the session descriptions.

### Session 1 - .NET package and executable-based project resource

**Implemented:** [#18442](https://github.com/microsoft/aspire/pull/18442), with API and polyglot follow-ups
including [#18981](https://github.com/microsoft/aspire/pull/18981) and
[#19026](https://github.com/microsoft/aspire/pull/19026).

`DotnetProjectResource` implements `IResourceWithServiceDiscovery`,
`IContainerFilesDestinationResource`, and `IDotnetProgramResource`, and carries
`ProjectLaunchDefaultsAnnotation`. `AddDotnetProject` attaches deferred path metadata, applies shared
project defaults and SDK publishing, and validates the input on the resource-start path. It accepts a
project file, a directory containing one `.csproj`, or a `.cs` file; file apps require .NET 10+.
Metadata is stable after defaults are applied.

C# overloads use `Action<ProjectResourceOptions>`. The internal polyglot dispatcher exports
`addDotnetProject` with a plain-data `DotnetProjectOptions` DTO: `launchProfileName`,
`excludeLaunchProfile`, and `excludeKestrelEndpoints`. Excluding profiles wins over a supplied name.
This intentionally does not change the legacy `ProjectResourceOptions` handle contract.

The package consumes public core seams and source-shared implementation helpers, not production
`InternalsVisibleTo`. Shared defaults supply launch-profile/Kestrel endpoints, telemetry, environment,
certificate handling, project launch metadata, and the Rebuild infrastructure.
The default tool invocation uses **`WithLaunchToolArgs`**, separate from program arguments; the current
normal launch path is described in Session 5, not a blanket `dotnet run` recipe.

**Blazor consumer:** both gateway resource shapes exist, sharing client/state/publish helpers.
`AddBlazorGateway` retains its `ProjectResource` return type; `AddDotnetProjectBlazorGateway` returns V2.
The V2 attachment capability is `withDotnetProjectBlazorClientApp`, generated as `withBlazorClientApp`.
Both gateway variants now intentionally override development launch to the official
`Microsoft.AspNetCore.Components.Gateway.Cli` tool and force process execution; their metadata-backed
file app is used for publishing. Thus a V2 resource type alone does not mean its current custom launch
can be replaced with project watch. Scripts are packed for both C# `buildTransitive` and direct
package-backed polyglot loading.

**Evidence:** [package implementation][dotnet-source], `DotnetProjectOptionsTests`,
`AddDotnetProjectBlazorGatewayTests`, four Blazor polyglot fixtures (TS/Go/Java/Python), and
`TypeScriptBlazorGatewayTests.DotnetProjectBlazorGatewayRunsFromTypeScriptAppHost`.

### Session 1b - Persistent playground/dogfood harness

**Implemented:** [#18654](https://github.com/microsoft/aspire/pull/18654).

[`playground/DotnetProject`][playground] has C# and TypeScript AppHosts, two project services
(`DotnetProject.ApiService` and `DotnetProject.WorkerService`) referencing
`DotnetProject.SharedLibrary`, and `worker/worker.cs`. Both AppHosts add the services through V2 and
wire worker service-discovery references to the API. They do **not** currently add `WaitFor(apiservice)`.
The C# AppHost has no service project references; the service projects retain their ordinary shared-library
references. The TypeScript AppHost declares the integration in `aspire.config.json`.

Keep this as the shared-library and mixed project/file-app target for later watch sessions, including
package-backed polyglot runs.

### Session 2 - Non-watch debugging and executable launch parity

**Implemented:** [#18729](https://github.com/microsoft/aspire/pull/18729), followed by shared launch
planning and coordinated/external-build support in [#19552](https://github.com/microsoft/aspire/pull/19552)
and [#19678](https://github.com/microsoft/aspire/pull/19678).

The [executable launch recipes][launch-recipes] and DCP launch policy produce an immutable launch plan
shared by legacy projects and executable resources. Launch-tool, launch-profile, and application
arguments have distinct roles; replacing a tool prefix must not discard program arguments. Failed
required IDE launch configuration is reported, not silently retried as a DCP process.

Coordinated V2 builds negotiate **`project-with-external-build.v1`**
(`KnownLaunchConfigurationTypes.ProjectWithExternalBuild`). In an external debug session, the coordinator
runs only when that capability is advertised. Older/capability-less launchers
retain the legacy project-build path; unsupported project launch or persistent lifetime can retain normal
process execution. Build configuration, build-only environment, build working directory, and build
suppression flow through project launch metadata.

The VS Code debugger understands externally built projects and file apps, resolves SDK-selected
commands/arguments, and preserves selected/disabled launch-profile behavior and required host variables.
It must not silently rebuild suppressed output or launch a different configuration. This preserves
non-watch Debug/NoDebug behavior; it does not implement this plan's watcher under an IDE.

**Evidence:** `ExecutableLaunchPlanTests`, DCP/package tests, `extension/src/test/dotnetDebugger.test.ts`,
and `extension/src/test-e2e/coordinatedBuild.e2e.test.ts`.

### Session 3 - Core run configuration state

**Implemented:** [#18863](https://github.com/microsoft/aspire/pull/18863).

`RunConfiguration` is an immutable-init `[AspireDto]` with `bool WatchEnabled`.
`DistributedApplicationExecutionContextOptions.RunConfiguration` initializes the never-null, read-only
execution-context property. `DistributedApplicationBuilder` reads **`AppHost:Run:WatchEnabled`** on
both run-mode construction paths. Non-boolean inputs yield false; publish contexts report defaults even
if caller-supplied run options request watching.

No watch-server/acquisition/pipe behavior is implemented by this state. Merely setting the key currently
does not make .NET services watch.

**Evidence:** `RunConfiguration.cs`, execution-context/options and builder source,
`OperationModesTests`, and the polyglot codegen snapshots.

### Session 4 - Runtime-only watcher acquisition and resolution

**Not implemented.** There is no watcher pin/reference, acquisition implementation, tool-path target,
or runtime resolver in `Aspire.Hosting.Dotnet`.

Implement D6 using the verified package contract in section 3:

- Pin the verified `10.0.403` baseline and acquire its complete payload from approved/configured sources only when
  an effective CLI watch run needs it. Reuse a compatible cache entry; handle concurrent ensure requests,
  interrupted acquisition, version mismatch, and missing runtimes with explicit diagnostics.
- Keep acquisition logic owned by `Aspire.Hosting.Dotnet`, usable during AppHost startup before the
  service-watch server starts. Do not add a build/restore/publish or ordinary-run download prerequisite.
- Expose a package-owned **launch-time ensure/resolution entry point** that the CLI can invoke from the
  already-restored C# AppHost project **before** launching the AppHost through `host`. Otherwise there is
  a circular dependency: AppHost startup cannot acquire the tool needed to start that AppHost.
- Establish path/configuration precedence and pass the same resolved installation to both launch paths.
  C# MSBuild metadata may expose information where useful, but cannot be the sole runtime contract for
  direct-loaded polyglot packages. The exact ensure entry point, cache layout, and tool-compatible
  acquisition primitive remain implementation work, not an assumed ordinary `PackageReference`.

**Acceptance:** watch-only acquisition and cache reuse work for C# and package-backed TypeScript
AppHosts; non-watch/build/restore/publish do not acquire the tool; the resolved installation executes
`--help` and meets runtime requirements. Cover failures, cancellation, concurrent callers, and approved
feed selection without modifying repository `NuGet.config`.

### Session 5 - Coordinated initial builds and output-bound launches

**Implemented:** [#19678](https://github.com/microsoft/aspire/pull/19678), with unified restore in
[#20294](https://github.com/microsoft/aspire/pull/20294).

`DotnetProjectBuildCoordinator` prepares supported Run-mode resources, then materializes the active
model's build plan in the **`BeforeStart` pipeline's final action**. Removed/replaced resources are not
built. Existing projects are grouped by physical nearest `global.json` identity and build configuration;
explicit `WithBuildEnvironment` callbacks select direct builds. Runtime `WithEnvironment`,
service-discovery references, launch-profile environment, and runtime working-directory overrides do not
split build groups.

Traversal projects are content-addressed, leased artifacts under **`Aspire:Store:Path/.aspire/build`**
(the resolved AppHost intermediate-output store). Each compatible group performs aggregate NuGet graph
restore and parallel MSBuild child builds, de-duplicating shared references within that build context
without introducing solution-specific properties. Groups run serially; direct builds and `.cs` apps also
run serially so shared outputs, including `#:project` references, do not race.

Aggregate restore does not run each root's `BeforeTargets="Restore"` / `AfterTargets="Restore"` hooks.
**`Aspire:Dotnet:RestoreProjectsIndividually=true`** opts into serial per-root restore while retaining
parallel builds. Unavailable NuGet restore targets or top-level `RestoreUseStaticGraphEvaluation` also
select the logged individual-restore path; a restore failure is not retried with another strategy.

Services wait for the full successful build chain, including forced starts. Failure handling waits for
a settled exit code rather than treating an early `Exited`/unknown code as success. Missing paths retain
resource-local failure behavior rather than preventing valid siblings from building. No coordinator is
created in publish mode or for an IDE that does not advertise external-build support.

After coordinated builds, `.csproj` resources query `ComputeRunArguments` for **`RunCommand`,
`RunArguments`, and `RunWorkingDirectory`** in the same build context and launch that output directly.
File apps use `dotnet run --file <path> --no-build`; uncoordinated file-app launches use `--no-cache`.
Start/Restart reuse built output; Rebuild builds and restarts through the separate project rebuilder.

**Additional delivered build API:** `WithBuildEnvironment` supplies build-only variables/global
properties for `.csproj` resources, not `.cs` apps. One evaluated snapshot per build attempt feeds the
coordinated build, run-property query, rebuilder, and IDE metadata; publishing also receives the
customization. Protected response files preserve MSBuild global-property precedence without command-line
values. This is **not a secret transport**. Callbacks must be configured before plan materialization;
incompatible duplicate registrations of one project with custom build contexts are rejected.

**Evidence:** [coordinator implementation][build-coordinator], `DotnetProjectBuildCoordinatorTests`,
`DotnetProjectBuildEnvironmentTests`,
`DotnetProjectRunPropertiesResolverTests`, `DotnetProjectBuildArtifactManagerTests`, and the
[integration README][dotnet-readme]. These cover real shared-graph builds as well as model/argument,
failure, retry/cancellation, filesystem-identity, build-output-readiness, and artifact-lifetime behavior.

### Session 5b - .NET program publishing and consumer parity

**Implemented, with documented limitations:** [#19997](https://github.com/microsoft/aspire/pull/19997).
This is no longer just planned scaffolding.

Core distinguishes **`IDotnetProgramResource` identity** from the annotation-backed
**`WithDotnetProgramPublishing` capability**. Legacy projects and `AddDotnetProject` opt into the shared
SDK image build/push pipeline; implementing the interface or directly constructing a V2 resource is not
enough. V2 emits project manifests, supports SDK container publishing and container-file layering, and
is recognized by Docker Compose, Kubernetes, Azure Container Apps/App Service/Sandboxes, Radius,
Foundry hosted-agent, and Blazor integration paths. The shared pipeline and general compute integrations
use identity/capability checks rather than requiring the V2 concrete type; Blazor deliberately references
the package for its concrete gateway variant.

Delivered details include SDK archive output, daemon-free archive builds when no container-file
layering is needed, temporary image identities/cleanup for layering, prebuilt-image validation, and
build-environment propagation with rejection of artifact identity/platform redirection.
File-app publishing preserves SDK defaults, including Native AOT; cross-OS AOT failures report
actionable guidance such as `#:property PublishAot=false` for framework-dependent output.
See the README for archive-format/runtime constraints rather than duplicating them here.

EF Core APIs accept the shared .NET-program identity and wait for build output using
`DotnetProgramBuildCompletionAnnotation`, without waiting for application readiness. **EF does not
receive Aspire-specific build values as MSBuild global properties.** It warns once per requested
operation and continues; output can be suitable, different/stale, or missing. Run-mode EF commands
normally retain `--no-build`; publish-time script/bundle generation allows EF to build. This limitation
must remain explicit, not be described as complete build-customization parity.

**Evidence:** [shared publishing implementation][publishing-source], core `DotnetProgramPublishingTests`
and archive/image-manager tests; V2 manifest/publish tests; EF readiness/customization tests;
compute-integration differential/snapshot coverage; and
deployment scenarios in `Aspire.Cli.EndToEnd.Tests` and `Aspire.Deployment.EndToEnd.Tests`, including
ACA/AKS, App Service, Sandboxes, Radius, and Foundry.

### Session 5c - Default adoption and API graduation

**Implemented; previously missing from the plan.**

- [#20490](https://github.com/microsoft/aspire/pull/20490) removed the V2 consumer experimental
  attributes; [#20582](https://github.com/microsoft/aspire/pull/20582) removed the obsolete diagnostic
  guidance. [#20825](https://github.com/microsoft/aspire/pull/20825) made `AddCSharpApp` obsolete.
- [#20867](https://github.com/microsoft/aspire/pull/20867) deprecated **`Aspire.AppHost.Sdk`**
  (`ASPIRE012`) and migrated C# AppHost templates to **`Microsoft.NET.Sdk`**, explicit
  `Aspire.Hosting.AppHost` / `Aspire.Hosting.Dotnet` package references, and path-based service additions
  instead of AppHost service `ProjectReference` items. File-based AppHost templates include both packages.
- The AppHost package supplies the plain-SDK defaults/capability and CLI-bundle run plumbing. Legacy
  SDK projects and explicitly marked legacy project references remain supported for staged migration;
  `AddProject` has not been silently changed to return V2.

This is the **default new C# project-inclusion path**, not proof that all existing applications or
every polyglot template has been migrated. The current TypeScript starter models Node/Vite services;
polyglot AppHosts add the .NET integration explicitly when they need C# services.

**Evidence:** `Aspire.AppHost.Sdk/SDK/Sdk.in.targets`, `Aspire.Hosting.AppHost/build`,
`Aspire.ProjectTemplates/templates`, CLI empty/starter templates, and `AppHostSdkTargetsTests`.

### Session 6 - Aspire-controlled C# service watch

**Not implemented.** No `DotnetWatchServerResource` or V2 watch launch rewrite exists.

When a supported CLI watch run is requested, ensure the tool, collect eligible project roots, add
**one hidden system watch server per AppHost run**, and launch one **Aspire-owned proxy per resource
instance**. Keep infrastructure out of manifests and inappropriate user lifecycle commands; define
intentional startup and shutdown ordering, rather than adding an explicit-start server with no owner to start it.

Retain the coordinated initial-build contract, then accommodate the watcher's additional build and
initialization before services use it. Use the existing launch-tool/program-argument seam rather than
discarding all `WithArgs` or silently overwriting a custom tool launch such as the Blazor gateway.
The AppHost still runs normally during this session.

Implement the applicable compatibility/lifecycle gates in section 5, including meaningful failure and
status reporting. A bare `WaitForStart(server)` is not the complete readiness design, and the status
monitor is not optional where correctness depends on it. Do not assume the stock proxy's zero exit
code or `rebuild` control request supplies Aspire's lifecycle semantics.

**Acceptance:** from the TypeScript playground first, then C#, a service edit updates the intended
service and a shared-library edit updates both consumers. Verify server failure, initial-build failure,
restart/rebuild behavior, cleanup, and unsupported-context diagnostics, not only successful edits.

### Session 6a - Hidden Aspire CLI service proxy

**Not implemented; requester-approved addition to the work breakdown.**

Implement a hidden, infrastructure-only CLI command for D10. DCP launches it per instance with the
intended service environment delivered through process environment/private local transport; it sends
that environment in the watcher resource-pipe request. Do not indiscriminately forward every ambient
CLI variable or put values into `-e` arguments, logs, dashboard command lines, or persisted payloads.

Pass the **exact invoking CLI launch specification** to the integration, including any managed/dogfood
host prefix; do not resolve an arbitrary `aspire` from `PATH`. Build-time `AspireCliPath` metadata alone
is not this runtime contract. The infrastructure command must avoid banners, prompts, first-run/update
UX, and payload dumps.

Own the pinned binary framing, serialization/profile states, output forwarding, disconnect cleanup,
cancellation, bounds checking, and failure handling. Version-byte acceptance alone does not prove full
semantic compatibility. Do not translate transport completion into a fabricated application exit code.
A future supported secure input channel in the upstream proxy may let Aspire replace this implementation,
but that is not available in the verified contract.

**Acceptance:** contract tests against the actual pinned watcher prove environment values reach the
service without command-line disclosure, stdout/stderr stay correctly separated, one proxy connection
owns one launched instance, and failures/cancellation never look like successful application completion.

### Session 7 - `aspire run --watch` CLI wiring

**Not implemented.** `RunCommand` currently has no `--watch` option and does not project the V2 signal.
The existing `defaultWatchEnabled` feature still selects whole-AppHost `dotnet watch` for project-based
C# AppHosts and the guest runtime's watch path for polyglot hosts. It is not the new service-watch mode.
Current `--no-build` validation already accounts for legacy watch and extension-terminal launches.

Add the option, with explicit `--watch` enabling watch and `DefaultWatchEnabled` supplying the default.
Project `AppHost:Run:WatchEnabled` into the process that actually hosts `Aspire.Hosting`.
For polyglot runs that means the **AppHost server's environment**, not only the guest application's
environment. Enable runtime acquisition and provide the exact proxy CLI invocation for that run.
Initially keep normal C# AppHost launch; service watch must work before Session 8 replaces its wrapper.

Preserve IDE ownership: VS Code/Visual Studio launches use their existing watch/debug/session-restart
protocol, not this package-backed CLI watcher. Reconcile flags, diagnostics, help/localization,
backchannel reconnect/cancellation, and existing `--no-build` behavior rather than leaving two unrelated
watch implementations active under the same effective setting.

**Acceptance:** CLI tests plus terminal E2E prove explicit/default watch selection, absence of acquisition
in non-watch runs, correct C#/guest signal delivery, existing option validation, and service hot reload.

### Session 8 - C# AppHost watch through `host`

**Not implemented; composition gate remains open.** Use the package-owned prelaunch ensure operation
from Session 4, then launch the C# AppHost with the resolved tool's `host` command.
Keep language-native AppHost watching for polyglot runs, including native-server configurations.

The requester chose **Aspire-controlled service launch**, not adoption of the package's built-in
execution service as a replacement backend. Establish a supported composition that prevents the
automatically injected external-launch settings from bypassing the proxy path or disabling coordinated
build ownership. CLI-only invocation is a boundary requirement, but does not suppress the package's
automatic service. An Aspire-side integration or upstream enhancement must be demonstrated; do not
silently assume an AppHost-only switch, strip protocol settings as an unexplained workaround, or mark
this gate resolved.

Also account for the AppHost package's `dotnet run` -> `aspire run` hook: watcher launches must not
recursively re-enter the CLI. Preserve the AppHost configuration/profile, forwarded arguments,
CLI backchannel, startup-failure behavior, and shutdown ownership.

**Acceptance:** supported AppHost edits update running code; topology changes actually reconstruct the
model through a verified restart/update path. Verify backchannel reconnection, old server/proxy/child
cleanup, changed root graphs, and continued Aspire ownership of service launch/builds. Do not equate
method-body hot reload with topology reevaluation.

### Session 9 - Watch end-to-end coverage, playground extension, and documentation

**Not implemented for the new watcher.** Existing package/build/publish/debug and legacy-watch coverage
is not end-to-end coverage of Sessions 6-8.

Extend the Session 1b playground and add hosting/proxy contract tests, CLI unit tests, and CLI terminal
E2E using the repository's `cli-e2e-testing` guidance. Verify **TypeScript first, C# second**, and cover
Windows, Linux, and macOS: TypeScript-first testing alone says nothing about cross-platform pipes.
Combine the acceptance checks of Sessions 4, 6, 6a, 7, and 8 with the compatibility gates in section 5;
also exercise rude-edit restart, invalid source edits, and recovery after correcting them.

Update package/CLI documentation for actual availability, supported contexts, runtime prerequisites,
acquisition/cache behavior, and limitations. Preserve the EF customization warning and other existing
publishing/debug limitations; do not label watch-debug or partial-run combinations as supported without
their own work.

Use the smallest relevant builds/tests, repository-native MTP filters, and focused E2E scenarios.
Do not require a full-repository build for every session or regenerate frozen release API baselines
as a routine coding-session completion step.

### Session dependencies

| Session | Prerequisites |
|---|---|
| 1b, 2, 4, 5 | 1 |
| 3 | Independent |
| 5b | 1, 5 |
| 5c | Delivered package/default-adoption work; not blocked on watcher sessions |
| 6a | 3, 4; agree the launch/configuration contract with 6/7 |
| 6 | 1, 3, 4, 5, 6a |
| 7 | 3, 4, 6, 6a |
| 8 | 4, 7, and supported composition of `host` with Aspire-owned service launch |
| 9 | Extend coverage alongside 6/6a/7; complete the full slice after 8 |

Session 1b remains the reusable dogfood target. Sessions 4 and 6a can advance independently of the
remaining AppHost composition investigation; service-watch delivery must not imply that Session 8 passed.

## 5. Remaining compatibility gates and roadmap boundaries

Start with **verified compatible watch contexts and explicit, actionable rejections** for unsupported
ones. Preserve broad support where semantics are established; do not impose an arbitrary blanket
single-instance-only restriction or silently fall back to non-watch behavior.

| Gate | Required investigation/verification | Owner |
|---|---|---|
| Build/launch fidelity | The watcher takes one SDK per server and has no per-resource build-property/configuration/RID fields or precomputed run-command input. Establish which existing V2 build contexts round-trip correctly, including custom inputs, working directories, and solution properties introduced by its `.slnx` build. Multiple servers alone do not solve shared-output races or property precedence. Reject unsupported cases rather than launch a different output. | 4, 6 |
| Exact entry point, arguments, profiles | Server graph lookup uses the requested path, but the standalone resource-launch construction does not explicitly pass `--project` or `--file`. Verify `.cs` apps and directories with multiple projects instead of inferring support from successful builds. Verify application-option forwarding and the three-state profile field across the actual wire serialization; parser-level tests alone are insufficient. | 6, 6a |
| Per-instance control and completion | One proxy maps to one service instance, but status/control events identify project paths only. Verify replicas and duplicate paths without losing attribution or independently requested lifecycle actions. Reject known ambiguous independently controlled registrations and completion-sensitive cases when the contract cannot represent their outcome; do not claim proxy completion is `WaitForCompletion` parity. | 6, 6a |
| Custom launches and framework resources | Resource type/metadata alone does not authorize replacing an integration's launch tool. Cover or explicitly reject custom invocation, persistent lifetime, and other unsupported combinations without taking back their normal non-watch behavior. | 6, 7 |
| Pipe security, environment, startup, cleanup | Verify current-user-only pipe behavior on each OS, intended environment versus server inheritance, startup-only environment semantics, connection/readiness timeouts, concurrent instances, disconnects, and server/AppHost restarts. A successful connection/ACK is not application health. | 6, 6a, 9 |
| C# AppHost composition/topology | Resolve the automatic execution-service overlap and run-hook recursion described in Session 8. Rebuild/start/reconnect ownership must remain explicit when an AppHost restarts or the model changes. | 8 |
| Future tool upgrades | Reverify version, approved-feed availability, runtime, protocol, and build/launch semantics before changing the pin, including any new prebuilt-output contract for D7. | 4, 6, 8 |

**Azure Functions remains deferred in this slice**, not removed from the overall no-take-back goal.
`AzureFunctionsProjectResource : ProjectResource` and its `azure-functions` / `func host start` path
remain in use. A V2-backed Functions resource needs Functions-host/isolated-worker watch validation and
an explicit package-dependency/launch design; ordinary `.NET resource` watching does not establish that.

**Annotation-based resource flavoring** ([#8984][resource-flavoring]) remains compatible directionally:
shared annotations/capabilities and run state avoid unnecessarily binding every behavior to a concrete
resource type. That proposal is not implemented by this plan, nor a prerequisite for its vertical slice.

**Events/callback redesign remains deferred.** The delivered build materialization and per-attempt
build-environment behavior are concrete current contracts to preserve. Watch must document any new
callback ordering/repetition and invalidation on restart; it must not claim to solve partial-run closure
or the broader event-cardinality questions merely by subscribing to `BeforeResourceStartedEvent`.

## References

| Subject | Reference |
|---|---|
| Overall vision, motivations, and orthogonal capabilities | [Project Resource V2 goals][vision] |
| Watch tracking item | [#15710](https://github.com/microsoft/aspire/issues/15710) |
| Original prototype, historical context only | [karolz-ms/aspire-watch](https://github.com/karolz-ms/aspire-watch) |
| Stable package and publication evidence | [NuGet version index][watch-versions], [10.0.403 registration][watch-registration], [published nupkg][watch-nupkg] |
| Published command/launch/pipe implementation | [Pinned command definitions][watch-commands], [resource/server protocol][watch-resource], [status/control][watch-status] |
| Existing user-facing watch documentation; update when new behavior lands | [Hot reload and watch](https://aspire.dev/app-host/hot-reload-and-watch/) |
| Resource flavoring proposal | [#8984][resource-flavoring] |
| Current package behavior and limitations | [Aspire.Hosting.Dotnet README][dotnet-readme] |

[vision]: https://github.com/microsoft/aspire/issues/16386#issuecomment-4300443345
[resource-flavoring]: https://github.com/microsoft/aspire/issues/8984
[watch-versions]: https://api.nuget.org/v3-flatcontainer/microsoft.dotnet.hotreload.watch.aspire/index.json
[watch-registration]: https://api.nuget.org/v3/registration5-gz-semver2/microsoft.dotnet.hotreload.watch.aspire/10.0.403.json
[watch-nupkg]: https://api.nuget.org/v3-flatcontainer/microsoft.dotnet.hotreload.watch.aspire/10.0.403/microsoft.dotnet.hotreload.watch.aspire.10.0.403.nupkg
[watch-commands]: https://github.com/dotnet/dotnet/tree/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch.Aspire/Commands
[watch-build]: https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch/HotReload/HotReloadDotNetWatcher.cs#L101-L185
[watch-resource]: https://github.com/dotnet/dotnet/tree/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch.Aspire/Resource
[watch-status]: https://github.com/dotnet/dotnet/tree/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch.Aspire/Server
[watch-host-factory]: https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch/HotReload/HotReloadDotNetWatcher.cs#L150-L181
[watch-host-service]: https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch/Aspire/AspireServiceFactory.cs
[nu1212]: https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1212
[package-download]: https://learn.microsoft.com/en-us/nuget/consume-packages/packagedownload-functionality
[dotnet-source]: ../../src/Aspire.Hosting.Dotnet
[dotnet-readme]: ../../src/Aspire.Hosting.Dotnet/README.md
[launch-recipes]: ../../src/Aspire.Hosting/ApplicationModel/ExecutableLaunchRecipe.cs
[build-coordinator]: ../../src/Aspire.Hosting.Dotnet/DotnetProjectBuildCoordinator.cs
[publishing-source]: ../../src/Aspire.Hosting/ApplicationModel/DotnetProgramPublishing.cs
[playground]: ../../playground/DotnetProject
