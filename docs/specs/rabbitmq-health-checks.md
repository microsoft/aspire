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

### Per-resource provisioning signal

Each resource owns a `TaskCompletionSource` that is completed (or faulted) when its own provisioning
step finishes. This isolates failures: if one queue fails to declare, only that queue's health check
reports `Unhealthy`. Sibling queues, exchanges, and shovels are unaffected.

The only legitimate cascade is a vhost-creation failure, which faults every child in that vhost
because nothing can exist without the vhost.

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

### Policy failures cascade to matching queues/exchanges (planned)

Unlike bindings, a policy changes the behaviour of the entity itself (TTL, max-length, DLX, HA).
A queue without its declared TTL policy will silently retain messages forever — a correctness bug
the user cannot observe from "queue exists = true". Therefore a policy failure marks every
queue/exchange whose name matches the policy pattern as `Unhealthy`.

Policy-to-entity matching is resolved once after the model is fully built (not at `AddPolicy` call
time, to avoid order-dependency) and cached on each entity. The same resolution pass adds a
dashboard relationship edge so the cascade is visible without reading logs.

## Extension guidance

When adding a new provisionable resource type:

- Give it its own provisioning signal (TCS), completed or faulted by the provisioner.
- Implement a live probe appropriate to the entity type (existence, state, connectivity).
- Declare health dependencies if the resource's correctness depends on other provisionables
  (e.g. policies applied to it).
- Register the health check using the shared helper — no bespoke registration logic.
- Add the resource to the appropriate provisioner phase; capture failures per-entity without
  short-circuiting siblings.
