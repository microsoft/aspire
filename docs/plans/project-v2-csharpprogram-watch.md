# Project V2 stage 1 - .NET language integration and `aspire run --watch`

This document records the .NET language integration and selective watch contracts.
The package-owned service watcher, combined CLI activation, and C# AppHost generations are
implemented in source. Historical session evidence below is not a fresh execution of every
manual check or deployment test. Current user-facing behavior is documented in the
[integration README][dotnet-readme].

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
3. **`aspire run --watch`**, with Aspire-controlled C# service watching and CLI-owned C#
   AppHost generations. Non-C# service packages need not implement watch
   in this slice; managed-server polyglot AppHost code keeps its language-native watching.
   Native protocol-v2 servers run normally and warn that AppHost changes require stopping and rerunning Aspire.

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
| D4 | **Implement watch for compatible C# AppHosts and C# services here.** Retain a general language-package seam. For managed-server polyglot AppHosts, keep the guest runtime's language-native AppHost watching and separately watch its C# services. Native protocol-v2 servers retain their backend and run without watching, with an actionable warning when watching is requested; do not wrap a native AppHost server or guest program in the .NET `host` command. |
| D5 | **Core exposes run configuration and generic experimental hooks, not watch mechanics.** Integrations consume `ExecutionContext.RunConfiguration.WatchEnabled` and opt into lifecycle-command replacement and pure snapshot projection. The .NET package owns service preflight, bundled payloads, and WatchHost; the CLI owns effective selection and complete C# AppHost generations. |
| D6 | **Ship the pinned watcher inside `Aspire.Hosting.Dotnet` (like DCP), never download it at run time.** Payload `10.0.403` requires a compatible .NET 10 SDK and the declared runtimes. Metadata or the restored package identifies it. Diagnosed ordinary fallback is permitted only during preflight; explicit required watch cannot silently fall back. |
| D7 | **The watcher performs the sole initial build of effectively watched services.** Their output-readiness barrier completes on successful watcher build. Ordinary resources retain coordinated builds; no late ordinary downgrade is permitted after handoff. Normal CLI AppHost preparation and the upstream `host` build are separate from this service-build invariant. |
| D8 | **CLI and IDE ownership are explicit.** Compatible C# AppHosts use `host` and complete model replacement; real IDE-owned runs retain IDE-native build/debug/watch and session restart. Upstream `host`-injected debug settings do not transfer CLI-owned service launch to its execution service. |
| D9 | **Non-watch F5 parity is required and implemented.** Preserve capability negotiation and older-launcher behavior rather than forcing every IDE onto the coordinated-build path. |
| D10 | **Aspire owns the service proxy in a small watch host exe shipped by Aspire.Hosting.Dotnet** (`tools/dotnet-watch-host/`, run with `dotnet exec`), using private-pipe environment delivery rather than sensitive `-e` arguments. The CLI doesn't know about it, so `aspire run --watch`, `dotnet run` of the AppHost and polyglot AppHost servers behave the same. Session 6a defines its runtime and wire-contract responsibilities. |

**API maturity is not uniform.** `DotnetProjectResource`, `AddDotnetProject`, and the package's ordinary
consumer APIs no longer carry the original `ASPIREDOTNETPROJECT001` experimental diagnostic. Starting
with Aspire 17.0, the package follows normal repository versioning, including stable versions for
stabilized builds. Package stabilization does not graduate integration-authoring seams such as
`IDotnetProgramResource`, `WithProjectDefaults`, and .NET publishing/build-completion APIs, which retain
`ASPIREPROJECTS001`. Package validation remains enabled; baseline comparisons skip releases before
17.0.0 because the package had no stable version in those releases.
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
for this package type ([NU1212][nu1212]). `PackageDownload` [does not flow transitively or become a packed
dependency][package-download], so (like `eng/dcppack`) `Aspire.Hosting.Dotnet` `PackageDownload`s the payload and
repacks it under `tools/dotnet-watch/`, with a `buildTransitive` targets file that stamps the path on the AppHost.
Package-backed polyglot hosts load integration assemblies without executing a C# AppHost's MSBuild targets, so they
don't get that metadata (open gap; see Session 4).

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

The watch host proxy (D10, `Aspire.Hosting.Dotnet.WatchHost`) connects to that resource pipe instead of constructing a sensitive
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
(`KnownLaunchConfigurationTypes.ProjectWithExternalBuild`). In an external debug session:

- The coordinator runs when that capability is advertised.
- The coordinator also runs when the launcher advertises an explicit capability list without `project`;
  the IDE cannot launch those resources, so they run as DCP processes and use the coordinated build as
  they do outside an IDE.
- Older/capability-less launchers, and launchers that advertise `project` without the external-build
  capability, retain the legacy project-build path.
- Unsupported project launch or persistent lifetime can retain normal process execution.

Build configuration, build-only environment, build working directory, and build suppression flow through
project launch metadata.

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

Core run configuration is state only. The .NET integration consumes it to select service watching;
the CLI separately owns C# AppHost wrapping and compatible language-native guest activation.

**Evidence:** `RunConfiguration.cs`, execution-context/options and builder source,
`OperationModesTests`, and the polyglot codegen snapshots.

### Session 4 - Packaged watcher acquisition and resolution

**Implemented (packaging, no runtime download).**

- `eng/Versions.props` pins the payload: `MicrosoftDotNetHotReloadWatchAspireVersion` (10.0.403). The watcher loads
  MSBuild from the selected SDK, so it only works with .NET 10 SDKs, and watch mode supports .NET 10 SDKs only.
  `Aspire.Hosting.Dotnet.csproj` `PackageDownload`s it (a `PackageReference` fails with NU1212), checks the payload is
  complete, lays it out under `artifacts/obj/Aspire.Hosting.Dotnet/dotnet-watch/` and packs it as `tools/dotnet-watch/`.
- `buildTransitive/Aspire.Hosting.Dotnet.targets` adds `AssemblyMetadata("aspiredotnetwatchtoolsdir", …)` to AppHosts
  (`IsAspireHost=true`). In-repo AppHosts import it through `Aspire.RepoTesting.targets` and the repo-mode polyglot server
  project, with `AspireDotnetWatchToolsDir` set in the root `Directory.Build.props`.
- `DotnetWatchToolResolver.ResolveToolPath` picks `<toolsdir>/Microsoft.DotNet.HotReload.Watch.Aspire.dll`.
  Missing metadata, an incomplete payload, or an incompatible SDK produces an actionable preflight diagnostic.
  Ordinary fallback can occur before ownership handoff, never afterward. `ASPIRE_DOTNET_WATCH_TOOL_PATH` is authoritative;
  invalid overrides do not select a different payload.

- Package-backed polyglot AppHosts (prebuilt server) don't run the package's MSBuild targets, so there's no metadata.
  The location is chosen by how `Aspire.Hosting.Dotnet.dll` was loaded, not by probing one place after another: when it
  is loaded from its restored package (`<package>/lib/<tfm>/`, with `aspire.hosting.dotnet.nuspec` in `<package>/`),
  that package's `tools/<folder>/` is the only source; otherwise the AppHost metadata is required. Either way a missing
  payload is an error naming the path it expected.

**Open:** the prebuilt-server path is unit-tested but not yet run against a published package. Payload signing in the official build and .NET 10 SDK feature bands other than 10.0.4xx are unverified.

`ResolveAspireDotnetWatch` is an explicitly invoked prelaunch query, not a Restore/Build/Run/Publish
hook. It returns bundled tool/WatchHost paths plus the executing project's SDK and muxer.
The source and packed targets use the same operation. Shared validation checks runtimeconfig/deps
assets, hot-reload agents, SDK compatibility, and the SDK-bound evaluation DLL/query project.

Before starting the watcher, the resolver checks the selected muxer's `--list-runtimes` against the payload's
runtimeconfig frameworks under the effective roll-forward policy (the more permissive of the file's and
`DOTNET_ROLL_FORWARD`), and fails with the missing framework and how to fix it rather than a host error in the server log.

**Evidence:** `DotnetWatchCoordinatorTests` (resolution, errors, unsupported SDKs, shipped payload targets net10.0),
`DotnetWatchToolRuntimeTests` (runtime check) and the contract tests, which run against the shipped payload.

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

**Implemented:** evaluated preflight assigns isolated watcher groups before build-plan handoff.

When a supported CLI watch run is requested, ensure the tool, collect eligible project roots, add
**hidden system watch servers for compatible build groups**, and launch one **Aspire-owned proxy per resource
instance**. Keep infrastructure out of manifests and inappropriate user lifecycle commands; define
intentional startup and shutdown ordering, rather than adding an explicit-start server with no owner to start it.

Every project joins the coordinated build when added; at `BeforeStartEvent`, before the build plan is
created, watched projects leave it (the watcher builds them) and excluded projects (`WithWatch(false)`,
`WaitForCompletion` targets, ...) keep the coordinated build, build environment and launch. The watcher starts only
after the excluded projects' coordinated build finished, and a watched project's
`DotnetProgramBuildCompletionAnnotation` completes on the watcher's first successful build, so EF
`--no-build` operations wait for its output. Use the existing launch-tool/program-argument seam rather than
discarding all `WithArgs` or silently overwriting a custom tool launch such as the Blazor gateway.
Compatible C# AppHosts independently use the CLI generation controller described in Session 8.

Evaluated graphs include physical roots, active imports, references, SDK/muxer and output/intermediate
domains. The SDK-bound adapter uses public SDK evaluation, including the public file-app factory,
without loading multiple SDKs into the AppHost or parsing directives manually. Ordinary/custom roots
participate in startup isolation, build-file observation, and fresh guard evaluation.

After handoff, observed invalidation fences a group before guarded refresh. Old output reservations
remain until confirmed teardown. Internal refresh preserves intended-running versus manually stopped
instances; changed SDK/output context or new ownership overlap requires full run restart.
This is not atomic upstream pause, live repartition, or guaranteed discovery of future inactive imports.

Rebuild is disabled and rejected for watched resources, including access to their physical rebuilder.
Manual Stop remains available; post-Stop Start requires full run restart. Restart is restart-only for
one unambiguous last-observed running application. Replicas and duplicate entry points retain watching,
but their events are project-group observations and instance-specific Restart is rejected.
Proxy PID/exit/ACK never become application identity, completion, or readiness.

Implement the applicable compatibility/lifecycle gates in section 5, including meaningful failure and
status reporting. A bare `WaitForStart(server)` is not the complete readiness design, and the status
monitor is not optional where correctness depends on it. Do not assume the stock proxy's zero exit
code or `rebuild` control request supplies Aspire's lifecycle semantics.

**Acceptance:** from the TypeScript playground first, then C#, a service edit updates the intended
service and a shared-library edit updates both consumers. Verify server failure, initial-build failure,
restart/rebuild behavior, cleanup, and unsupported-context diagnostics, not only successful edits.

### Session 6a - Aspire service proxy (watch host)

**Implemented as `src/Aspire.Hosting.Dotnet.WatchHost`** (ship the proxy as an exe in
Aspire.Hosting.Dotnet; the CLI no longer knows about it). A net10.0 framework-dependent dll (`RollForward=Major`, no
apphost, so the nupkg stays RID-neutral) with two commands:

- `proxy --server <pipe> --entrypoint <project> --launch-gate <file> [--working-directory <dir>] -- <args>`: what DCP
  runs for each watched replica, with the project's own `dotnet` (`dotnet exec <host> proxy ...`).
- `server -- <muxer> exec <watch tool> server ...`: the hidden `dotnet-watch` resource's process. It supervises the
  watcher with the shared `ProcessSupervisor` (Windows job object / Unix process group, kill when the owner exits)
  and stops it gracefully, then forcefully after a grace period.

Aspire.Hosting.Dotnet packs the host's build output under `tools/dotnet-watch-host/`; its `buildTransitive` targets
stamp that directory on the AppHost as `aspiredotnetwatchhostdir` metadata (in-repo AppHosts use
`artifacts/obj/Aspire.Hosting.Dotnet/dotnet-watch-host/`). Prebuilt AppHost servers, which load
`Aspire.Hosting.Dotnet.dll` straight from its restored package, use only that package's `tools/` folder (Session 4). A missing host fails the run with an error naming the fix. The wire constants shared by hosting
and the host are source-linked from `src/Shared/DotnetWatch/DotnetWatchHostProtocol.cs`; there is no CLI/hosting
protocol handshake.

Original scope: implement a hidden, infrastructure-only proxy for D10. DCP launches it per instance with the
intended service environment delivered through process environment/private local transport; it sends
that environment in the watcher resource-pipe request. Do not indiscriminately forward every ambient
CLI variable or put values into `-e` arguments, logs, dashboard command lines, or persisted payloads.

The proxy ships next to the watch tool, so no CLI launch specification is passed to the integration; do not
resolve an arbitrary `aspire` from `PATH`. The proxy must avoid banners, prompts and payload dumps.

Own the pinned binary framing, serialization/profile states, output forwarding, disconnect cleanup,
cancellation, bounds checking, and failure handling. Version-byte acceptance alone does not prove full
semantic compatibility. Do not translate transport completion into a fabricated application exit code.
A future supported secure input channel in the upstream proxy may let Aspire replace this implementation,
but that is not available in the verified contract.

**Acceptance:** contract tests against the actual pinned watcher prove environment values reach the
service without command-line disclosure, stdout/stderr stay correctly separated, one proxy connection
owns one launched instance, and failures/cancellation never look like successful application completion.

### Session 7 - `aspire run --watch` CLI wiring

**Implemented.** Explicit `--watch[=true|false]` overrides `defaultWatchEnabled`; both select one combined mode.
Compatible project-based C# AppHosts use the bundled `host` command and generation replacement, not a
competing legacy `dotnet watch` loop. Managed-server guest AppHosts use language-native watching from that same effective choice.
The managed watch signal goes only into the final **AppHost server's** environment, not discovery or
the guest process. Native protocol-v2 selection is retained and runs normally without AppHost or managed
C# service watching. An effective watch request emits one warning that AppHost edits require stopping
and rerunning Aspire; explicit false suppresses the warning even when the feature default is enabled.
Native backend capability fences guest watching even when a toolchain can synthesize a watch command.
The CLI hosts neither the service proxy nor service watch server (Session 6a).

- `--no-build` with effective watch fails with a clear error that names the cause: an explicit `--watch`,
  or the `defaultWatchEnabled` feature (with `--watch false` as the way out).
- IDE-owned runs (`ASPIRE_EXTENSION_DEBUG_SESSION_ID` or `DEBUG_SESSION_PORT`) never start the CLI
  watcher; the IDE's own watch/debug protocol applies, `--no-build` stays allowed, and an explicit
  conflicting `--watch` is rejected with a clear ownership explanation.
- An effective CLI watch run in an Aspire Terminal keeps CLI ownership rather than delegating its
  wrapper to the extension. Ordinary IDE terminal delegation is unchanged.
- `aspire run --detach` forwards an explicit `--watch`/`--watch false` to the detached child.

Add the option, with explicit `--watch` enabling watch and `DefaultWatchEnabled` supplying the default.
Project `AppHost:Run:WatchEnabled` into the process that actually hosts `Aspire.Hosting`.
For polyglot runs that means the **AppHost server's environment**, not only the guest application's
environment. Hosting finds the watch tool and the watch host itself; nothing else is passed.
Unsupported C# wrappers retain ordinary AppHost launch with separately reported coverage; compatible
service groups are evaluated independently.

Preserve IDE ownership: VS Code/Visual Studio launches use their existing watch/debug/session-restart
protocol, not this package-backed CLI watcher. Reconcile flags, diagnostics, help/localization,
backchannel reconnect/cancellation, and existing `--no-build` behavior rather than leaving two unrelated
watch implementations active under the same effective setting.

**Acceptance:** CLI tests plus terminal E2E prove explicit/default watch selection, absence of acquisition
in non-watch runs, correct C#/guest signal delivery, existing option validation, and service hot reload.

### Session 8 - C# AppHost watch through `host`

**Implemented:** use the package-owned prelaunch resolve operation from Session 4, then launch a
compatible project-based C# AppHost with the resolved tool's `host` command.
Keep language-native AppHost watching for managed-server polyglot runs. Native-server configurations
retain normal execution with the warning-and-rerun limitation described in Session 7.

The requester chose **Aspire-controlled service launch**, not adoption of the package's built-in
execution service as a replacement backend. Establish a supported composition that prevents the
automatically injected external-launch settings from bypassing the proxy path or disabling coordinated
build ownership. CLI-only invocation is a boundary requirement, but does not suppress the package's
automatic service. An explicit private CLI owner marker keeps Aspire build/proxy ownership despite the upstream-injected
execution-service settings. It is not a blanket removal of real IDE settings.

The private, versioned, bounded, session-bound notification channel distinguishes a validated
HostStarted instance from metadata invalidation. Wrapped startup requires the initial real-instance
handshake within the existing startup budget. A different instance or model invalidation coalesces
one complete generation replacement; disconnect alone does not invalidate. Previous run/cleanup
faults are observed before replacement, which receives fresh backchannels and completion sources.
The new model determines startup, including `ExplicitStart`; stale DCP IDs and manual-stop intent
are not carried between complete AppHost models.

Also account for the AppHost package's `dotnet run` -> `aspire run` hook: watcher launches must not
recursively re-enter the CLI. Preserve the AppHost configuration/profile, forwarded arguments,
CLI backchannel, startup-failure behavior, and shutdown ownership.

**Acceptance:** supported AppHost edits update running code; topology changes actually reconstruct the
model through a verified restart/update path. Verify backchannel reconnection, old server/proxy/child
cleanup, changed root graphs, and continued Aspire ownership of service launch/builds. Do not equate
method-body hot reload with topology reevaluation.

### Session 9 - Watch end-to-end coverage, playground extension, and documentation

**Implemented coverage:** hosting/proxy contracts, generic core-hook and CLI unit tests, packed-payload
checks, and nine assembled CLI terminal scenarios. The scenarios cover C# AppHost generations,
managed and native TypeScript selection, replicas/duplicates, initial-build recovery and EF barriers,
commands, launch compatibility, evaluated isolation/refresh, HTTPS, and the bundled package contract.
Existing package/build/publish/debug and legacy-watch tests remain complementary coverage, not
substitutes for executing the assembled scenarios.

File-app coverage distinguishes syntax-error hot-reload recovery, genuine class-to-struct ENC
recovery with a fresh dedicated watcher, and the pinned SDK's accepted return-type delete/reinsert
replay limitation. The latter requires an explicit complete stop/rerun with the edit retained;
it is not counted as automatic recovery or diagnosed from repeated project-only start events.
That workaround verifies fresh owner/application identities and old-run cleanup, rather than
preserving an independent watcher's identity across a complete run replacement.

The terminal scenarios use the repository's `cli-e2e-testing` harness and run on Linux. Execute one
bounded scenario at a time when diagnosing failures, retain its HTTP/process evidence, and isolate a
failed phase before repeating a long scenario. Run applicable platform-level tests separately on
macOS and Windows; Linux success does not establish Windows named-pipe/job behavior or macOS socket
behavior. Record unavailable-platform validation explicitly rather than counting it as passed.

Combine the acceptance checks of Sessions 4, 6, 6a, 7, and 8 with the compatibility gates in section 5,
including rude-edit restart, invalid source edits, and recovery after correcting them.

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
| Exact entry point, arguments, profiles | The proxy passes `--project` for project files and `--file` for file-based C# apps. Continue verifying `.cs` apps and directories with multiple projects across the actual watcher wire contract, along with application-option forwarding and the three-state profile field; parser-level tests alone are insufficient. | 6, 6a |
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
