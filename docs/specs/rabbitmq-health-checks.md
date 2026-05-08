# RabbitMQ child-resource health check design

This document captures the design intent and key decisions for how health checks work across
`Aspire.Hosting.RabbitMQ` child resources. It is aimed at contributors extending the integration.

For the user-facing contract see the [README](../../src/Aspire.Hosting.RabbitMQ/README.md#health-checks).

## Guiding principle

A child resource is `Healthy` iff it has been provisioned **exactly as declared** in the AppHost,
including every cross-cutting configuration that affects its runtime behaviour, and a live probe
confirms it still exists on the broker.

"Exactly as declared" is the key phrase. A queue with a TTL policy that failed to apply is not
what the user declared — it must be `Unhealthy` so that `WaitFor(queue)` blocks dependents.

## Design decisions

### Two signals per resource: lifecycle and health

Every RabbitMQ child resource participates in two independent signaling channels:

**Lifecycle signal** (Aspire resource state — `Starting` / `Running` / `FailedToStart`):
Reflects whether the provisioning attempt has been made and what its outcome was.
The resource transitions to `Running` once the broker call completes successfully.
If the broker call fails, the resource transitions to `FailedToStart`.
Resources that have not yet been reached by the provisioner remain in `Starting`.

**Health signal** (`ProvisionedTask` — a read-only `Task`):
A gate the health check awaits. Pending means provisioning has not completed yet (health check
returns `Degraded`). Completed means provisioning succeeded and the live probe can run.
Faulted means provisioning failed (health check returns `Unhealthy`).

These two channels are intentionally separate: lifecycle state is visible in the Aspire dashboard
resource list; health state is visible in the health check panel and gates `WaitFor` dependents.

### Resource owns both signals

Each resource owns its provisioning signal (`ProvisionedTask`) and is responsible for publishing
its own lifecycle state transitions. The provisioner calls `ApplyAsync` (and for exchanges,
`ApplyBindingsAsync`) and the resource handles everything else internally.

The `TaskCompletionSource` is `private readonly` inside each resource. The interface exposes only
the read side: `Task ProvisionedTask { get; }`. This prevents the provisioner from signaling
arbitrary states independently of the actual broker call result.

`ApplyAsync` receives a `ResourceNotificationService` parameter so the resource can publish
`Starting` at entry and `Running` or `FailedToStart` at exit without any external coordination.

### Per-resource provisioning signal

Each resource owns a `Task ProvisionedTask` that is completed (or faulted) when its own
provisioning step finishes. This isolates failures: if one queue fails to declare, only that
queue's health check reports `Unhealthy`. Sibling queues, exchanges, and shovels are unaffected.

When a vhost fails to create, the provisioner returns early and child resources are never reached.
Children remain in `Starting` with `ProvisionedTask` still pending — the health check returns
`Degraded` ("provisioning in progress"), which is semantically correct: provisioning never ran.
There is no cascade-fault of children; `FailedToStart` is reserved for resources whose own
provisioning attempt was made and failed.

### Lifecycle and health state matrix

| Situation | Lifecycle state | Health check result |
|---|---|---|
| Provisioner has not reached this resource yet | `Starting` | `Degraded` |
| Provisioning succeeded | `Running` | `Degraded` → `Healthy` after live probe |
| Provisioning failed | `FailedToStart` | `Unhealthy` |
| Exchange declared; bindings in progress | `Running` | `Degraded` |
| Exchange declared; bindings failed | `Running` | `Unhealthy` |

### Exchange is a special case: two-phase provisioning

Exchanges are declared in phase 2 and have their bindings applied in phase 3. The exchange
transitions to `Running` after successful declaration (it is live on the broker at that point).
`ProvisionedTask` is not completed until bindings also succeed. If bindings fail, the exchange
stays `Running` but `ProvisionedTask` faults, so the health check reports `Unhealthy`.

If declaration itself fails, the exchange transitions to `FailedToStart` and `ProvisionedTask`
faults immediately. The provisioner skips phase 3 for that exchange by checking
`exchange.ProvisionedTask.IsFaulted`.

### Two-stage health check: provisioning signal + live probe

The provisioning signal proves "we sent the declare and the broker accepted it." The live probe
proves "the entity still exists" — catching out-of-band deletion by an operator. Both stages are
required for correctness.

### Resource owns its own health semantics

Each resource type knows how to verify itself (existence check, connection check, state check).
This keeps health-check registration in the builder extensions trivial and uniform — every `Add*`
call site uses the same one-liner helper with no per-resource parameters.

### Probe result type is separate from `HealthCheckResult`

Resource classes return a lightweight domain type rather than `HealthCheckResult` directly. This
keeps `Microsoft.Extensions.Diagnostics.HealthChecks` out of the resource model layer, which is
important for testability and layering.

### Binding failures are attributed to the source exchange only

Bindings are declared on the exchange; routing is the exchange's responsibility. The destination
queue's own behaviour is unaffected by a missing binding. Propagating to the destination would
fan-in failures from many exchanges onto one queue and obscure the root cause.

### Shovel failures are isolated to the shovel resource

Shovels move messages between otherwise-independent endpoints. If a shovel fails, the source queue
still exists and is correctly configured. The shovel's live-state probe naturally catches downstream
breakage without needing to cascade to source or destination.

### Policy failures cascade to matching queues/exchanges

Unlike bindings, a policy changes the behaviour of the entity itself (TTL, max-length, DLX, HA).
A queue without its declared TTL policy will silently retain messages forever — a correctness bug
the user cannot observe from "queue exists = true". Therefore a policy failure marks every
queue/exchange whose name matches the policy pattern as `Unhealthy`.

Policy-to-entity matching is resolved once after the model is fully built (not at `AddPolicy` call
time, to avoid order-dependency) and cached on each entity via `AppliedPolicies`. The same
resolution pass adds a dashboard relationship edge so the cascade is visible without reading logs.
This behaviour is implemented and covered by tests.

## Extension guidance

When adding a new provisionable resource type:

- Keep the `TaskCompletionSource` `private readonly`; expose only `Task ProvisionedTask { get; }`.
- In `ApplyAsync`, publish `Starting` at entry, then do the broker work.
  On success: complete the TCS and publish `Running`.
  On failure: fault the TCS and publish `FailedToStart`.
- Implement a live probe appropriate to the entity type (existence, state, connectivity).
- Declare health dependencies if the resource's correctness depends on other provisionables
  (e.g. policies applied to it).
- Register the health check using the shared helper — no bespoke registration logic.
- Add the resource to the appropriate provisioner phase; capture failures per-entity without
  short-circuiting siblings. The provisioner must not touch the TCS directly.
