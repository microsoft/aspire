# Lean native hosting feasibility spike

This is an isolated experiment for [#20873](https://github.com/microsoft/aspire/issues/20873), not a new supported AppHost runtime. It changes no shipped hosting implementation, CLI behavior, Nuxt package, dependency version, or generated API baseline.

## What it exercises

```text
TS experiment AppHost / RPC relay
    |
    +-- Native AOT core: native executable descriptors and deferred owner references
    |
    +-- Node integration process: Nuxt -> native executable primitives
    |
    +-- Managed adapter: unchanged AddRedis -> CLR Hosting -> DCP -> Redis

Native value resolution -> managed owner -> native consumer callback -> result
```

The native core references only the BCL. The Node integration defines Nuxt using executable/build primitives, without the generated C# JavaScript SDK. The managed adapter calls the existing Redis integration directly, keeping its actual resources, password parameter, health checks, and lifecycle subscriptions. It contains no CLR Nuxt resource in the split scenario and does not load `Aspire.Hosting.JavaScript` for that scenario.

Native AOT still includes .NET GC and runtime support. This experiment establishes a native binary with no JIT or dynamic managed assembly loading in the core, not a core with no .NET runtime machinery. A different native implementation language remains a separate choice.

Resource descriptors retain an owner identity and logical resource name. Native state retains an unresolved Redis URI reference; each resolution asks the managed owner for its current value or publish expression. Resolved credentials and local ports are never written back into the native model.

The harness covers:

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
  playground/NativeHosting/apphost.mts playground/NativeHosting/nuxt-integration.mts

node playground/NativeHosting/apphost.mts
```

The two optional command-line arguments select the native executable and managed adapter DLL. `NATIVE_HOSTING_RESULTS=/absolute/path/results.json` preserves the results outside the temporary workspace. The harness copies the existing sample dependency manifest and links its already-installed modules into a securely created temporary directory; it does not edit dependency manifests.

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

**Native -> managed value flow is easier than transparent model interoperation.** This spike avoids asking an existing C# API to mutate a native resource. Existing generic builders and concrete resource types still need real CLR objects. A foreign ATS handle is not automatically a CLR `IResourceBuilder<T>`, resource subclass, annotation collection, or service.

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
| One application graph | There is no global name registry or cross-owner graph query/mutation contract. Native and managed owners expose only the identities needed by this experiment. |
| Existing callbacks over native resources | CLR model enumeration, concrete resource checks, annotation mutation, and callbacks that expect a native resource to be a CLR object are not supported. |
| Native orchestration | Nuxt is launched by the harness, not a new native DCP executor. Port reservation has a release/launch race; production allocation must use the existing orchestration contract. |
| Unified dashboard | The managed model contains Redis, not the native Nuxt resource. Unified snapshots, logs, commands, readiness, restart, and relationships require a real cross-owner control plane. |
| General run references | Run URI resolution uses the existing host/executable context. Network-aware `ValueProviderContext`, custom expressions, secret policies, conditional/TLS values, and custom property annotations need broader contracts. |
| Actual Aspire publishing/deployment | The Docker experiment implements only the Redis URI tokens and rejects unknown tokens. It reuses the managed Redis container on a test network; it does not prove Redis provisioning, a general publisher, `aspire publish`/`deploy`, or deployment-target compatibility. |
| Supervision | The harness observes and reaps its own process identities, checking start time against PID reuse. Production must reuse `OwnedTree`, authenticate registration, propagate owner death, and validate Windows behavior. |
| Native-only sessions | Redis is present in every scenario. An application with no managed integrations should launch no managed adapter; that important memory case is not measured here. |

## Implications for the next implementation

Do not migrate all integrations or replace the complete hosting engine on the strength of this spike.

1. Define resource-owner identity, generation/invalidation, cross-owner value requests, and source/consumer network context using production ATS contracts.
2. Establish how owner islands participate in a single graph, execution lifecycle, dashboard, and publisher. Decide explicitly which standard operations use lightweight facades and which concrete CLR operations remain owner-local.
3. Integrate the smallest native executable/container execution path with DCP, existing process containment, and the CLI. Reuse standard readiness and port allocation rather than the harness's launch logic.
4. Exercise existing callbacks and publish/deploy targets against native resources, not just native consumers reading managed values.
5. Measure the actual current-server baseline and native-only, mixed, and managed-heavy applications with equivalent capabilities, complete session accounting, and repeatable startup trials.

The existing spec's stronger statement that every C# integration host consumes a projected single ATS model is not achieved by retaining unchanged integrations against CLR owner islands. That distinction needs a deliberate design decision, not an assumption that moving DLL discovery solves it.
