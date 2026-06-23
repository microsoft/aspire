# Project v2 stage 1 — `Aspire.Hosting.Dotnet` language integration + `aspire run --watch`

---

## 1. Goal

Deliver a minimal-but-real vertical slice of Project v2 that establishes the **language integration
package** as the unit that owns how a language's services are launched (run *and* watch):

1. A new experimental **`Aspire.Hosting.Dotnet`** package — the C# peer of `Aspire.Hosting.Go`,
   `Aspire.Hosting.Python`, and `Aspire.Hosting.JavaScript`. It hosts the relocated, **`ExecutableResource`-based**
   `CSharpAppResource` + `AddCSharpApp` (C# program added **by path**, polyglot-friendly).
2. **`aspire run --watch`** — a **sub-mode of `aspire run`**. In watch sub-mode the C# language
   integration package launches each C# service via the watch tool's **`resource`** command, coordinated
   by a hidden **watch `server`** system resource; the app host itself runs under the tool's **`host`**
   command (delivered as its own session). C# services **hot-reload**; other languages run in watch mode
   or normally depending on their own integration package's watch support.
3. The Aspire app model performs a single **coordinated initial build** (temp `.slnx`) of all C# services
   itself — identically for watch and non-watch — because the watch tool only does *incremental* builds.

The design must not preclude the full Project v2 vision (partial runs, persistent execution, container
execution, debugging-under-watch, programmatic build config, events/callbacks redesign), and must
generalize cleanly so Go/Python/JavaScript can add watch support later.

---

## 2. Decisions locked in with the requester (do not re-litigate)

| # | Decision |
|---|----------|
| **D1** | **New `Aspire.Hosting.Dotnet` language integration package**, structured as a peer of `Aspire.Hosting.Go` / `Aspire.Hosting.Python` / `Aspire.Hosting.JavaScript`. |
| **D2** | **Move `CSharpAppResource` + `AddCSharpApp` (+ the polyglot `addCSharpApp` export) out of core `Aspire.Hosting` into `Aspire.Hosting.Dotnet`, and rebase `CSharpAppResource : ExecutableResource`** (it is **no longer** a `ProjectResource`). Core keeps only `AddProject<T>` / `ProjectResource`. **Single type**, preserve the shipped names and the `ASPIRECSHARPAPPS001` diagnostic. This is an accepted breaking change to the experimental surface. |
| **D3** | **Activation is `aspire run --watch`** (watch is a **sub-mode of local run**, not a separate command). In watch sub-mode the **app host runs via the watch tool's `host` command** *and* **each C# service runs via the tool's `resource` command**, coordinated by a hidden watch **`server`**. |
| **D4** | **Only C# watch is implemented now.** Design a **general per-language-package watch seam** so Go/Python/JavaScript can adopt watch later, but do not implement them in this plan. Non-C# services run normally under watch until their package adds support. |
| **D5** | **Core exposes run sub-mode as state** on `DistributedApplicationExecutionContext` (e.g. `IsWatch` / a `RunSubMode`); language packages query it. **All watch mechanics** (server, `host`/`resource`/`server` commands, pipes, builds) live in the language package. Core is **not** involved in watch details. |
| **D6** | **Watch tool referenced from `Aspire.Hosting.Dotnet`** via a NuGet `PackageReference` (`GeneratePathProperty=true`) + a `.targets` file that injects the tool dll path as **app-host assembly metadata** (the DCP/dashboard/terminal-host pattern); the running app host invokes it with `dotnet exec`. **Not bundled in the CLI.** The CLI obtains the tool for the `host` command by resolving it from the **restored app host project** (handled in the app-host-watch session). |
| **D7** | **Coordinated INITIAL build is in scope**, owned by `Aspire.Hosting.Dotnet`: generate a temp `.slnx` of all `CSharpAppResource` `.csproj`s and run **one coordinated `dotnet build`** before services start — **identically for watch and non-watch**. The watch tool's `server`/`host`/`resource` perform only **incremental** builds, never the initial one. Library: `Microsoft.VisualStudio.SolutionPersistence`. |
| **D8** | **App-host watch via the tool's `host` command is a separate implementation session.** The earlier service-watch sessions run the app host normally; the host-command session layers app-host hot reload on top and reconciles/replaces today's whole-app-host `dotnet watch`. |
| **D9** | **Non-watch debugging/F5 parity is preserved** for the relocated `CSharpAppResource` (no regression vs today's `AddCSharpApp`). Because the type is no longer a `ProjectResource`, this requires generalizing the DCP project-launch/debug path (§6, R1). |

### Assumptions (flag if wrong)
- **A1.** The watch tool's CLI surface (`server` / `resource` / `host` flags) follows the
  `karolz-ms/aspire-watch` POC (§4.1). **Re-verify against the shipped `Microsoft.DotNet.HotReload.Watch.Aspire`**
  (currently `10.0.301` on nuget.org) since the POC may differ from the released tool.
- **A2.** Both `Microsoft.DotNet.HotReload.Watch.Aspire` and `Microsoft.VisualStudio.SolutionPersistence`
  must be available on an **approved internal feed mirror** (per `NuGet.config`) before use; mirror them if not.
- **A3.** The watch tool's `--sdk <dir>` must match the active SDK; resolve the SDK directory at runtime
  (e.g. via `dotnet --info`) rather than assuming the bundled/pinned version.
- **A4.** The watch tool does **not** perform the initial build (confirmed by the requester); the coordinated
  `.slnx` build (D7) is always required.

---

## 3. How it works today (research baseline — verified against current `main` as of 6/22/2026)

### 3.1 The watch tool protocol (from `karolz-ms/aspire-watch`; re-verify per A1)
Tool = `Microsoft.DotNet.HotReload.Watch.Aspire` (a `PackAsTool` dotnet tool, .NET 10); invoked as
`dotnet exec <tool>.dll <command> …`. Three commands — **all three are now used**:

```
server   --sdk <sdkDir> --server <pipe> --status-pipe <pipe> --control-pipe <pipe> --verbose \
         --resource <projA.csproj> --resource <projB.csproj> …
resource --server <serverPipe> --entrypoint <proj.csproj> --no-launch-profile --verbose -e KEY=VALUE …
host     --sdk <sdkDir> --entrypoint <apphost.csproj> --verbose [-- forwardedArgs]
```

Model: a hidden **`server`** holds the status/control pipes and owns incremental builds (service +
shared-library change handling); each service runs as a **`resource`** that connects to the server pipe
and `WaitForStart`s it; the **`host`** wraps the app host process itself under hot reload.

### 3.2 Language integration packages are the established pattern (the template for `Aspire.Hosting.Dotnet`)
`Aspire.Hosting.Go`, `Aspire.Hosting.Python`, `Aspire.Hosting.JavaScript` already demonstrate everything
the C# package needs:

| Concern | How the existing language packages do it |
|---------|------------------------------------------|
| Resource type | `GoAppResource` / `PythonAppResource` derive from **`ExecutableResource`** (e.g. `ExecutableResource(name, "go"/"python", workingDirectory)`), implement `IResourceWithServiceDiscovery`, `IContainerFilesDestinationResource`, etc. |
| Launch args | A `WithArgs(ctx => …)` callback builds the run command (`go run …`, `python …`). |
| Debug/IDE launch | **`WithDebugSupport(producer, "<type>", argsCallback)`** attaches a `SupportsDebuggingAnnotation` carrying a `*LaunchConfiguration : ExecutableLaunchConfiguration("<type>")` + an args transformer that strips the run-command args in IDE mode. Run-mode only. |
| Supporting executables | Hidden helper `ExecutableResource`s (e.g. `PythonInstallerResource`, `PythonVenvCreatorResource`, `JavaScriptInstallerResource`) added with `WithParentRelationship`, `WithExplicitStart`, and ordered via `WaitForCompletion` / `WaitForStart`. |
| Polyglot exposure | `[AspireExport]` on the resource type (`ExposeProperties = true`) and on the `Add*` method; code generators (`Aspire.Hosting.CodeGeneration.{TypeScript,Go,Python,…}`) + `Aspire.Hosting.RemoteHost` surface them to non-C# app hosts. |

### 3.3 DCP launch categorization (the one core change non-watch needs)
`Dcp/ExecutableCreator.cs`:
- `PrepareProjectExecutables()` iterates **`_model.GetProjectResources()`** (i.e. `OfType<ProjectResource>()`)
  and configures the **`"project"`** launch config there directly.
- `PreparePlainExecutables()` iterates `ExecutableResource`s; in `CreateObjectAsync` the generic debug
  annotator runs **only for launch types `!= "project"`** (lines ~163–172) — `"project"` is deliberately
  skipped because it is handled in `PrepareProjectExecutables`.

This means, once `CSharpAppResource` is an `ExecutableResource` (not a `ProjectResource`), a `"project"` launch
config on it would be applied **nowhere**. Preserving F5/debug parity (D9) therefore requires
generalizing this path (see R1). Non-debug run works regardless (plain `dotnet run --project …`).

### 3.4 Existing watch is the *inverse* of the v2 vision (must be reconciled)
Today watch is a CLI **feature flag** `KnownFeatures.DefaultWatchEnabled` (default `false`); there is **no
`--watch` CLI option**. When enabled, `DotNetCliRunner.RunAsync` runs the **whole app host** under
`dotnet watch` (`watch` vs `run`), and the guest path enables `enableHotReload`. The v2 vision is the
opposite (app host + services hot-reload via the watch tool's `host`/`resource`). The app-host-watch
session (D8) reconciles these by switching to the tool's `host` command.

### 3.5 Run/Publish plumbing pattern (template for the watch sub-mode signal)
`DistributedApplicationBuilder` reads `Configuration["Publishing:Publisher"]` to choose Run vs Publish and
builds `DistributedApplicationExecutionContextOptions` → `DistributedApplicationExecutionContext`
(`Operation`, `IsRunMode`, `IsPublishMode`). The `--publisher` CLI arg maps to that config key. The watch
sub-mode follows the identical pattern: a CLI config key → builder → a new state member on the execution
context.

### 3.6 Tool referencing pattern (template for D6)
`Aspire.Hosting.AppHost/build/*.targets` inject `AssemblyMetadata` (e.g. `dcpclipath`,
`aspiredashboardpath`, `aspireterminalhostpath`) from resolved bundle/package paths; `Dcp/DcpOptions.cs`
reads them at runtime (priority: explicit config > `ASPIRE_*` env var > assembly metadata). The dashboard
is invoked via `dotnet exec --runtimeconfig <cfg> <dll>`. `Aspire.Hosting.Dotnet` mirrors this for the
watch tool dll.

### 3.7 Reuse map
| Need | Reuse | Location |
|------|-------|----------|
| `ExecutableResource` + debug seam | `ExecutableResource`, `WithDebugSupport`, `SupportsDebuggingAnnotation`, `ExecutableLaunchConfiguration` | `Aspire.Hosting` |
| Project defaults (OTEL, `ASPNETCORE_URLS`, launchSettings/Kestrel endpoints, dev certs, rebuilder) | `WithProjectDefaults` & helpers (today `where T : ProjectResource`) | `ProjectResourceBuilderExtensions.cs` |
| Hidden/explicit-start/ordering | `WithHidden`, `WithExplicitStart`, `WaitForStart`, `WaitForCompletion`, `WithParentRelationship`, `ExcludeFromManifest` | `ResourceBuilderExtensions.cs` |
| Supporting executable pattern | `PythonInstallerResource` / `PythonVenvCreatorResource` wiring | `Aspire.Hosting.Python` |
| Hidden DCP build executable | `ProjectRebuilderResource` + rebuild command | `Aspire.Hosting/ApplicationModel` |
| Polyglot export + codegen | `[AspireExport]`, `Aspire.Hosting.RemoteHost`, `CodeGeneration.*` | various |
| Tool path → app host | `.targets` `AssemblyMetadata` + runtime reader; `dotnet exec` | `AppHost/build/*.targets`, `Dcp/DcpOptions.cs` |
| Temp build artifacts | `IAspireStore` / `IFileSystemService.TempDirectory` | `Aspire.Hosting` |
| CLI run/launch/backchannel | `RunCommand`, `AppHostLauncher`, `DotNetCliRunner`, backchannel | `Aspire.Cli/…` |

---

## 4. Target architecture

### 4.1 Component view (watch sub-mode)
```mermaid
flowchart TD
    CLI["aspire run --watch (CLI)"] -->|"set run sub-mode config; resolve watch tool<br/>from restored app host project"| HOST["watch tool: host --entrypoint apphost<br/>(app host under hot reload — session 6)"]
    HOST --> AH["App host process (C# or polyglot)"]
    AH --> Model["Aspire app model (Aspire.Hosting, core)"]
    Model -->|"ExecutionContext.IsWatch == true"| Pkg["Aspire.Hosting.Dotnet (language integration)"]
    Pkg -->|"coordinated INITIAL build (always)"| SLNX["temp .slnx → dotnet build<br/>(vs-solutionpersistence)"]
    Pkg -->|"adds hidden system resource"| WS["watch server (ExecutableResource, hidden)<br/>dotnet exec tool server --resource projA --resource projB …"]
    Pkg -->|"launch each C# service via tool"| P1["csharp svc A<br/>dotnet exec tool resource --entrypoint projA --server pipe"]
    Pkg -->|"launch each C# service via tool"| P2["csharp svc B<br/>dotnet exec tool resource --entrypoint projB --server pipe"]
    P1 -. "WaitForStart" .-> WS
    P2 -. "WaitForStart" .-> WS
    WS <-->|named pipes| P1
    WS <-->|named pipes| P2
    WS -->|"INCREMENTAL rebuild on change<br/>(service + shared library)"| P1
    WS --> P2
```
Non-watch run is identical minus the watch `server`/`host`/`resource` wrapping: the coordinated initial
build still runs, then each `CSharpAppResource` launches as `dotnet run --project …` (with `"project"`
debug launch config preserved for F5).

### 4.2 The core/package boundary (D5)
- **Core `Aspire.Hosting`** gains exactly one watch-aware concept: **run sub-mode as state** on
  `DistributedApplicationExecutionContext` (e.g. `bool IsWatch` and/or a `RunSubMode` enum), populated from
  a CLI config key the same way `Operation` is populated from `Publishing:Publisher`. Core contains **no**
  watch server, pipes, tool paths, or build logic.
- **`Aspire.Hosting.Dotnet`** reads `ExecutionContext.IsWatch` and, when true, adds the watch `server` and
  rewrites each C# service's launch to the tool's `resource` command. This is the **general seam**: any
  language package can do the same for its own resources later (D4).

---

## 5. New & changed types / files

> Resources → `Aspire.Hosting.ApplicationModel` (or the package's own namespace, matching Go/Python);
> builder extensions → `Aspire.Hosting`. All new/relocated public surface stays `[Experimental]`. Do **not**
> hand-edit `api/*.cs`, `*.Capabilities.txt`, `*.ats.txt` (generated).

### 5.1 `Aspire.Hosting.Dotnet` (new package)
- `src/Aspire.Hosting.Dotnet/Aspire.Hosting.Dotnet.csproj` (mirror `Aspire.Hosting.Go.csproj`: project-ref
  to `Aspire.Hosting`, `[AspireExport]` wiring, `api/Aspire.Hosting.Dotnet.cs`, README).
- **`CSharpAppResource`** (relocated, rebased): `public class CSharpAppResource(string name, string workingDirectory)
  : ExecutableResource(name, "dotnet", workingDirectory), IResourceWithServiceDiscovery, …` — sheds
  `ProjectResource` (and its container-build pipeline baggage). `[AspireExport(ExposeProperties = true)]`.
- **`AddCSharpApp`** (relocated): builds the `CSharpAppResource`, attaches `IProjectMetadata`, adds a
  `WithArgs` callback producing `run --project <proj>` (or the file-based `.cs` form), applies the
  generalized project defaults (§6.4), and `WithDebugSupport(mode => new ProjectLaunchConfiguration{…},
  "project", stripRunArgs)` for F5 parity. Keep the `Action<ProjectResourceOptions>` overload + validation
  (`.csproj`/`.cs`, .NET-version check) in `OnBeforeResourceStarted`.
- **`AddCSharpAppForPolyglot`** (relocated, `internal`, `[AspireExport("addCSharpApp")]`). Capability id
  moves from `Aspire.Hosting/addCSharpApp` to `Aspire.Hosting.Dotnet/addCSharpApp` (acceptable; experimental).

### 5.2 `CSharpWatchServerResource` (new, internal) — hidden system resource
`internal sealed class CSharpWatchServerResource : ExecutableResource` running
`dotnet exec <tool> server --sdk <dir> --server <pipe> --status-pipe <pipe> --control-pipe <pipe>
--resource <projA> …`. Added with `WithHidden`, `WithExplicitStart`, `ExcludeFromManifest`, lifecycle-command
exclusion (mirrors `AddRebuilderResource`). One per app run (MVP). Includes a small pipe-name helper
(ported from POC) and, optionally, a status-pipe monitor surfacing watch status into resource logs/state.

### 5.3 Coordinated build orchestrator (new, internal)
`internal sealed class CSharpAppBuildOrchestrator` (name TBD): collects all `CSharpAppResource` `.csproj`
paths → generates a temp `.slnx` (`Microsoft.VisualStudio.SolutionPersistence`) → runs one coordinated
`dotnet build` as a run-sequence step **before** services start, for **both** run modes. File-based `.cs`
apps are excluded (built/run individually). Reuses `IAspireStore`/temp-dir abstractions and the
`ProjectRebuilderResource`-style DCP-build-executable approach for log capture + cleanup.

### 5.4 Generalized project-defaults wiring (core ↔ package)
Today `WithProjectDefaults` / `SetAspNetCoreUrls` / rebuilder / launchSettings-endpoint logic are
`private`/`where T : ProjectResource` in `Aspire.Hosting`. The relocated `AddCSharpApp` (different assembly,
non-`ProjectResource`) must reuse them. **Decision to confirm during implementation (R2):** generalize to an
abstraction (e.g. an internal interface implemented by both `ProjectResource` and `CSharpAppResource`) and
expose to the package via `InternalsVisibleTo` **or** a small public `[Experimental]` surface — preferred
over reimplementing the defaults in the package.

### 5.5 Core run sub-mode state (new, minimal)
- Add `IsWatch` (and/or `RunSubMode { Normal, Watch }`) to `DistributedApplicationExecutionContextOptions`
  and surface read-only on `DistributedApplicationExecutionContext`.
- `DistributedApplicationBuilder` populates it from a CLI config key (e.g. `Run:Mode` / `ASPIRE_RUN_MODE`),
  exactly as `Operation` is derived from `Publishing:Publisher`.

### 5.6 Watch tool acquisition (D6)
- `Directory.Packages.props`: pin `Microsoft.DotNet.HotReload.Watch.Aspire` (mirror to approved feed; A2).
- `Aspire.Hosting.Dotnet`: `PackageReference … GeneratePathProperty="true"` + `build/Aspire.Hosting.Dotnet.targets`
  injecting the tool dll path as app-host `AssemblyMetadata`; a runtime reader (priority: env var > metadata)
  resolves it; invoked via `dotnet exec`.

### 5.7 CLI `aspire run --watch`
- Add a `--watch` `Option<bool>` to `RunCommand`; when set, pass the run sub-mode config signal to the app
  host (so the package reacts). Reconcile with `DefaultWatchEnabled` (explicit `--watch` wins; keep the flag
  as the default-on switch). The `host`-command wrapping of the app host is **session 6**.

### 5.8 Consumer migrations
- **`Aspire.Hosting.Blazor`**: `AddBlazorGateway` calls `AddCSharpApp` and currently returns
  `IResourceBuilder<ProjectResource>`. Once `CSharpAppResource` is not a `ProjectResource`, this **breaks** —
  add a project-ref to `Aspire.Hosting.Dotnet`, change the return type to `IResourceBuilder<CSharpAppResource>`,
  and adapt any `ProjectResource`-dependent gateway code.
- Polyglot SDKs / `api/*` / `*.Capabilities.txt` / `*.ats.txt`: regenerate. Any tests/playground using
  `AddCSharpApp` gain a `using`/package reference.

---

## 6. Work breakdown — agentic coding sessions

> Sequential unless noted. Every session ends **green**: `./build.sh` clean + targeted tests, and (where
> applicable) a manual run against a **TypeScript** app host first, then a C# app host. Keep all surface
> `[Experimental]`. Do **not** hand-edit generated `api/*` files.

### Session 1 — Scaffold `Aspire.Hosting.Dotnet`; relocate + rebase `CSharpAppResource`/`AddCSharpApp`
Create the package (mirror `Aspire.Hosting.Go`). Move `CSharpAppResource` (rebased `: ExecutableResource`)
and `AddCSharpApp` (+ polyglot `addCSharpApp`) out of core; remove them from `Aspire.Hosting`. Reproduce
**non-watch, non-debug** launch via `dotnet run --project …` args + generalized project defaults (§6.4).
Migrate `Aspire.Hosting.Blazor` (§6.8); regenerate polyglot SDKs/api. **Verify:** builds clean; a C# service
added via `AddCSharpApp` runs (no debug) from a C# app host **and** a TS app host with endpoints/env/service
discovery; Blazor gateway works. *Depends on: none.*

### Session 2 — Restore non-watch **debug/F5 parity** (DCP project-launch generalization)
Generalize the DCP path so a non-`ProjectResource` carrying `IProjectMetadata` + a `"project"`
`SupportsDebuggingAnnotation` is launched/debugged correctly (preferred: an annotation/metadata-driven
predicate; fallback: a dedicated prepare path or a distinct launch type). Preserve launch-profile behavior.
**Verify:** F5/debug of a `CSharpAppResource` (no watch) from a C# app host and the Aspire VS Code extension;
no regression vs today's `AddCSharpApp`. *Depends on: 1.* **(R1)**

### Session 3 — Core run sub-mode state (minimal, no mechanics)
Add `IsWatch`/`RunSubMode` to `DistributedApplicationExecutionContext(+Options)`; populate from a CLI config
key in `DistributedApplicationBuilder` (mirror `Publishing:Publisher`). No watch logic in core. **Verify:**
unit test that the config flag flips `ExecutionContext.IsWatch`; both run modes still behave normally.
*Depends on: none (parallelizable with 1–2).*

### Session 4 — Watch tool acquisition in `Aspire.Hosting.Dotnet`
Pin + mirror `Microsoft.DotNet.HotReload.Watch.Aspire` (A2); add the `PackageReference` +
`GeneratePathProperty` + `.targets` assembly-metadata injection + runtime resolver; invoke via `dotnet exec`.
**Verify:** from a built app host that references `Aspire.Hosting.Dotnet`, `dotnet exec <resolved tool> --help`
runs; re-verify the `server`/`resource`/`host` flag surface (A1). *Depends on: 1.*

### Session 5 — Coordinated initial `.slnx` build (vs-solutionpersistence)
Add/mirror `Microsoft.VisualStudio.SolutionPersistence` (A2). Implement `CSharpAppBuildOrchestrator` (§6.3):
temp `.slnx` of all `CSharpAppResource` `.csproj`s → one coordinated `dotnet build` before services start,
**identically for watch and non-watch**; exclude `.cs` apps; stream logs; fail fast. **Verify:** a multi-project
app **with a shared library** builds once, no write races, from a TS app host then a C# app host.
*Depends on: 1. Parallelizable with 2–4.*

### Session 6 — C# **service** watch: watch `server` + `resource` launch
Add `CSharpWatchServerResource` (§6.2). When `ExecutionContext.IsWatch`, the package (a) adds the hidden
watch server with all C# project paths, (b) rewrites each `CSharpAppResource` to `dotnet exec <tool> resource
--entrypoint <proj> --server <pipe> --no-launch-profile -e K=V …`, (c) `WaitForStart(server)`. Coordinated
initial build (session 5) runs first; the server owns incremental builds. Optional status-pipe monitor.
**App host still runs normally in this session.** **Verify (TS app host first, then C#):** edit a service file →
that service hot-reloads; edit the shared library → both services reload. *Depends on: 3, 4, 5 (and 1).*

### Session 7 — `aspire run --watch` CLI wiring
Add the `--watch` option to `RunCommand`; flow the run sub-mode signal to the app host; reconcile with
`DefaultWatchEnabled` (explicit flag wins). App host still launched normally (services hot-reload). **Verify
(TS first, then C#):** `aspire run --watch` → C# services start under watch and hot-reload end-to-end.
*Depends on: 6, 3.*

### Session 8 — App-host watch via the tool's `host` command (reverses D3; separate per D8)
CLI launches the app host via `dotnet exec <tool> host --sdk <dir> --entrypoint <apphost> …` (tool resolved
from the **restored app host project**, not bundled), replacing today's whole-app-host `dotnet watch`.
Reconcile `DefaultWatchEnabled`. Decide polyglot-app-host behavior (guest hosts under their own watch; the C#
app-host-server case). **Verify:** editing app host code hot-reloads the topology under `aspire run --watch`.
*Depends on: 7, 4.*

### Session 9 — Tests, playground & docs
TS playground using `addCSharpApp` under watch + a C# sample; CLI e2e for `aspire run --watch` (hex1b /
`cli-e2e-testing`); hosting tests for the package, watch-server wiring, sub-mode switch, and the `.slnx`
build; Verify-snapshot updates. Docs for experimental `Aspire.Hosting.Dotnet` + `aspire run --watch`,
limitations (no watch-debug, no partial runs yet), `ASPIRECSHARPAPPS001`. *Depends on: 7 (and 8).*

### Dependency graph
```
1 ─┬─► 2 ─────────────────────────┐
   ├─► 4 ─────────────┐           │
   └─► 5 ─────────────┤           │
3 ───────────────────►6 ─► 7 ─► 8 ─► 9
                       ▲           ▲
                       └─ (4,5) ───┘
```
Session 1 is the root. Sessions 2, 4, 5 parallelize after 1; session 3 is independent. Service watch (6)
needs 3+4+5; CLI `--watch` (7) needs 6+3; app-host watch (8) needs 7+4; tests/docs (9) last.

---

## 7. Compatibility with the annotation-based RunAs/PublishAs proposal ([#8984](https://github.com/microsoft/aspire/issues/8984))
The per-language launch decision is **annotation/state-driven** (`ExecutionContext.IsWatch` + the package's
own annotations), not type-bound — the natural insertion point for a future `RunAsWatch()` / `RunAsProject()`
/ `RunAsContainer()` union. Adding modalities (container, persistent) later is additive and stays inside the
language package. #8984 alignment is best-effort and must not compromise the core Project v2 vision.

## 8. Events / callbacks (noted; redesign deferred)
The slice touches `BeforeResourceStartedEvent` (path/SDK validation, env materialization), `WaitForStart`
ordering (server → services), and env/args callbacks (materialized into the `resource` command line). For the
MVP we **document** current ordering/cardinality but do **not** redesign the contracts. Watch point: env/args
callbacks may need to run for build/closure even when a resource isn't "running" (a known #16386 concern).

## 9. Risks & open items
- **R1 — DCP project-launch generalization (Session 2).** Making a non-`ProjectResource` debuggable via the
  `"project"` path could affect Azure Functions / file-based-app categorization. Mitigate with an
  annotation/metadata-driven predicate + regression tests; fall back to a dedicated prepare path or a distinct
  launch type recognized by DCP/the Aspire extension.
- **R2 — Cross-assembly reuse of `WithProjectDefaults` (Session 1).** Heavily `ProjectResource`-typed and
  `private`. Generalize via a shared internal interface + `InternalsVisibleTo`, or a minimal public
  `[Experimental]` surface; avoid divergent reimplementation.
- **R3 — Feed availability (A2).** `Microsoft.DotNet.HotReload.Watch.Aspire` and
  `Microsoft.VisualStudio.SolutionPersistence` must be mirrored to an approved internal feed before Sessions 4/5.
- **R4 — Watch tool ↔ SDK coupling (A3).** `--sdk <dir>` must match the active SDK; resolve at runtime.
- **R5 — Tool surface drift (A1).** Re-verify the shipped tool's `server`/`resource`/`host` flags vs the POC.
- **R6 — Named pipes cross-platform.** Verify `PipeOptions.CurrentUserOnly` semantics on macOS/Linux
  (POC was Windows-centric); TS-first verification exercises non-Windows.
- **R7 — `--watch` vs `DefaultWatchEnabled` vs the old `dotnet watch`.** Reconcile UX/strings so the explicit
  flag, the feature flag, and the host-command behavior don't collide (Sessions 7–8).
- **R8 — Polyglot capability namespace move.** `addCSharpApp`'s capability id moves to
  `Aspire.Hosting.Dotnet/addCSharpApp`; confirm guest SDK regeneration and any pinned references.
- **R9 — Blazor gateway breakage (Session 1).** Return-type and `ProjectResource`-dependency changes; covered
  by migrating it in the same session that relocates `CSharpAppResource`.
- **O1 — Run sub-mode shape.** `bool IsWatch` vs a `RunSubMode` enum (future-proof for more sub-modes).
- **O2 — App-host watch for polyglot hosts (Session 8).** How the tool's `host` command applies when the app
  host is TypeScript/Go/Python (guest runtime watch vs the C# app-host-server).

## References
| Description | Reference |
|---------|------|
| Project v2 vision and roadmap | https://github.com/microsoft/aspire/issues/16386 |
| Stage 1 scope for Project v2 (tracking issue) | https://github.com/microsoft/aspire/issues/15710 |
| Aspire watch prototype | https://github.com/karolz-ms/aspire-watch |
| Watch tool package | https://www.nuget.org/packages/Microsoft.DotNet.HotReload.Watch.Aspire/ |
| Hot reload & watch docs | https://aspire.dev/app-host/hot-reload-and-watch/ |
| Solution file editing library | https://github.com/microsoft/vs-solutionpersistence |
| Annotation-based resource flavoring proposal | https://github.com/microsoft/aspire/issues/8984 |
