# Lean native hosting feasibility spike

This is an isolated experiment for [#20873](https://github.com/microsoft/aspire/issues/20873), not a new supported AppHost runtime. C# hosting remains managed and the default guest server selection stays unchanged. An explicit development-only CLI override now exercises the native server and extends the command's cleanup window. No Nuxt package, dependency version, or generated API baseline is changed. The original handwritten-RPC experiments remain available; the ATS-native server below uses generated ATS APIs instead.

## ATS-native integration authoring direction

[App Model v2](../../docs/specs/appmodelv2.md) is the living design companion for
this PoC. It maps the existing resource model to server-owned ATS entities,
structured values, relationships, and integration-owned behavior. The goal is
not one-to-one compatibility with the managed integration implementation API.

`CompatibleAppHost/apphost.mts` is a bounded experiment with ordinary production
guest APIs: `createBuilder`, Redis, PostgreSQL database children, JavaScript
workloads, references, Dev Tunnels, and `build().run()`. The exact sample compiles
against the full managed ATS-generated SDK. Its native backend uses a compact
wire adapter and external integration ports, not managed Hosting objects.
Unsupported options fail explicitly. This does not support arbitrary existing
AppHosts or integrations.

In this mode the CLI still calls `generateCode` and `getRuntimeSpec`. The native
server launches a separate managed codegen helper to invoke the real ATS scanner
and TypeScript generator. The helper's managed integration dependencies stay out
of the AOT core. The guest's pending `run` call keeps its lifetime active without
native-specific timers or bootstrap methods.

```bash
dotnet run --project playground/NativeHosting/AtsCodegen/NativeHosting.AtsCodegen.csproj \
  -- playground/NativeHosting --compatible
dotnet run --project playground/NativeHosting/AtsCodegen/NativeHosting.AtsCodegen.csproj \
  -- playground/NativeHosting --validate-apphost
dotnet publish playground/NativeHosting/AtsServer/NativeHosting.AtsServer.csproj \
  -r osx-arm64 -c Release -o artifacts/native-hosting/ats-server
NATIVE_HOSTING_COMPATIBLE=1 node playground/NativeHosting/cli-e2e.mts
```

Generate and compile the primitive SDK using the ATS commands below before
running the integration host. The compatible generator also writes its external
projection metadata. The harness supplies the helper paths, observes readiness
without modifying AppHost source, exercises Redis through Nuxt and the tunnel
fixture, queries the PostgreSQL database, and checks exact owned-container
removal. The sample is a compatibility probe, not the proposed v2 authoring API.

## Actual `aspire run` with the AOT core

`ASPIRE_CLI_NATIVE_APPHOST_SERVER` selects the native executable before the usual repository/bundle guest-server selection. The CLI retains its standard process factory, AppHost server session, authentication, SDK writing, language runtime execution, and graceful-shutdown infrastructure. Native server selection applies only to guest AppHosts; C# hosting remains managed.

```text
aspire run
  +-- NativeAppHostServerProject -> standard supervised AppHostServerSession
  |     +-- authenticated NativeAOT server
  |           +-- static ATS dispatch + embedded offline-generated TypeScript SDK
  |           +-- minimal graph, readiness, handles and controller lifetime
  |           +-- BCL kubeconfig reader + HTTPS/JSON -> actual DCP workloads
  |           +-- explicitly configured Node ATS integration host
  |                 Redis / PostgreSQL / Nuxt / Dev Tunnels behavior
  +-- ordinary TypeScript guest launch, using the server-generated .aspire/modules
  +-- CLI-side native headless adapter: actual readiness/control RPC + captured logs
```

DCP is part of the core's execution boundary, not a managed integration adapter. `Core/NativeDcp.cs` starts the API server, reads its generated connection material directly, validates TLS against its CA and host name, and performs authenticated BCL HTTP requests with source-generated JSON. It does not use `kubectl`, KubernetesClient, a YAML runtime, or `Aspire.Hosting`. The compact scalar reader is shared with the CLI's existing AOT-safe DCP doctor implementation.

For example, `NativeModel` creates services through the native DCP client:

```csharp
await _dcp.StartAsync(RpcPeer.RequiredString(args, "dcp"), startup.Token);

await _dcp.CreateAsync("services", service,
    new JsonObject
    {
        ["protocol"] = "TCP",
        ["addressAllocationMode"] = kind == "container" ? "Proxyless" : "Localhost"
    },
    null, cancellationToken);
```

The kernel commits primitive resource definitions, resolves deferred values and dependencies, creates DCP containers/executables/networks/volumes, and invokes integration-owned readiness/controller callbacks. Integration-specific defaults, Redis/SQL clients, database creation, and tunnel reconciliation remain outside it.

`CliAppHost/apphost.mts` uses the actual SDK supplied by this server:

```typescript
import { connect, createNativeBuilder } from './.aspire/modules/aspire.mjs';
import { CancellationToken } from './.aspire/modules/transport.mjs';
import { setTimeout as delay } from 'node:timers/promises';

const builder = await createNativeBuilder(await connect());
const cache = await builder.addRedis('cache');
const postgres = await builder.addPostgres('postgres');
const database = await postgres.addDatabase('app-db', 'native_app');
const web = await builder.addNuxt('web', webDirectory, cache);
const tunnel = await builder.addDevTunnel('tunnel', web, dataDirectory, true);
await builder.run(new CancellationToken());

// This prototype's run returns after readiness. Keep the guest alive until
// the CLI shuts it down; an unresolved Promise alone does not keep Node alive.
while (true) await delay(60000);
```

The CLI consumes genuine graph readiness, not merely an existing socket. The server keeps its integration host connected while custom resources are stopped, then closes sockets and terminates the host. Runtime-state snapshots serialize with graph teardown so reload cannot query an already-disposed builder. Ctrl+C uses the normal CLI shutdown ladder. The explicit native override gives that ladder an 80-second cooperative window, covering the integration's 30-second disposal hook and DCP's 45-second cleanup deadline. The normal five-second CLI window could kill this in-tree DCP while PostgreSQL was still stopping; managed Hosting instead detaches its DCP. Without the override, the existing CLI budget stays unchanged.

Whole-graph disposal uses DCP's `admin/execution` merge-patch protocol: request `CleaningResources` with `shutdownResourceCleanup: Full`, observe `CleanupComplete`, then request `Stopping` and await DCP exit. DCP owns dependency-aware resource and runtime cleanup; the core must not equate deleted Kubernetes objects with completed Docker removal. Administrative PATCH content types omit the charset parameter, matching the production client's wire format.

### Reproduce the real CLI path

First run the restore, offline SDK generation/compilation, and NativeAOT publish commands in the ATS section below. The native server embeds the generated `.mts` and compiled `.mjs` assets at build time; SDK changes require regenerating, compiling, and republishing, not a runtime generator.

From the repository root:

```bash
dotnet build src/Aspire.Cli/Aspire.Cli.csproj /p:SkipNativeBuild=true

NATIVE_HOSTING_CLI_RESULTS=/absolute/path/native-cli-results.json \
  node playground/NativeHosting/cli-e2e.mts

NATIVE_HOSTING_EXPECT_CLI_FAILURE=1 \
  node playground/NativeHosting/cli-e2e.mts
```

The harness prepares a private workspace, copies the sample and existing Nuxt inputs, reuses installed Node dependencies, and invokes **the built Aspire CLI's `run` command**, not an imitation of its protocol. Its subprocess command is equivalent to:

```bash
ASPIRE_CLI_NATIVE_APPHOST_SERVER="$PWD/artifacts/native-hosting/ats-server/NativeHosting.AtsServer" \
NATIVE_HOSTING_INTEGRATION_HOST="$PWD/playground/NativeHosting/ats-integration-host.mts" \
NATIVE_HOSTING_DCP=/absolute/path/to/dcp \
NATIVE_HOSTING_GUEST_DIRECTORY=/absolute/path/to/prepared/workspace \
NATIVE_HOSTING_PORT_DATA_DIRECTORY=/absolute/path/to/prepared/workspace \
NATIVE_HOSTING_TUNNEL_FIXTURE=/absolute/path/to/prepared/workspace/fixture \
DOTNET_ROOT="$PWD/.dotnet" \
  ./.dotnet/dotnet artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll \
  run --apphost /absolute/path/to/prepared/workspace/apphost.mts \
  --non-interactive --nologo
```

Set `NATIVE_HOSTING_ATS_BINARY`, `NATIVE_HOSTING_CLI_DLL`, or `NATIVE_HOSTING_DCP` to override the harness defaults; `NATIVE_HOSTING_NODE` selects the server's Node executable. The default DCP path is the previously tested macOS arm64 package. Node must support the sample's type-stripped `.mts` files. The prepared workspace needs the existing Nuxt dependencies and generated SDK's Node dependencies, which the harness arranges without editing package manifests.

For isolated Docker validation, set `DOCKER_CONTEXT` explicitly without changing the default context. Colima must be able to mount the workspace; the CLI harness creates it under `artifacts/native-hosting`. The older ATS socket harness uses the system temporary directory, so set its `TMPDIR` to a private repository-mounted directory when required.

The actual CLI path exercised real Redis `PING`, PostgreSQL `SELECT 1`, Nuxt HTTP, and local tunnel forwarding. It reported `dynamicCodeSupported: false`, observed CLI readiness notification, exited with code 0 on Ctrl+C, and verified owned containers were removed on repeated runs. The deliberate failing guest exited with CLI code 2 rather than leaving the session hanging; the harness bounds process exit by the native cleanup window plus final drain. The published server is approximately 7 MiB, with an observed core working set around 28-31 MiB; these are not whole-session or comparative performance measurements. The stdio mock and both socket guest generations also passed after the CLI/DCP changes.

**Boundaries:** this is a headless Unix/TypeScript exploration. There is no Dashboard or managed streaming-backchannel endpoint; logs come from the standard captured server output. CLI watch, publishing/deployment, separate-command discovery/stop, arbitrary package loading, and other languages are not implemented. Managed/npm integration declarations are rejected; implicit Hosting and TypeScript codegen references are accepted only as CLI metadata, not restored or loaded. The integration host is explicitly configured. DCP acquisition still uses a supplied executable. Dev Tunnels uses the local fixture, not the live relay. Explicit guest reexecution remains full graph replacement, not warm reconciliation.

## ATS-native AppHost server

The latest experiment replaces handwritten guest model calls with the repository's actual ATS scanner, TypeScript generator, generated handle proxies, callback marshalling, transport, and external integration-host runtime. Redis, PostgreSQL, and Dev Tunnels are bounded TypeScript ports over the same generated primitive API. Nuxt supplies the real executable and HTTP consumer used to exercise them.

```text
Build time only:
  minimal C# [AspireExport] contracts -> real ATS scanner
                                      +-> real TypeScript SDK / integration projections
                                      +-> static C# dispatch / source-generated JSON

Runtime:
  guest TypeScript AppHost -- generated ATS proxies --+
                                                     |
  Node integration host -- generated ATS callbacks ---+-> authenticated Unix socket
      Redis / PostgreSQL / Dev Tunnels                 |       |
      defaults, health, provisioning, reconciliation  |   Native AOT server
                                                     |       |
                                                     +-- generic resource/value graph
                                                         dependencies and lifecycle
                                                         custom controller fencing
                                                         BCL HTTPS -> real DCP
                                                             containers / executables
```

The kernel exposes three resource handles: `NativeBuilder`, `NativeResource`, and `NativeValue`. It handles primitive declarations, structured values, deferred endpoints/properties, parameters, readiness callbacks, compute/storage, custom-resource controllers, symbolic publication, cancellation, and graph lifetime. It contains no Redis, SQL, Nuxt, or Dev Tunnels service-specific dispatcher.

### Actual generated guest API

This is the entry shape used by `ats-guest.mts`, not manually declared TypeScript interfaces:

```typescript
import { connect, createNativeBuilder } from './generated/aspire.mjs';
import { CancellationToken } from './generated/transport.mjs';

const client = await connect();
const builder = await createNativeBuilder(client);
const cache = await builder.addRedis('cache');
const postgres = await builder.addPostgres('postgres');
const database = await postgres.addDatabase('app-db', 'odd"db');
const web = await builder.addNuxt('web', '/absolute/path/to/web', cache);
const tunnel = await builder.addDevTunnel('tunnel', web, '/private/workspace', true);
const consumer = await builder.addResource('consumer', 'executable', {
    executable: process.execPath,
    directory: '/private/workspace',
    runOnly: true,
});
await consumer.withArgument('/absolute/path/to/consumer.mjs')
    .withEnvironment('TUNNEL_URL', await tunnel.getProperty('url')).waitFor(tunnel);
await builder.run(new CancellationToken());
await database.query('SELECT 1', false, new CancellationToken());
```

`getProperty()` returns an opaque generated `NativeValue` handle, not the resolved URI. `withEnvironment()` records its structured expression in the core; evaluation occurs in the consumer's execution/network context. Resource arguments likewise cross ATS as typed opaque references to native registry objects. Incoming type IDs are checked against the server-recorded type, and handles from a disposed graph cannot be reused.

Integration exports also come from scanned contracts. `IntegrationContracts.cs` supplies build-time declarations; its stubs are excluded from native dispatch. Their real implementation is `ats-ports.mts`, registered through the unmodified generated `runIntegrationHost`. Integration callbacks can reenter the core through generated proxies while native startup or a command awaits their result.

### Reproduce

Use repository restore and the Node/Docker/DCP prerequisites described below. The harness reuses the existing Nuxt sample's installed modules; it does not change package manifests. From the repository root:

```bash
dotnet run --project playground/NativeHosting/AtsCodegen/NativeHosting.AtsCodegen.csproj \
  -- playground/NativeHosting
ln -sfn ../../NuxtApp/node_modules playground/NativeHosting/generated/node_modules

node playground/NuxtApp/node_modules/typescript/bin/tsc \
  --module NodeNext --moduleResolution NodeNext --target ES2023 \
  --strict --skipLibCheck --types node \
  --typeRoots playground/NuxtApp/node_modules/@types \
  --outDir playground/NativeHosting/generated playground/NativeHosting/generated/*.mts

node playground/NuxtApp/node_modules/typescript/bin/tsc \
  --noEmit --allowImportingTsExtensions \
  --module NodeNext --moduleResolution NodeNext --target ES2023 \
  --strict --skipLibCheck --types node \
  --typeRoots playground/NuxtApp/node_modules/@types playground/NativeHosting/ats-*.mts

dotnet publish playground/NativeHosting/AtsServer/NativeHosting.AtsServer.csproj \
  -c Release -r osx-arm64 -o artifacts/native-hosting/ats-server

NATIVE_HOSTING_EXPECT_AOT=1 \
NATIVE_HOSTING_ATS_RESULTS=/absolute/path/ats-results.json \
  node playground/NativeHosting/ats-e2e.mts
```

`PublishAot` is local to the server project. Do not pass it as a global command-line property to this project graph: that incorrectly applies AOT publishing to the netstandard compiler analyzer. Set `NATIVE_HOSTING_ATS_BINARY` to select another published binary and `NATIVE_HOSTING_DCP` to select the restored DCP for the machine.

The harness first uses a parent-owned stdio mock, then launches the actual native server entry point with `REMOTE_APP_HOST_SOCKET_PATH` and `ASPIRE_REMOTE_APPHOST_TOKEN`. The latter serves independent authenticated guest and integration-host connections. Its socket must be inside a private, existing directory. The guest uses real generated `connect()`; the integration process uses real `runIntegrationHost`, not the mock's registration adapter.

It exercises authenticated Redis/SQL and rejected credentials, an escaped physical database name, data surviving container replacement, real Nuxt HTTP, deferred custom endpoint consumption, tunnel stop/restart/rebinding, run-only publication exclusion, unchanged symbolic publication after execution, bounded query cancellation and recovery, forged handle types, foreign custom-controller rejection, atomic failed host registration, and rejected second-guest disconnection. The forwarding fixture is explicit: **the live Dev Tunnels relay remains unvalidated**.

PostgreSQL uses a private host bind directory; Redis uses a session-owned named volume. PostgreSQL retains normal initialization syncing and gives database creation a separate bounded filesystem-work budget. Resource startup budgets begin after dependencies become ready, so slow parents do not consume a child's initialization budget. Storage survives replacement within one graph, not graph disposal.

### Guest reload policy

The socket harness executes two independent guest processes while keeping both the native server and Node integration host alive. Guest EOF calls the integration's bounded `releaseGraph` hook while old handles still work, stops and joins controllers, deletes experiment-owned tunnels, unregisters callbacks, then revokes handles, cancels native operations, and removes the old DCP workloads, network, and volumes. Structured idle state permits the next guest generation to start.

The second guest rejects a captured old handle, rebuilds the graph, and supplies a different environment value that is observed through real Nuxt HTTP. This establishes **guest reexecution with full graph replacement**, not warm reconciliation, preserved Redis data across reload, an automatic file watcher, or in-place SDK replacement. Unexpected integration-host death also disposes the graph, but remote service cleanup cannot be guaranteed after losing that owner; the tunnel's bounded expiration remains relevant.

### Dependency findings and remaining work

The scanned contract currently has 33 capabilities, four ATS handle entries including cancellation, and 11 DTOs. The published macOS arm64 server is approximately 7 MiB including its embedded offline SDK. Its managed dependency manifest contains only the two experiment assemblies and the .NET runtime pack. Native AOT still includes .NET GC/runtime support. Neither Hosting, TypeSystem, scanner/codegen, reflection-based capability dispatch, client libraries, nor integration assemblies load into the running native core.

Generation, strict SDK/integration TypeScript compilation, Native AOT publication, the mock, and both real socket-server guest generations passed. All three reported `dynamicCodeSupported: false`; query cancellation completed in 302-305 ms and subsequent queries succeeded. The native core's three post-readiness working-set observations were approximately 28-32 MiB, not whole-session measurements or a repeatable performance comparison. The original named-RPC tunnel scenario and native ports, including unchanged managed Redis, also passed after the shared-core changes.

Final container runs used an isolated disposable Colima Docker VM selected through `DOCKER_CONTEXT`, with `TMPDIR` inside its repository mount. The shared Docker Desktop VM had exhausted its filesystem; no unrelated containers, volumes, images, or cache were pruned, and the active Docker context was not changed. The isolated VM was removed after validation.

Build-time dependencies are the real TypeSystem/scanner/TypeScript generator, linked export attributes, integration analyzer, SDK, and AOT toolchain. Runtime dependencies outside the core remain Node plus the generated ATS runtime's packages, DCP, Docker/images, Nuxt, image-local `psql`, and the Dev Tunnels CLI or explicit fixture. This is a dependency boundary, not proof of whole-session memory or startup savings.

The generated DTO properties currently include optional TypeScript fields even for required C# members; the integration validates observations before using them. Structured value parameters require awaiting the value-producing capability before passing its handle.

Two production seams are exposed explicitly:

- The current generator always emits a managed `createBuilder` helper referencing Hosting-specific types, even when those contracts are absent. The offline tool removes only that helper, retains the actual generated capabilities/transport, and fails if the helper's shape changes. A kernel-neutral bootstrap should become a supported generator feature.
- Remote callback cancellation tokens currently reach local protocol/CLI operations through a bounded generated status poll. A push-based local cancellation adapter should be extracted from the production transport instead.

This server speaks the exercised ATS subset and supports the **explicit headless `aspire run` override above**, not general AppHost server replacement. Production acquisition, arbitrary SDK/package loading, other language runtimes, Dashboard and managed streaming backchannels, full ATS schema/collection/union support, reconnection policy, managed compatibility adapters, deployment lowering, Windows transport, and warm graph reconciliation remain work. The original managed facade experiments below establish only their explicitly tested compatibility edges; this ATS port does not expand that promise.

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

The facade and managed consumer above exist only in the compatibility scenario; the original split scenario retains Redis alone in managed Hosting. A separate native-primitive scenario now drives real DCP directly from the AOT core, with no managed adapter, as described below.

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

## Native primitive ports

`native-ports.mts` explores three different resource shapes rather than translating every helper in an existing integration:

```text
TS experiment AppHost / RPC relay
    |
    +-- Node integration process
    |       +-- Redis defaults, authenticated RESP health, connection properties
    |       +-- PostgreSQL defaults, authenticated health, child database creation
    |       +-- Nuxt command/build conventions and HTTP readiness
    |
    +-- BCL-only Native AOT core
            +-- Immutable generic resource/value/dependency graph
            +-- Parameters, deferred properties, endpoint/network resolution
            +-- Dependency-aware startup and reentrant integration callbacks
            +-- HTTPS DCP client -> DCP
                                   +-- Redis container + session volume
                                   +-- PostgreSQL container + session volume
                                   +-- Redis client container on the shared network
                                   +-- Nuxt executable + allocated HTTP service
```

Neither the integration process nor the core loads `Aspire.Hosting`, a managed adapter, Npgsql, StackExchange.Redis, or KubernetesClient. DCP and Docker remain real workload dependencies. The core reads DCP's generated scalar kubeconfig directly and talks to DCP through BCL `HttpClient`, validating the session certificate authority and hostname and using its generated credentials. DCP acquisition remains a supplied executable path, not a production acquisition contract.

The ports retain selected behaviors of the existing integrations:

| Integration shape | Real behavior exercised | Integration-local implementation |
| --- | --- | --- |
| Redis container service | Password authentication, rejection of bad credentials, URI/host/port/password properties, authenticated Nuxt read/write, AOF data surviving container replacement | Image/command defaults, Redis environment binding, RESP health and test commands |
| PostgreSQL parent and database child | SCRAM authentication, rejection of bad credentials, creation after parent readiness, a logical `app-db` with physical database name `odd"db`, composed URI, table data surviving server replacement | Image/auth defaults, SQL identifier escaping, database creation and authenticated queries |
| Nuxt executable | Actual DCP-owned process, dependency on healthy Redis, HTTP readiness, deferred Redis injection, replacement and environment rebinding | Development command, endpoint environment convention, HTTP probe, symbolic build metadata |

The PostgreSQL integration uses `psql` inside the pinned PostgreSQL image as its protocol client. On macOS, it reaches the actual DCP-allocated host port through `host.docker.internal`; it does not rely on a trusted local socket or run a managed client. This demonstrates where the client belongs, not a recommendation to shell out for every production health check. PostgreSQL connection and statement timeouts are bounded, and its password is passed through environment rather than command-line arguments.

The core contains no Redis, PostgreSQL, SQL, or Nuxt dispatch branches in `NativeModel` or `NativeDcp`. The original descriptor experiment's hardcoded `withReference` remains isolated in the legacy path. The new path binds a consumer-selected environment key to a generic resource-property expression; the integration decides that Nuxt expects `NUXT_REDIS_URI`.

### What the ports imply for the core

| Primitive | Why it is needed | What exists in this experiment |
| --- | --- | --- |
| Resource identity and immutable configuration | All integrations need names, owner validation, and a publishable source model | Owner/name handles, duplicate rejection, model sealing, separate runtime state |
| Structured values | Connection properties compose secrets and endpoints, including a child's inherited server values | Literal, parameter, endpoint, property, concatenation, URI formatting, and existing owner-routed value requests |
| Endpoint/network resolution | A host executable needs allocated ports; a container needs a network alias and target port | DCP service allocation, a session container network, host/container resolution |
| Compute and storage | Redis/PostgreSQL are containers; Nuxt is an executable; replacement must retain data | Explicit DCP JSON for containers, executables, services, networks, and session-scoped volumes |
| Dependency and readiness coordination | Database creation follows parent health; Nuxt follows Redis health | Parallel dependency graph startup, integration RPC health/initialization callbacks, bounded cancellation |
| Targeted operations and containment | Restart must not race itself or retain an old endpoint allocation | Conflicting restart rejection, allocation invalidation, explicit dependency rebinding on consumer replacement, DCP execution-document cleanup on EOF |
| Publication | Runtime secret values and local ports must not overwrite source expressions | Canonical `native-model.v0` JSON and symbolic resolution, unchanged before/after execution |

`portFor` and `portForServing` are not interchangeable: the former consumes a service port; the latter supplies the workload port associated with its producer annotation. The executable path uses `portForServing` plus a localhost service so DCP allocates and connects both sides. Treating service allocation as a random-port helper produced a real startup/endpoint failure during exploration.

An additional Redis client container consumes deferred host/port/password properties and performs an authenticated write to `cache:6379` over the actual DCP network. This validates container-context injection with a real consumer, not just expression resolution.

The graph checks also exercise dependency/value cycles, a canceled integration-readiness callback, dependent failure propagation, reentrant queries while startup is blocked, and continued RPC use after cancellation. Successful EOF cleanup removes all three current containers, the network, the session volumes, and the observed DCP-owned workload/helper processes without Docker cleanup in the native-only harness.

A separate mixed scenario uses the new DCP-backed native Nuxt path with unchanged managed `AddRedis`. Generic owner-routed expressions resolve through the CLR provider, including five native/managed reentrant callbacks; the managed model contains only Redis and does not load the JavaScript integration. Closing that managed owner makes subsequent deferred resolution fail explicitly. Its existing detached-DCP shutdown and stopped-container retention policy is preserved; the harness separately supervises its helper processes and removes its exact observed stopped container. The older CLR-facade compatibility scenario also remains separate and still works with the extended core.

### Reproduce the native ports

Run the restore and AOT publish commands above first. Additional prerequisites are Docker and the repository-selected DCP executable. The default DCP path selects the tested macOS arm64 package; set `NATIVE_HOSTING_DCP` to the appropriate repository-restored DCP path on another machine.

```bash
node playground/NuxtApp/node_modules/typescript/bin/tsc \
  --noEmit --module NodeNext --moduleResolution NodeNext --target ES2023 \
  --strict --types node --typeRoots playground/NuxtApp/node_modules/@types \
  playground/NativeHosting/integration-ports.mts playground/NativeHosting/native-ports.mts

NATIVE_HOSTING_RESULTS=/absolute/path/native-ports-results.json \
  node playground/NativeHosting/native-ports.mts
```

The harness creates a private temporary Nuxt workspace and reuses the existing sample dependencies without changing package manifests. The pinned Redis 8.6 and PostgreSQL 18.3 images are pulled by DCP if missing. Resource names and storage are isolated by session identity; volumes survive replacement within that session but are deliberately deleted on session shutdown. This is not cross-session persistence.

### Remaining scope and footprint

Before the custom tunnel extension below, the additional core implementation was roughly 800 lines across `NativeModel.cs` and `NativeDcp.cs`, excluding the earlier RPC peer and descriptor experiment. The Redis/PostgreSQL/Nuxt integration process is roughly 280 lines. These sizes describe a narrow experiment, not an estimate for a production rewrite.

Before the custom tunnel extension, the extended AOT binary was approximately 5.8 MiB on disk and 23 MiB resident in the measured run. The complete native scenario's five post-readiness host-process samples had a median near 1.2 GiB, including the integration process, Nuxt, both DCP processes, and observed Docker helpers. Container memory in Docker's VM, precise shared-memory accounting, peaks, and the harness itself are excluded. The scenario includes PostgreSQL and an additional client container and has no equivalent managed baseline here; these figures establish a footprint, not a memory or startup improvement.

This is not a full Redis/PostgreSQL/Nuxt port or a production native server. Missing behavior includes TLS/certificate trust, existing databases and custom creation scripts, Redis modules, admin companions, resource log/dashboard integration, continuous health/drift reconciliation, cross-session volume policy, automatic dependent restart, complete owner-death recovery, portable PostgreSQL probing, production ATS/schema validation, and CLI acquisition/launch integration. A parent's replacement does not automatically re-run its child's creation callback or restart its consumers; the harness explicitly replaces Nuxt to re-evaluate its environment.

`native-model.v0` is an experimental source graph, not an Aspire deployment manifest. It retains structured expressions, callback identities, parent metadata, and Nuxt build metadata; there is no new deployment-target lowering or native build executor. The earlier compatibility experiment remains the evidence for running the actual managed manifest pipeline. Production publication still needs execution-mode policies, target-specific graph transforms, formatted-secret dependency discovery, build pipelines, and deployment output.

The useful reuse boundary is DCP's existing workload controllers and wire protocol, plus an optional managed adapter for existing CLR integrations. Integration defaults can be re-expressed outside the core; existing DI health checks, Npgsql/client use, annotations, event subscriptions, and publisher extensions cannot simply be linked into this BCL-only AOT process. They must stay managed or be implemented against equivalent portable primitives. These ports identify those primitives without claiming transparent CLR compatibility.

## Dev Tunnels custom-resource port

`devtunnel-integration.mts` is a bounded experimental port of the existing Dev Tunnels shape: a run-only executable hosts a tunnel, and a custom port resource exposes its public endpoint. It does not modify `Aspire.Hosting.DevTunnels`, replace its monitor, or adopt existing customer tunnels.

```text
Native Nuxt HTTP endpoint
    ^
    | forwarding
DCP-owned devtunnel executable <-- external Node tunnel integration
                                      |
                                      +-- CLI auth/provisioning and port reconciliation
                                      +-- DCP log observation and tunnel-output parsing
                                      +-- custom port state / endpoint updates over RPC
                                                    |
BCL-only AOT core <----------------------------------+
    +-- run-only custom port: generation, revision, state, public URL
    +-- dependent DCP executable consumes deferred TUNNEL_URL
```

The core adds a generic `custom` primitive and controller operations in `NativeCustomResources.cs`. It does not know about tunnel IDs, Dev Tunnels URL formats, authentication, or CLI commands. The external integration discovers and validates the URL, then reports the allocated endpoint and readiness. The custom port has no DCP executable/container of its own.

The experiment covers:

- Run-only owner, port, and consumer resources excluded from publication. A publish request for a run-only URL fails explicitly; this prototype does not implement the shipped helper's publish-mode no-op reference injection.
- A real DCP-owned Nuxt process and dependent executable consuming the custom resource's deferred URL, with an actual forwarded HTTP health response.
- Stop/restart commands, revoked endpoint values during unavailability, target-port reconciliation after replacing Nuxt, and consumer replacement to re-evaluate its inherited environment.
- Monotonic controller generations and state revisions. Concurrent duplicate updates accept exactly one; foreign owners, stale generations, invalid URLs, conflicting commands, and post-cancellation updates are rejected.
- Cancellation and controller disconnection during an outstanding command. The port becomes terminal and no longer resolves its old endpoint.
- Tunnel-host process exit observed through DCP, failed custom-port state, and endpoint invalidation. The integration uses DCP's log subresource rather than assuming DCP exposes stdout files.
- Explicit relay-reported integration EOF, endpoint invalidation, and removal of that controller's DCP executable. Owner identity is a prototype routing identifier, not an authenticated capability.

**Local forwarding is validated; the live Dev Tunnels relay is not.** The local run substitutes `devtunnel-fixture.mts` for the CLI. This fixture implements the narrow provisioning/output contract and a real loopback HTTP forwarder to DCP-owned Nuxt. It never contacts the Dev Tunnels service. Production parsing accepts validated HTTPS `*.devtunnels.ms` endpoints; loopback HTTP acceptance is enabled only for this explicit fixture mode.

A live run was attempted with the installed CLI and stopped at authentication preflight because its login had expired. That check runs before DCP/Nuxt launch and before any remote tunnel creation. The experiment does not automatically log in, copy credentials from another tool, or quietly substitute the fixture after live authentication fails.

### Run the custom-resource experiment

Use the restore/AOT publish prerequisites above, then:

```bash
node playground/NuxtApp/node_modules/typescript/bin/tsc \
  --noEmit --allowImportingTsExtensions \
  --module NodeNext --moduleResolution NodeNext --target ES2023 \
  --strict --types node --typeRoots playground/NuxtApp/node_modules/@types \
  playground/NativeHosting/devtunnel-output.mts \
  playground/NativeHosting/devtunnel-integration.mts \
  playground/NativeHosting/devtunnel-fixture.mts \
  playground/NativeHosting/tunnel-consumer.mts \
  playground/NativeHosting/native-tunnels.mts

# Local CLI/output fixture, real DCP and real HTTP forwarding:
NATIVE_HOSTING_RESULTS=/absolute/path/native-tunnel-results.json \
  node playground/NativeHosting/native-tunnels.mts

# Explicit authentication prerequisite for the actual service:
devtunnel user login

# Actual Dev Tunnels service. Creates a fresh, short-lived tunnel and explicitly
# enables anonymous access to the disposable health-only Nuxt experiment.
NATIVE_HOSTING_LIVE_TUNNEL=1 \
  NATIVE_HOSTING_RESULTS=/absolute/path/native-tunnel-live-results.json \
  node playground/NativeHosting/native-tunnels.mts
```

`NATIVE_HOSTING_DEVTUNNEL` selects the live CLI executable; `NATIVE_HOSTING_DCP` selects DCP as above. The health-only Nuxt fixture does not run Redis or exercise its credential-bearing route. Results label the transport and whether a live relay was actually validated. The live preflight failure is persisted as a failure result, not a successful local test.

The port intentionally creates a fresh random tunnel ID and explicitly deletes that experiment-owned tunnel during orderly cleanup. This is not the shipped integration's persistent remote-resource policy. Unexpected integration EOF invalidates the local resource and removes its DCP executable, but cannot guarantee deletion of remote state without working service access; the experiment sets a one-hour expiration and never adopts a user's existing tunnel.

The remaining production scope includes authenticated transport ownership, automatic EOF dispatch rather than the prototype relay's explicit call, startup/disconnect races, login coalescing/interaction, complete CLI output/version coverage, access-policy reconciliation, existing persistent tunnels, dashboard command/log/URL integration, and supported run/publish reference semantics. The custom monitor is integration-local and narrower than the shipped `DevTunnelMonitor`; its local fixture is not evidence of service compatibility.

## Feasibility findings

**A bounded mixed-owner composition is feasible.** Native descriptors, owner-routed deferred values, managed CLR execution, and reentrant callbacks can compose a real workload without copying the entire hosting model or loading a managed runtime into the native process.

**Unchanged C# integration execution remains managed.** The existing Redis implementation registers DI health checks and event subscriptions, constructs CLR resources and annotations, and evaluates its own expressions. Keeping those semantics requires managed execution and enough existing Hosting infrastructure to support them. A native router does not remove that cost.

**A small core is not yet a session memory improvement.** Before the DCP-backed primitive extension, the native executable was approximately 2.8 MiB on disk and approximately 9.4 MiB resident on the tested macOS arm64 machine. The original reduced comparison had roughly 1.2 GiB of host-process RSS in both modes once Nuxt, the managed adapter, Node hosts, and detached DCP processes were included. The larger current core's footprint is recorded above. Differences between individual or unequal runs are not evidence of savings.

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
| Full ATS integration | The legacy compatibility experiments use private handwritten RPC. The ATS-native checkpoint above adds real offline discovery/generated SDKs, authenticated sockets, structured errors, and graph-scoped handles for a bounded contract. Full dynamic discovery, protocol coverage, and reconnection remain unimplemented. |
| One application graph | Explicit facade import makes this bounded graph visible locally. There is still no global name registry, general graph query/mutation protocol, automatic import, or distributed cycle detection. |
| Existing callbacks over native resources | Standard-interface enumeration/environment mutation is demonstrated. Concrete CLR assumptions, arbitrary annotations, captured object identity, late collection mutations, certificates, and custom DI/lifecycle interactions remain unvalidated. |
| Native orchestration | The native-primitive and ATS scenarios use actual DCP allocation and workload controllers from the core. The explicit CLI override uses standard supervised launch and graceful shutdown; general CLI replacement and complete production execution semantics remain unimplemented. |
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
3. Promote the experimental native DCP executable/container path into a supported execution contract with production process containment and CLI integration. The primitive ports now exercise DCP allocation and readiness, but their prototype JSON client and shutdown policy are not that contract.
4. Extend lifecycle projections to owner invalidation, all network contexts, logs, and built-in commands. Add real deployment-target/native-build support using structured publisher values and dependency discovery; a facade or expression string alone is insufficient.
5. Measure the actual current-server baseline and native-only, mixed, and managed-heavy applications with equivalent capabilities, complete session accounting, and repeatable startup trials.

The existing spec's stronger statement that every C# integration host consumes a projected single ATS model is not achieved by retaining unchanged integrations against CLR owner islands. That distinction needs a deliberate design decision, not an assumption that moving DLL discovery solves it.
