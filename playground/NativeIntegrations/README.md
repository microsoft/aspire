# Native integration providers

These nonshipping examples demonstrate **integration implementation** in C# and
TypeScript against the native server. Each external-API integration retains its
own exported `AddExternalApi`, `WithApiKey`, and resource identity API. Its
implementation uses generated native composition methods, not managed
`Aspire.Hosting`, capability-ID strings, invitations, or a test worker.

`AddExternalApi` declares an external HTTP(S) service; it does not provision that
service or perform an HTTP health check. `WithApiKey` stores a symbolic secret
reference without reading the credential in either integration language.

## Product opt-in

AppHost Server v2 ships as an additive bundle component. Existing AppHosts keep
using the managed server unless their target `aspire.config.json` explicitly
sets `"features": { "appHostServerV2": true }`. Global configuration does not
select v2. The initial CLI boundary supports TypeScript/Node.js on Unix and Windows,
core resources and qualified protocol-v2 npm providers. The C# examples below
remain standalone provider/contract examples, not compatible managed NuGet
integrations for the product path.

The C# Redis provider uses StackExchange.Redis for health checks, SAVE and
consumer commands. Integration implementations are ordinary managed code and
may use service-client libraries; only the server core must remain AOT-friendly.

A qualified npm entry point has an adjacent `aspire.apphost-server.json`:

```json
{
  "protocolVersion": 2,
  "packageName": "example.ts.redis",
  "metadata": "provider-contract.json",
  "sdkFiles": {
    "redis-builder.mts": "redis-builder.mts",
    "provider-client.mts": "provider-client.mts"
  },
  "exports": ["redis-builder.mts"]
}
```

Declare every authoring/runtime source needed by the exported modules in
`sdkFiles`; the abbreviated example above is not the complete Redis payload.
Assets must exist inside the entry point's package directory. The CLI prepares
their SDK using the matching bundle's offline ATS contract and shared generator,
then supplies activation coordinates over authenticated RPC. Ordinary AppHosts
import `createBuilder` and the selected integration APIs from the generated
`aspire.mjs`; they do not pass provider commands or set server overrides.

For local server development, `ASPIRE_CLI_NATIVE_APPHOST_SERVER` overrides only
the executable and still requires project opt-in. The CLI retains any available
bundle layout for managed provider-codegen tooling, DCP and Dashboard. Put the
matching `ats-contract.json` beside the overridden executable; provider-backed
AppHosts require both that contract and the bundle's managed tooling.

## Source layout

- `CSharp/ExternalApi`: ordinary resource class and `[AspireExport]` methods.
- `CSharp/Contracts`: offline provider dispatcher, metadata, and consumer
  projection generation. Generated files remain under `artifacts/obj`.
- `CSharp/Provider`: framework provider entry point.
- `CSharp/AppHost`: typed consumer with explicit framework provider activation.
- `TypeScript/external-api.mts`: `AspireExport`/`defineIntegration` implementation.
- `TypeScript/provider.mts`: framework provider entry point and offline metadata
  emission.
- `TypeScript/apphost.mts`: consumer of the offline-generated provider client.
- `CSharp/DockerCompose`: independent C# implementation of the v2 Compose target,
  with native graph export, typed customization and native-scheduled deployment steps.
- `CSharp/DockerComposeAppHost`: standalone C# publish/deploy comparison AppHost,
  sharing the TypeScript Compose sample's actual web application.
- `TypeScript/DockerCompose`: TypeScript version of the same target. See the
  [side-by-side comparison](CSharp/DockerCompose/README.md#side-by-side-implementation).

## Validation

From the repository root:

```bash
./restore.sh
dotnet build playground/NativeIntegrations/CSharp/Provider/NativeIntegrations.Provider.csproj
dotnet build playground/NativeIntegrations/CSharp/AppHost/NativeIntegrations.AppHost.Sample.csproj
dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj \
  --no-launch-profile -- --filter-class '*.NativeProviderClientTests' \
  --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
```

Use the local `.dotnet` SDK/runtime as described in the repository instructions.
`eng/scripts/test-native-hosting.sh` additionally publishes the actual AOT server,
builds these providers and AppHosts, emits TS metadata, generates the TS consumer
projection, and executes both genuine AppHosts against authenticated native
sockets. That full lane requires Docker, DCP, and Node.js 24 or later.

For a separately running native server, the C# sample accepts a loopback endpoint
and the built provider executable path:

```bash
export ASPIRE_NATIVE_RPC_TOKEN='<native server authentication token>'
dotnet run --project playground/NativeIntegrations/CSharp/AppHost/NativeIntegrations.AppHost.Sample.csproj \
  -- 127.0.0.1:43210 "$PWD/artifacts/bin/NativeIntegrations.Provider/Debug/net11.0/NativeIntegrations.Provider"
```

Registration is explicit and one-use. The shared framework activates a selected
provider, discovers its offline metadata, and routes calls through bidirectional
RPC. Native core handles remain delegated to their original connection; a
provider-owned resource must declare an authorized native lifetime. Disconnects
fail pending calls without retries and do not retire the application's model.
Failed activation immediately abandons its pending registration authority,
including process-launch failures; late discovery cannot register afterward.
Abandonment has an independent configurable deadline, so a stalled RPC peer
cannot prevent owned-process shutdown. C# uses `NativeProviderActivationOptions`
(also exposed through builder options); TypeScript connection options expose
`providerCleanupTimeoutMilliseconds` and `providerShutdownTimeoutMilliseconds`.
Disconnect removes the provider's discovery and type registrations. A replacement
can register the same contract, but existing handles remain revoked and cannot
be rebound to the replacement process.
Portable provider metadata is available through `getIntegrationCapabilities`;
the static `getCapabilities` contract remains separate from integration APIs.

TypeScript provider implementations can obtain host cancellation with
`getIntegrationInvocationSignal(args)` from `integration-host.mjs`, passing the
original capability argument bag before destructuring it. This works for managed
and native hosts without reserving an argument name such as `signal`. Projected
`cancellationToken` parameters continue to receive SDK cancellation tokens.
Retiring a provider-owned resource cancels and drains all invocations using it,
including handles nested inside argument arrays and objects, before disposal.
On disconnect or SIGINT/SIGTERM, process cleanup has a five-second deadline;
failed or timed-out cleanup emits a diagnostic and exits with a nonzero code.

## Deliberate boundaries

This phase supports required primitive arguments/results, typed handles, and
projected callbacks with nonvoid results. DTO/union/optional provider shapes and
void callback writeback are explicitly rejected.
The native core adds no Redis-specific APIs; this does not prove existing
integration compatibility.

Generic declarative workload composition now resides on the shared native
surface: `AddContainer`/`AddExecutable`, `WithEnvironment`, `WithArgument`,
`WithEndpoint`, `WithReference`, entrypoints, and retained named volumes.
Ordinary AppHosts declare the graph, then `StartApplication` activates native
workloads. `WaitApplicationReady` observes native dependency and protocol-health
ordering; no resource-owner invitations are needed. Both generated languages project these same native contracts.

## Docker Compose publishing and deployment

[`TypeScript/DockerCompose`](TypeScript/DockerCompose/README.md) is a separate
nonshipping deployment integration for the opt-in TypeScript CLI path. It
contributes portable authoring APIs with an empty tooling capability list; it
does not launch a runtime provider or add Compose behavior to the native kernel.

An AppHost uses `builder.addDockerComposeEnvironment('compose')`, assigns
workloads with `withComputeEnvironment` and customizes typed Compose services
with `publishAsDockerComposeService` callbacks. The sample
combines an authenticated, volume-backed Redis image and a buildable Node HTTP
application using the same native graph, symbolic credential and endpoint
references.

`aspire publish` composes without local execution and writes secret-free Compose
artifacts. `aspire deploy` resolves explicit `ASPIRE_PARAMETER_*` target inputs,
builds and applies through Docker Compose, waits for health and records verified
service URLs. `--list-steps --format json` inspects the pipeline without running
target operations. Named volumes and deployed services outlive the guest/server;
cleanup is external and there is no automatic rollback.

`builder.pipeline()` and resource-scoped pipeline factories/configuration use
the same named-step, dependency, `requiredBy`, tag and reporting concepts as
today's TypeScript AppHosts. The native server owns DAG validation, target
selection and ready-step scheduling; callbacks remain in the authoring guest.
Inspection does not execute them. Local startup runs the `before-start` closure.
Parameters are configuration-backed and nonsecret by default; generated
credentials require an explicit generation API.

The native validation harness prepares an isolated AppHost/package and real
bundle layout, then exercises the actual CLI and AOT server through publishing,
missing-input failure, deployment, container replacement with retained Redis
data, repeated deployment and owned-resource cleanup. It also compiles the
unchanged existing Compose TypeScript fixture, inspects and executes custom
targets with concurrent branches, checks failure propagation, isolates multiple
environments and proves `before-start` actions precede local execution.
The same sample also runs through foreground `aspire run`, targeted stop and
detached `aspire start`, proving HTTP readiness, clean guest exit and authenticated
Redis persistence across restart with isolated ports and an owned data volume.
Docker Compose v2 is
required. See the integration README for commands, artifacts and unsupported
resource/lifecycle shapes. This does not make the run-only Redis provider below
publishable or establish managed Compose parity.

## Real Redis integration

`CSharp/Redis/RedisBuilderExtensions.cs` and
`TypeScript/Redis/redis-authoring.mts` implement run-only Redis using ordinary
generic builder composition: `AddContainer`, environment/argument/endpoint
configuration, symbolic connection expressions, and executable health checks.
Inert typed resource data is separate from the provider's runtime callback
object. This is integration-author code, not generated RPC calls with new names.

Ordinary C# composition is synchronous:

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var cache = builder.AddRedis("cache").WithDataVolume();
builder.AddExecutable("consumer", executablePath, workingDirectory)
    .WithReference(cache)
    .WaitFor(cache);
await builder.Build().RunAsync();
```

The equivalent TypeScript integration contributes a fluent builder method:

```typescript
const builder = await createBuilder();
const cache = await builder.addRedis('cache').withDataVolume();
await builder.addExecutable('consumer', executablePath, workingDirectory)
    .withReference(cache)
    .waitFor(cache);
await builder.build().run();
```

Composition and `Build` only record/seal a typed transport plan; they never
connect, block on RPC, resolve secrets, or create a language-local runtime graph.
Async execution materializes declarations and configuration through the
generated ATS projections into a staged native revision. The shared framework
owns authenticated transport, selected provider activation, workspace commits,
readiness, and retirement. Provider-side generated adapters materialize the
integration's same generic builder implementation under delegated native
authority; portable lifecycle callbacks remain out-of-process.

`WithReference` is consumer-side environment injection; `WaitFor` is a separate
explicit dependency operation. Native value references already contribute their
own readiness dependencies, so these are distinct authoring concepts, not a
claim that connection injection has no native scheduling dependency.

`AddRedis` defaults to a cryptographically generated **native-owned** secret
parameter, never an empty password. An explicit `AddParameter` can supply a
credential, including a changed value in a staged revision. Credentials are not
returned to the integration author. Redis receives its password through quoted
environment expansion, and an integration-owned protocol executable performs
real `AUTH` and `PING` before dependents can start. No raw socket readiness
substitute is used.

The integration enables AOF with `appendfsync always`; data volumes mount at
`/data` and survive application retirement. Ordinary AppHosts use a generated
credential and a real protocol consumer. Separate
`CSharp/RedisValidation` and `TypeScript/Redis/redis-validation.mts` scenarios use
the shared application `UpdateAsync`/`update` API to rotate an explicit password,
replace the container and affected consumer, and read retained AOF data. The
native workspace—not the recorder—owns revision identity and retention decisions.
The C# validation entry point keeps ordinary fluent composition; its separate
`RedisPersistenceValidation` helper owns the rejected-revision and persistence checks.
Validation removes only its uniquely named volume after retirement.

```bash
dotnet build playground/NativeIntegrations/CSharp/RedisProvider/NativeIntegrations.Redis.Provider.csproj
dotnet build playground/NativeIntegrations/CSharp/RedisAppHost/NativeIntegrations.Redis.AppHost.Sample.csproj
dotnet build playground/NativeIntegrations/CSharp/RedisValidation/NativeIntegrations.Redis.Validation.csproj
```

For the C# ordinary sample, framework configuration can select a separately
running server/provider and finite consumer without adding transport code:

```bash
export ASPIRE_NATIVE_RPC_TOKEN='<native server authentication token>'
dotnet run --project playground/NativeIntegrations/CSharp/RedisAppHost/NativeIntegrations.Redis.AppHost.Sample.csproj -- \
  --native-endpoint=127.0.0.1:43210 \
  --native-provider=example.csharp.redis="$PWD/artifacts/bin/NativeIntegrations.Redis.Provider/Debug/net11.0/NativeIntegrations.Redis.Provider" \
  --consumer-path="$PWD/artifacts/bin/NativeIntegrations.Redis.Provider/Debug/net11.0/NativeIntegrations.Redis.Provider" \
  --native-exit-on-completion=consumer
```

Provider selection is framework configuration, not application registration.
Without `--native-exit-on-completion`, `RunAsync`/`run` waits for shutdown and
retires before closing its provider. Default retained volumes are
`aspire-<application-name>-<root/name-hash>-cache-data`, not a global `aspire`
namespace. The stable application root defaults to the launch working directory;
launch from the AppHost source root, or set `--native-application-root` when
launching from elsewhere. It must not point at a rebuild-specific output folder.
The application name defaults to that root's directory name; use
`--native-application-name` to distinguish AppHosts sharing a root. C#
`NativeBuilderOptions` and TypeScript `createBuilder(args, options)` also accept
these root/name settings. Framework flags override the corresponding options.

Both languages resolve the root to an absolute, dot-segment-free path, remove
non-root trailing separators, use `/` separators, and ASCII-fold root/name case
on Windows only. UTF-8 bytes of `root + NUL + name` are hashed with nonsecurity
FNV-1a/64 (offset `14695981039346656037`, prime `1099511628211`, wrapping at
64 bits), rendered as 16 lowercase hexadecimal digits. The display name uses
its first 40 UTF-16 code units, ASCII-lowercased and with characters outside
`[a-z0-9_.-]` replaced by `_`. This preserves namespaces across rebuilds,
reruns and native revisions, while separating different source roots even when
their directory/resource names match. Moving the source root changes its
namespace; `--native-volume-prefix` preserves an exact explicit prefix, including
for reusing an existing retained volume. No existing volume is renamed or deleted.
To intentionally reuse the previous global default, select
`--native-volume-prefix=aspire`.

C# framework time/log services can be injected with `NativeBuilderOptions`.
TypeScript provider configuration uses
`--native-provider=example.ts.redis=<node>|<provider.mjs>`; quote the whole flag
so `|` is not interpreted by the shell.

The dedicated native harness also generates the TS Redis provider/consumer,
executes both AppHosts against the actual AOT server and DCP, and validates
authenticated readiness, persistence, revisions, provider retirement callbacks,
and workload cleanup.

`WithLifecycleExecutable` declares provider-owned one-shot `initialize`,
`health`, `ready`, and `retire` commands with server-resolved symbolic inputs.
Health commands may retry within the startup deadline; non-health side effects
are not retried. Retirement executes before workload deletion, retains failed
cleanup obligations, and never sends resolved inputs or command output back to
the integration authoring process.

The Redis resource also exports integration-owned ATS lifecycle handlers.
C# uses `[NativeResourceLifecycle("ready")]` alongside `[AspireExport]`; TS uses
the same portable `lifecyclePhase` capability metadata. Returning a resource
automatically binds its validated handlers to its original native declaration.
Ordinary AppHosts do not construct bindings, scope handles, or callback requests.
Handlers receive a generated `ResourceLifecycleScope` and cancellation
(`CancellationToken` in C#, an `AbortSignal` in TS), not resolved secrets.
Initialization, allocation, readiness, and retirement are native-ordered,
bounded, and never replayed. The retirement handler requests actual
authenticated Redis `SAVE` using native-captured input slots before the
container is deleted. Validation checks its resulting `dump.rdb`.

Scope authority permits resource logs and bounded one-shot native executable
commands only; there is no credential/reference read API or general declaration
authority. Public handles are revoked before retirement; the cleanup scope
expires after the phase and drains its native executions. Provider-local
objects are released deterministically after generation replacement, resource
removal, aborted authoring, or disconnect. Retained physical workloads adopt
the next authoring object's handlers without restarting or replaying
initialization. C# `DisposeAsync` and TS `dispose` release local state once.

The 79-export builder examples also passed against the independent Rust runtime.
Both ordinary C#/TS AppHosts exercised native-generated default credentials,
authenticated readiness, references, retirement SAVE and owned-volume cleanup.
Both separate validators exercised rejected revisions, password rotation and
retained data. Rust source is not included in this change. In-process .NET tests
are not Rust execution evidence.

Ongoing health monitoring, container-network references,
TLS, Redis modules/admin companions, provider-catalog discovery, dynamic provider
addition, and publish/deploy remain outside this
bounded run-only sample. Unsupported container-context endpoint references fail
explicitly instead of using host loopback. No existing Redis parity is claimed.
`--publish=true` fails explicitly; no TLS authoring operation is exposed.
Default generated credentials are new native declarations per materialized
revision; use an explicit parameter when controlling retention/rotation.
Backing-declaration retirement immediately revokes native provider handles.
Prestart allocated-endpoint barriers, external endpoint publication/revocation,
provider notification/login selection, and remote-object retention policies
needed by other integration archetypes are not covered by this Redis example.
