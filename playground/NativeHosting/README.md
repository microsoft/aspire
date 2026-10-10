# Lean native hosting feasibility spike

This is an isolated experiment for [#20873](https://github.com/microsoft/aspire/issues/20873), not a new supported AppHost runtime. It changes no shipped hosting implementation, CLI behavior, Nuxt package, dependency version, or generated API baseline.

## What it exercises

```text
TS experiment AppHost / RPC relay / native workload launcher
    |
    +-- Native AOT core <-- Node Nuxt integration: executable/build primitives
    |       |
    |       +-- Nuxt descriptor and deferred managed-provider identities
    |
    +-- Managed adapter: unchanged CLR Hosting
            +-- AddRedis -> DCP -> real Redis
            +-- Imported Nuxt facade: C# helpers/callbacks -> explicit core export
            +-- Managed consumer -> DCP -> HTTP -> real native-owned Nuxt

Harness launches real Nuxt -> authenticated Redis protocol -> real Redis
Native deferred value -> managed CLR provider -> context-specific value
```

The native core references only the BCL. The Node integration defines Nuxt using executable/build primitives, without the generated C# JavaScript SDK. The managed adapter calls the existing Redis integration directly, keeping its actual resources, password parameter, health checks, and lifecycle subscriptions. The original split scenario contains no CLR Nuxt resource and does not load `Aspire.Hosting.JavaScript`. A separate compatibility scenario imports a standard-interface CLR facade for native Nuxt, described below; it deliberately probes JavaScript concrete-type compatibility.

Native AOT still includes .NET GC and runtime support. This experiment establishes a native binary with no JIT or dynamic managed assembly loading in the core, not a core with no .NET runtime machinery. A different native implementation language remains a separate choice.

Resource descriptors retain an owner identity and logical resource name. Native state retains an unresolved Redis URI reference; each resolution asks the managed owner for its current value or publish expression. Resolved credentials and local ports are never written back into the native model.

The facade and managed consumer above exist only in the compatibility scenario; the original split scenario retains Redis alone in managed Hosting.

The original split scenario covers:

- Actual Nuxt 4 development-server reads/writes against password-authenticated Redis.
- Four concurrent native -> managed -> native callback chains without deadlock.
- Duplicate native names, foreign-session owners, and invalid execution modes.
- Publish expressions before allocation and after running, without local-value contamination.
- Actual built Nitro production-server Redis reads/writes.
- A production Docker image using the published URI lowered to `cache:6379`, not a host-allocated port. A secret placeholder is externalized in the recipe and supplied only at execution.
- Cancellation through the native/managed relay, recovery on the same connection, and a managed-owner disconnect during an outstanding request.
- EOF shutdown of integration processes and explicit cleanup of observed session helper processes and Docker resources.

## Running it

Prerequisites: macOS or Linux, .NET SDK installed by repository restore, Node.js 24, Docker, and the existing Nuxt sample's installed dependencies. The `ps`-based measurement/cleanup harness is Unix-specific. Windows transport and containment are not validated.

From the repository root:

```bash
./restore.sh

# Only when these sample dependencies are missing:
(cd playground/NuxtApp && npm install --no-package-lock)
(cd playground/NuxtApp/web && npm install --no-package-lock)

# Use linux-arm64/linux-x64/osx-x64 as appropriate for the machine.
dotnet publish playground/NativeHosting/Core/NativeHosting.Core.csproj \
  -c Release -r osx-arm64 /p:PublishAot=true -o artifacts/native-hosting/core

dotnet build playground/NativeHosting/ManagedAdapter/NativeHosting.ManagedAdapter.csproj \
  -c Debug /p:SkipNativeBuild=true

node playground/NuxtApp/node_modules/typescript/bin/tsc \
  --noEmit --module NodeNext --moduleResolution NodeNext --target ES2023 \
  --strict --types node --typeRoots playground/NuxtApp/node_modules/@types \
  playground/NativeHosting/apphost.mts playground/NativeHosting/nuxt-integration.mts \
  playground/NativeHosting/managed-consumer.mts

node playground/NativeHosting/apphost.mts

# Focused compatibility edges plus native-only activation, without the original
# Docker experiment and reduced managed baseline:
NATIVE_HOSTING_EDGES_ONLY=1 node playground/NativeHosting/apphost.mts
```

The two optional command-line arguments select the native executable and managed adapter DLL. `NATIVE_HOSTING_RESULTS=/absolute/path/results.json` preserves the results and both symbolic compatibility manifests beside that file, outside the temporary workspace. The harness copies the existing sample dependency manifest and links its already-installed modules into a securely created temporary directory; it does not edit dependency manifests.

It launches processes through private stdio pipes using JSON-RPC 2.0 with existing `vscode-jsonrpc` framing. This establishes AOT-safe framing and static dispatch, not interoperability with the production ATS dispatcher or SDK. The small C# peer is deliberately a prototype, not a proposed replacement RPC library.

## Feasibility findings

**A bounded mixed-owner composition is feasible.** Native descriptors, owner-routed deferred values, managed CLR execution, and reentrant callbacks can compose a real workload without copying the entire hosting model or loading a managed runtime into the native process.

**Unchanged C# integration execution remains managed.** The existing Redis implementation registers DI health checks and event subscriptions, constructs CLR resources and annotations, and evaluates its own expressions. Keeping those semantics requires managed execution and enough existing Hosting infrastructure to support them. A native router does not remove that cost.

**A small core is not yet a session memory improvement.** The native executable is approximately 2.8 MiB on disk and approximately 9.4 MiB resident on the tested macOS arm64 machine. The reduced comparison still has roughly 1.2 GiB of host-process RSS in both modes once Nuxt, the managed adapter, Node hosts, and detached DCP processes are included. Differences between individual runs are not evidence of savings.

These figures are exploratory, not a benchmark:

- The comparison uses direct managed Hosting for Redis/Nuxt, **not today's full ATS AppHost server**, metadata discovery, CLI, or current Nuxt external host.
- Five post-readiness samples are taken per scenario; startup is one observation and is sensitive to caches, order, and background contention.
- Parent ancestry alone misses detached DCP and its workloads. The harness additionally identifies DCP by the current adapter's monitor PID and includes its descendants.
- Host RSS excludes Redis/container memory in Docker's VM, shared-memory accounting, transient peaks, and processes that were not observed. No total-system-memory or startup-speed claim is justified.
- Shutdown is explicitly supervised by the harness. Its result records how many observed helper processes it signaled after the integration processes exited; this does not prove production owner-death containment.

**Standard-interface interoperation is feasible, but not transparent CLR compatibility.** The extended experiment applies existing C# APIs to an imported native-resource facade. Generic builders still need real CLR objects; the facade satisfies selected standard interfaces, not `ExecutableResource` or `JavaScriptAppResource`. A foreign ATS handle alone is not a CLR builder, annotation collection, or service.

## Compatibility-edge experiments

`ManagedAdapter/ManagedEdges.cs` imports `web` as a custom `Resource` implementing environment, endpoints, service-discovery, and wait interfaces. It intentionally does **not** derive from `ExecutableResource`: doing so would give managed DCP an independent workload to launch. Native descriptors remain in the core; CLR annotations and their original value providers remain in the managed owner.

Existing C# `WithReference(cache)`, `WithEnvironment`, `WaitFor(cache)`, `WithHttpHealthCheck`, and a custom resource command are applied to that facade. A graph-wide `OnBeforeStart` callback discovers it through the CLR model and adds an environment annotation. The bridge also publishes `BeforeResourceStartedEvent` before native launch, triggering an existing C# resource callback. An explicit export phase runs Hosting's `ExecutionConfigurationBuilder`, retains its unprocessed CLR providers, and transfers literal values or owner/provider identities into the native environment. The actual Nuxt process exposes the non-secret injected values over HTTP for assertion. This is a bounded annotation bridge, not interception of arbitrary collection mutation.

A real managed `ExecutableResource` receives `WithReference(web)`, an endpoint-valued environment variable, and `WaitFor(web)`. Managed DCP launches that consumer; it uses injected service discovery to call native Nuxt, which performs authenticated Redis `AUTH`/`SET`/`GET`. Nuxt itself is launched only by the harness. The CLR model contains `cache`, the `web` facade, and `managed-consumer`; the native core still has one executable descriptor.

| Edge | Observed result | Implementation implication |
|---|---|---|
| C# reference injection into native Nuxt | Standard Redis connection string, splatted properties, original/portable aliases, and reference relationships survive the bridge. Physical alias collisions still fail in the existing Hosting validator. | Import standard interfaces and reuse actual helper annotations; a single hardcoded URI is insufficient. |
| Native Nuxt referenced by managed workload | A DCP-launched consumer receives the same actual URL through service discovery and `EndpointReference` and completes the Redis round trip. | Import endpoint metadata and owner allocation into the CLR projection. |
| Deferred values and callbacks | The core keeps provider identities, not credentials. Runtime gathering uses Hosting's evaluate-once callback cache; repeating export does not rerun user side effects. Providers remain re-resolvable. | Separate callback evaluation lifetime from value-resolution lifetime. The pre-build inspection is deliberately separate and does invoke callbacks once before normal gathering. |
| Model enumeration and annotation mutation | `OnBeforeStart` finds the facade and its environment mutation reaches the launched native workload after explicit export. | A projection makes interface-based graph callbacks possible; define synchronization phases and ownership, not automatic transparency. |
| Concrete CLR types | The imported object is neither `ExecutableResource` nor `JavaScriptAppResource`. | Integration APIs or callbacks requiring those concrete types cannot consume this facade unchanged. Deriving from them would also require preventing their normal execution/publishing behavior. |
| Cross-owner startup | Waiting for all managed startup before launching Nuxt deadlocks: the managed consumer's configuration awaits the native endpoint. Concurrent owner startup plus Redis dependency readiness succeeds. | Coordinate startup across the graph, not sequential owner islands. |
| Readiness and commands | Standard managed HTTP health checks and `WaitFor(web)` release the consumer after observed native readiness. `ResourceCommandService` executes a custom command that calls the native owner. | Bridge lifecycle notifications and command dispatch. Backend snapshots are exercised; dashboard UI, log streaming, and built-in start/stop/restart commands are not. |
| Restart and stale state | A real Nuxt stop/relaunch on another port updates both fresh and previously captured endpoint references. Of four concurrent duplicate state updates, exactly one succeeds. Foreign-session state is rejected. | Serialize lifecycle publication, rebind observed allocations, and fence state updates. The spike's `generation` field is a monotonic state sequence, not a full resource-incarnation protocol. |
| Source network context | Existing Redis providers resolve to the allocated localhost port for a host consumer and `cache.dev.internal:6379` for the container network. | Preserve caller/network context in the RPC contract. Native Nuxt itself has only a localhost allocation here; container callers of native Nuxt remain unvalidated. |
| Actual native owner death | EOF terminates the native process; guarded managed provider resolution fails. Explicit invalidation marks the facade failed. An old built-in `EndpointReference` still returns its last URL. A new core rejects handles from the old owner session. | Owner death needs a production invalidation contract for all provider paths. Updating resource status alone does not revoke endpoint values. |
| Default manifest publisher | The actual Hosting publish pipeline emits `web.error`: this resource does not support manifest generation. | Standard interfaces alone do not make a custom facade publishable. |
| Bridged manifest publisher | An explicit callback emits experimental `native-executable.v0` build metadata and uses standard environment/binding writers. Managed consumer references remain symbolic; runtime URLs and credentials do not appear. | Add native publication metadata and deployment-target support. This experimental type is not understood by shipping deployment targets. |
| Formatted secrets | The actual manifest writer emits `cache-password-uri-encoded` as an `annotated.string` dependency for the Redis URI. Plain `ValueExpression` resolution omitted that dependency. | Preserve structured values and publisher dependency discovery, including formatting and conditional expressions. |
| Publish owner failure | Publishing against a terminated native owner fails the pipeline and RPC explicitly; it is not reported as a successful partial manifest. | Keep owners alive through publishing and propagate pipeline completion/failure, not merely `StartAsync` completion. |
| Native-only activation | Native core, Node integration, and built Nitro become healthy without launching any managed adapter. Five host-tree RSS samples are recorded. | Lazy managed activation is viable. This health-only scenario lacks Redis and is not a parity performance comparison. |

The reproduction assertions and negative cases live in `apphost.mts`; the managed consumer's actual HTTP assertions live in `managed-consumer.mts`. The full result object records the edge outcomes. With `NATIVE_HOSTING_RESULTS` set, `native-edge-manifest-false.json` and `native-edge-manifest-true.json` preserve the inspected publisher output without resolved secrets.

### Existing implementation evidence

- [HandleRegistry](../../src/Aspire.Hosting.RemoteHost/Ats/HandleRegistry.cs) stores live CLR objects; `GetObject<T>` requires an actual compatible CLR type.
- [CapabilityDispatcher](../../src/Aspire.Hosting.RemoteHost/Ats/CapabilityDispatcher.cs) scans assemblies, invokes reflected members, and unwraps builder handles into CLR resources.
- [ReferenceExpressionRef](../../src/Aspire.Hosting.RemoteHost/Ats/ReferenceExpressionRef.cs) resolves provider handles to objects and constructs managed reference-expression builders through reflection.
- [RedisBuilderExtensions](../../src/Aspire.Hosting.Redis/RedisBuilderExtensions.cs) registers connection-string events and DI health checks and attaches environment/argument callbacks to its CLR resource.
- [ResourceBuilderExtensions.WithReference](../../src/Aspire.Hosting/ResourceBuilderExtensions.cs) uses destination annotations, reference relationships, physical environment names, and source connection-property providers. Resolving one URI does not implement all of that behavior.
- [DistributedApplication](../../src/Aspire.Hosting/DistributedApplication.cs) passes the CLR application model to subscribers, lifecycle hooks, and pipeline execution. A native resource outside that model is not automatically visible.
- [DcpHost](../../src/Aspire.Hosting/Dcp/DcpHost.cs) deliberately launches a detached apiserver monitoring the AppHost PID, explaining why simple descendant-only memory sampling is incomplete.
- [The integration architecture spec](../../docs/specs/polyglot-integrations.md) already distinguishes moving DLL discovery out of process from establishing ATS-native resource semantics.

## What is not established

| Surface | Remaining boundary |
|---|---|
| Full ATS integration | Production capability/type discovery, generated SDKs, errors, authentication, reconnection, and handle lifetime are not wired to the prototype. Private parent-owned pipes provide the spike's trust boundary. |
| One application graph | Explicit facade import makes this bounded graph visible locally. There is still no global name registry, general graph query/mutation protocol, automatic import, or distributed cycle detection. |
| Existing callbacks over native resources | Standard-interface enumeration/environment mutation is demonstrated. Concrete CLR assumptions, arbitrary annotations, captured object identity, late collection mutations, certificates, and custom DI/lifecycle interactions remain unvalidated. |
| Native orchestration | Nuxt is launched by the harness, not a new native DCP executor. Port reservation has a release/launch race; production allocation must use the existing orchestration contract. |
| Unified dashboard | The compatibility model contains a native Nuxt facade with managed readiness, relationships, and custom command dispatch. Actual dashboard UI, unified logs, built-in controls, containment, and automatic owner-death reconciliation still require the control plane. |
| General run references | Run URI resolution uses the existing host/executable context. Network-aware `ValueProviderContext`, custom expressions, secret policies, conditional/TLS values, and custom property annotations need broader contracts. |
| Actual Aspire publishing/deployment | The real Hosting manifest pipeline is now exercised, including its default failure and an explicit bridge. CLI `aspire publish`/`deploy`, shipping deployment targets, and infrastructure provisioning are not. The separate Docker recipe remains a limited token-lowering experiment. |
| Supervision | The harness observes and reaps its own process identities, checking start time against PID reuse. Production must reuse `OwnedTree`, authenticate registration, propagate owner death, and validate Windows behavior. |
| Performance parity | Native-only activation and RSS are measured, but not against an equivalent current-server native-only baseline. Mixed-session savings and repeatable startup benefits remain unproved. |

## Implications for the next implementation

Do not migrate all integrations or replace the complete hosting engine on the strength of this spike.

Keep the existing in-process C# AppHost path unchanged. Bound the first mixed implementation to native executable workloads, imported standard-interface facades, existing managed dependencies, and explicit supported annotation/value operations. Concrete CLR resource APIs, arbitrary annotations, all deployment targets, and multi-owner graph-wide mutation are not automatically compatible and should not be included in that compatibility promise.

1. Define resource-owner identity, generation/invalidation, cross-owner value requests, and source/consumer network context using production ATS contracts.
2. Implement a capability-based managed facade projection with explicit graph synchronization phases. Preserve interface-based helpers and standard callback caching; classify concrete CLR operations as owner-local or requiring dedicated adapters.
3. Integrate the smallest native executable/container execution path with DCP, existing process containment, and the CLI. Reuse standard readiness and port allocation rather than the harness's launch logic.
4. Extend lifecycle projections to owner invalidation, all network contexts, logs, and built-in commands. Add real deployment-target/native-build support using structured publisher values and dependency discovery; a facade or expression string alone is insufficient.
5. Measure the actual current-server baseline and native-only, mixed, and managed-heavy applications with equivalent capabilities, complete session accounting, and repeatable startup trials.

The existing spec's stronger statement that every C# integration host consumes a projected single ATS model is not achieved by retaining unchanged integrations against CLR owner islands. That distinction needs a deliberate design decision, not an assumption that moving DLL discovery solves it.
