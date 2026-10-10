# Aspire App Model v2: ATS-native integration authoring

> **Status:** Design exploration alongside `playground/NativeHosting`. This is not a supported API contract.
> **Audience:** Contributors exploring a minimal NativeAOT hosting core and integrations authored in C#, TypeScript, and other languages.
> **Baseline:** [Aspire Resource Model: Concepts, Design, and Authoring Guidance](appmodel.md).
> **Related:** [Polyglot integrations](polyglot-integrations.md#single-runtime-representation).

## Purpose

This document evolves with the native hosting proof of concept. Its purpose is to
give each integration-authoring pattern in `appmodel.md` an ATS-native equivalent,
not to reproduce every `Aspire.Hosting` type or method.

The proposed foundation is:

**The application model is one server-owned graph of typed entities, structured
values, and relationships. Integrations contribute schemas and behavior through
projections of core ATS APIs. CLR classes are an authoring convenience, not the
shared runtime representation.**

The core remains implemented in C#, but its contracts do not require loading
arbitrary integration assemblies or instantiating integration-defined CLR types.
C# and TypeScript integrations use the same identities and state. There is no
second CLR model to synchronize with an ATS model.

The desired guest experience remains ordinary application composition with
`Add`/`With` methods and build/run lifetime. Preserving useful guest APIs does not
require preserving the implementation-facing integration API one for one.

## Architecture and dependency boundary

```text
C# integration host                    TypeScript integration host
  generated core projection              generated core projection
  integration assemblies                 integration packages
  service clients, local DI              service clients, controllers
              |                                  |
              +----------- ATS contracts ---------+
                                   |
                         Native hosting core
                    entities, values, relationships
                    lifecycle, ownership, state
                    DCP execution and cleanup
                                   |
                       application workloads
```

An integration contributes projectable capability metadata and implementations.
The server routes exported calls to their owner. The implementation composes
resources by calling projected core APIs, rather than manipulating server objects.
Authors do not implement sockets, authentication, callback IDs, or RPC dispatch.

Managed dependencies, reflection-based discovery, dependency injection, and
service-specific clients may live in a C# integration host. npm dependencies may
live in a TypeScript host. They must not become native-core dependencies.
Metadata scanning and SDK generation can execute outside the native process.

Application containers and executables declared through the model are owned by
the core's DCP execution boundary. Integration-host workers belong to their
host's supervised process scope. These are different ownership domains.

## Resources and resource types

A resource is an inert, server-owned entity with:

- A unique application-model name and a generation-scoped identity.
- A declared ATS resource type and standard capability contracts.
- Typed configuration, annotations, outputs, and explicit relationships.
- An execution description or an integration-owned lifecycle provider.

Separate the **integration type** from the **execution kind**:

| Integration type | Execution shape |
|---|---|
| Redis | Container |
| PostgreSQL server | Container |
| PostgreSQL database | Logical child with initialization and readiness behavior |
| JavaScript application | Executable |
| Dev Tunnel port | Controller-backed resource with externally reported endpoints |
| Talking Clock | Controller-backed resource with no independent DCP workload |

Custom resource types remain supported as a design requirement. An integration
can declare fields, outputs, applicable capabilities, and exported methods.
Generated facades provide typed access without introducing a new class into the
native executable.

For example, the Redis type could describe:

```text
type: example.redis/Redis
execution: container
supports: endpoints, environment, arguments, connection-properties
fields:
  password: parameter-reference
outputs:
  host: string-expression
  port: integer-expression
  uri: secret-string-expression
  connectionString: secret-string-expression
```

This is an illustrative schema, not a selected schema syntax. Registration must
validate schemas and capability applicability. A caller-supplied type identifier
must not grant access to capabilities the actual entity does not support.

## Standard capabilities and polymorphism

The original guide's standard interfaces express important behavior. Preserve
that behavior as language-neutral capability contracts rather than relying on
CLR assignability.

| Existing interface | Proposed contract |
|---|---|
| `IResourceWithEnvironment` | Configure literal or structured environment values |
| `IResourceWithArgs` | Configure literal or structured arguments |
| `IResourceWithEndpoints` | Declare and reference named endpoints |
| `IResourceWithServiceDiscovery` | Contribute discoverable service endpoints |
| `IResourceWithConnectionString` | Expose connection properties and a default connection expression |
| `IResourceWithWaitSupport` | Declare explicit readiness dependencies |
| `IResourceWithoutLifetime` | Participate as model data without independent execution |
| `IResourceWithParent` | Declare lifecycle ownership separately from visual grouping |

Generated projections should expose methods only where applicable, and the
server must enforce applicability independently of client type checking.
Publishers and other integrations should work against these contracts without
special-casing every integration's concrete resource type.

## Add and With authoring patterns

`AddX` validates its inputs, creates entities, configures defaults, declares
outputs, and registers owned behavior. Composition must not start workloads,
resolve secrets, provision cloud resources, or allocate endpoints.

`WithX` contributes configuration, annotations, relationships, or behavior to an
existing entity. It normally preserves that entity's identity. Methods that
create child resources should make that distinction explicit.

Projections can offer fluent APIs and language-specific conveniences. Their
semantics come from the shared contract, not from C# overload resolution.

### C# integration example

The following is a **proposed projected API**, not a compilable example against
the current proof of concept. `BuilderHandle`, `ResourceHandle`, specification
types, and enum names are placeholders for the future core contract.
`RedisProbe` is integration-owned client code.

```csharp
public static class RedisIntegration
{
    [AspireExport]
    public static async Task<ResourceHandle> AddRedis(
        this BuilderHandle builder,
        string name,
        string image)
    {
        var password = await builder.AddParameter(
            $"{name}-password",
            new ParameterSpec { Secret = true, Generate = true });

        var redis = await builder.AddContainer(name, new ContainerSpec
        {
            Image = image,
            Command = "/bin/sh",
            Arguments =
            [
                "-c",
                "exec redis-server --requirepass \"$REDIS_PASSWORD\""
            ],
            Endpoints =
            [
                new EndpointSpec
                {
                    Name = "tcp",
                    Scheme = "redis",
                    TargetPort = 6379
                }
            ]
        });

        await redis.WithEnvironment("REDIS_PASSWORD", await password.Value());

        var uri = await builder.Concat(
            await builder.Literal("redis://:"),
            await password.Value(ValueEncoding.UriComponent),
            await builder.Literal("@"),
            await redis.Endpoint("tcp", EndpointProperty.Host),
            await builder.Literal(":"),
            await redis.Endpoint("tcp", EndpointProperty.Port));

        await redis.WithConnectionProperty("uri", uri);
        await redis.WithHealth(async (resource, cancellationToken) =>
        {
            var endpoint = await resource.ResolveConnectionProperty(
                "uri", cancellationToken);
            return await RedisProbe.PingAsync(endpoint, cancellationToken);
        });

        return redis;
    }
}
```

The returned resource must ultimately carry the integration's declared Redis
type, not just an unrestricted handle. Type attachment syntax is not selected.
The example focuses on primitive composition and a URI output; a complete Redis
integration also declares its default connection string for generic references.

### TypeScript integration example

This is the equivalent **proposed API**, not the current generated SDK.
`redisMetadata` represents build-produced export metadata. Full TypeScript
signature inference is not implemented by the proof of concept.

```typescript
import type {
    BuilderHandle,
    ResourceHandle,
} from './.aspire/modules/core.mjs';
import { AspireExport, defineIntegration } from './.aspire/modules/base.mjs';
import { redisMetadata } from './generated/exports.mjs';
import { pingRedis } from './redis-client.mjs';

export const addRedis = AspireExport(
    redisMetadata,
    async ({ builder, name, image }: {
        builder: BuilderHandle;
        name: string;
        image: string;
    }): Promise<ResourceHandle> => {
        const password = await builder.addParameter(
            `${name}-password`,
            { secret: true, generate: true });

        const redis = await builder.addContainer(name, {
            image,
            command: '/bin/sh',
            arguments: [
                '-c',
                'exec redis-server --requirepass "$REDIS_PASSWORD"',
            ],
            endpoints: [
                { name: 'tcp', scheme: 'redis', targetPort: 6379 },
            ],
        });

        await redis.withEnvironment(
            'REDIS_PASSWORD', await password.value());

        const uri = await builder.concat(
            await builder.literal('redis://:'),
            await password.value('uriComponent'),
            await builder.literal('@'),
            await redis.endpoint('tcp', 'host'),
            await builder.literal(':'),
            await redis.endpoint('tcp', 'port'));

        await redis.withConnectionProperty('uri', uri);
        await redis.withHealth(async (resource, cancellation) => {
            const endpoint = await resource.resolveConnectionProperty(
                'uri', cancellation);
            return pingRedis(endpoint, cancellation);
        });

        return redis;
    });

export default defineIntegration({
    name: 'Redis',
    capabilities: [addRedis],
});
```

Callbacks are normal author-language functions. The projection and host runtime
handle registration, invocation, cancellation, and disposal. An integration can
use local clients and caches, but shared model state belongs to the core.

## Annotations and extension data

Preserve annotations as an extensibility mechanism, but replace arbitrary live
objects with namespaced, schema-defined model data.

For example, `example.redis/persistence` could carry a duration and an integer
threshold. C# projects it as a record and TypeScript as an interface. Both access
the same server-owned payload.

A single JSON value per ID is not a complete replacement for the existing
annotation collection. The contract must distinguish:

- Singleton settings with explicit replacement semantics.
- Ordered, repeatable contributions with identity and removal semantics.
- Relationships and expression-valued fields.
- Behavior registrations, which carry owned callback references rather than
  serialized delegates.

An integration may retain opaque local objects in its own host. Those objects
are not portable annotations and cannot be inspected by another integration or
publisher. Portable data must use the shared contract.

## Structured values, references, and secrets

The core owns an expression graph, not just strings or JSON DTOs.

```text
Literal("postgresql://")
Parameter(password, encoding: uri-component)
Endpoint(postgres, "tcp", property: host)
Output(database, "databaseName")
Concat(...)
```

Expressions preserve output types, references, sensitivity, and evaluation
context. Runtime resolution accounts for the consumer's network context,
including host-to-container and container-to-container traffic. Publishing
translates the same expressions into target-specific representations without
resolving local endpoints or secrets.

Secret sensitivity must propagate through composed expressions. Logs, model
inspection, metadata, and generated artifacts must not accidentally disclose
resolved secret values.

Integration-defined value providers remain a design requirement. They need
explicit inputs and references, typed outputs, cancellation, ownership, and
publish semantics. An opaque callback returning a string does not provide enough
information for dependency analysis or portable publishing.

## Relationships and graph validation

Use distinct relationships rather than one overloaded parent/dependency field:

| Relationship | Meaning |
|---|---|
| Visual grouping | Organize presentation without controlling execution |
| Lifecycle ownership | Tie a child's lifetime to its owner |
| Readiness dependency | Wait for another resource to satisfy a declared condition |
| Value reference | Consume a parameter, endpoint, or output |

These relationships are authored explicitly, not inferred from resource names.
Validation must enforce the relevant rules for each relationship.

Endpoint references do not automatically imply readiness waits. Mutual
frontend/OIDC URL references must not become startup deadlocks. The graph must
distinguish endpoint allocation dependencies from execution/readiness dependencies.

## Lifecycle, health, and controllers

The core owns scheduling and lifecycle progression. Integrations register
bounded behavior through explicit resource-scoped contracts.

| Behavior | Example |
|---|---|
| Preparation | Produce configuration or files required for execution |
| Initialization | Create a PostgreSQL database after its server is ready |
| Pre-start setup | Configure a tunnel port using allocated endpoint information |
| Health probe | Test Redis connectivity without changing service state |
| Controller | Monitor an external resource and report state and endpoints |
| Disposal | Stop controllers and release integration-owned external state |

The stage names are provisional. The contract must define ordering, concurrency,
timeouts, cancellation, error propagation, and which operations are legal in
each phase. Multiple registrations must have explicit composition semantics.
Independent resources should not be serialized behind a global integration queue.

Readiness is derived from execution state and configured probes. A failed probe
is distinct from an invocation failure or a disconnected provider.
Controller-backed resources report observations through a validated ownership
contract; they do not arbitrarily publish core lifecycle events.

Initialization callbacks complete. Background controllers have a separate,
explicit lifetime. Service clients and controller queues remain in integration
hosts, not resource constructors or core services.

## Resource state, logs, and commands

Projected APIs provide resource-scoped logs, structured snapshots, endpoint
updates, and command registration. The core owns validation and delivery.
Human-readable diagnostics and machine-readable state remain separate.

Writes are fenced by resource identity, graph generation, and controller
ownership. Late updates from a retired owner must be rejected even when a new
resource uses the same name.

Commands need typed inputs/results, cancellation, and provider-side concurrency
control. A disabled UI command is not a concurrency guarantee.

Dashboard presentation metadata is part of the design, but the current native
proof of concept does not implement the Dashboard protocol.

## Custom-resource example: Talking Clock

The original guide's Talking Clock is the reference example for a resource with
no container or executable of its own. Its v2 equivalent would:

1. Declare a clock resource and two child entities with explicit relationships.
2. Register a controller owned by the integration host.
3. Start its ticking loop when the run lifecycle activates the controller.
4. Publish tick/tock snapshots and resource logs through projected APIs.
5. Cancel and await the loop during graph disposal.

The core knows ownership, state, and relationships, not how a clock ticks.
This is the same extensibility category as a Dev Tunnel port whose endpoint and
health are supplied by an external controller.

## Publishing and deployment

Do not remote the existing `ManifestPublishingContext.Writer` operation by
operation. A publisher consumes a typed model view and contributes structured
artifacts or a deployment plan. Expressions and dependencies stay structured
until the target translates them.

Integrations may contribute target-specific metadata, preparation, provisioning,
and publishing capabilities outside the core. Run-only resources must be
explicitly excluded from deployment output.

Model views should support generic inspection by capability as well as
integration-specific schema inspection. Artifact paths, ownership, diagnostics,
and cancellation require explicit contracts.

The native proof of concept has symbolic model output, not a complete
publishing/deployment implementation. Publishing requirements must nevertheless
guide the model so local execution does not discard information needed later.

## Integration lifetime and guest reload

Model generation, guest process lifetime, and integration-host process lifetime
are separate scopes.

The first reload policy is full graph replacement:

1. Fence new mutations from the retiring guest.
2. Stop and await its controllers and owned behavior.
3. Cancel outstanding work and revoke generation-scoped handles.
4. Clean up workloads and integration-owned session state.
5. Allow the next guest to build a new graph.

Integration hosts can remain alive between generations. Their graph-scoped
clients, callbacks, and controller state cannot remain active accidentally.
Host loss must invalidate its behavior and surface a failure rather than leave
resources reporting healthy indefinitely.

Retaining and reconciling workloads across reload is a separate future policy.
It needs stable identities, desired-state comparison, and controller
reconciliation semantics; it is not implied by keeping the host process alive.

## Comparison with the original guide

| Original authoring pattern | V2 direction |
|---|---|
| Data-only resource classes | Typed server-owned entities and projected facades |
| CLR inheritance and standard interfaces | Execution kinds and capability contracts |
| `IResourceBuilder<T>` | Typed entity configuration APIs with preserved identity |
| Arbitrary `IResourceAnnotation` objects | Schema-defined data and explicit contribution semantics |
| `IValueProvider` / `IManifestExpressionProvider` | Structured expressions and owned provider contracts |
| `builder.Services` | Integration-host-local services; projected core behavior registration |
| Application event subscriptions | Resource-scoped lifecycle contracts with defined scheduling |
| .NET health-check registration | Integration-owned probes scheduled by the core |
| Notification and logger services | Owned, generation-fenced state/log APIs |
| Custom resource loops | Explicit integration-host controllers |
| Manifest writer callbacks | Typed model views and structured publishing contributions |
| Live object references | Validated generation-scoped entity and callback references |

The intended invariants survive: inert composition, deferred values, explicit
relationships, polymorphic integration, readiness distinct from running, and
separate run/publish behavior. Their CLR implementation mechanisms need not.

## Proof-of-concept evidence and gaps

See [the native hosting playground](../../playground/NativeHosting/README.md)
for executable samples and reproduction commands.

| Area | Current evidence | Still required |
|---|---|---|
| Native core | NativeAOT primitive graph, static dispatch, BCL DCP access | Generalized ATS model contracts |
| TypeScript authoring | Generated primitive APIs, external Redis/PostgreSQL/Dev Tunnels ports | Stable authoring surface and metadata tooling |
| C# authoring | Offline C# declarations drive ATS scanning | Generated core projection and external C# execution host |
| Resources | Fixed primitive handles and bounded production-type views | Integration-defined schemas and validated capability catalogs |
| Values | Deferred parameters, endpoint properties, concatenation | Typed extensible providers and complete sensitivity semantics |
| Lifecycle | Health/setup callbacks and owned custom-resource controller updates | General registration composition and recovery policy |
| Reload | Explicit guest replacement with stale-handle rejection | CLI watch integration and optional warm reconciliation |
| Publishing | Symbolic primitive model output | Publisher contracts and deployment targets |

The production-signature compatibility adapter is an experiment, not the
foundation of this design. It must not determine what integrations are allowed
to express or require the native core to mirror all managed Hosting APIs.

## Expanding the proof of concept

Use the original guide's patterns as acceptance cases:

1. Typed Redis composition with portable annotations and connection outputs.
2. PostgreSQL parent/child resources with initialization and readiness ordering.
3. Dev Tunnels or Talking Clock with owned controllers, logs, commands, and fenced updates.
4. The same core contracts consumed by an external C# integration host.
5. A publisher consuming expressions and extension data without integration CLR objects.

For each expansion, update this document with the selected contract, executable
evidence, and remaining limitations. Keep proposed samples labeled until they
can run against generated projections.

Measure the actual native binary size, runtime dependency graph, and repeated
post-readiness core working-set samples. Report integration-host and codegen
costs separately rather than presenting core-only measurements as whole-session
costs.
