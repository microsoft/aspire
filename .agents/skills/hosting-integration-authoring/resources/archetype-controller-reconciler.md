# Archetype: controller/reconciler integration

Use this archetype when an integration must coordinate lifecycle events, dashboard commands, background probes, and per-resource operations against shared external state. The goal is resilient, reentrant orchestration rather than one-off event handlers that race each other.

Representative examples:

- A run-mode provisioning controller that serializes provision, reprovision, reset, delete, cancel, and drift-check operations.
- A deployment target controller that reconciles desired app-model resources with live platform state.
- A local infrastructure operator that reacts to commands and resource state changes while keeping dashboard command state accurate.
- A stateless lifecycle orchestrator that centralizes per-resource start/ready/stopped logic without serializing unrelated resources.

## Shape

Controller/reconciler logic should live in a singleton service. Resources stay app-model data and dashboard surfaces.

DO:

- Put orchestration state and operation dispatch in a singleton controller service.
- Keep resource classes inert; use resources for identity, relationships, references, initial state, and command annotations.
- Expose an environment/control resource when users need aggregate status and environment-level commands.
- Attach per-resource commands from a preparer/final-model hook when commands depend on the final resource set.
- Represent each operation as a typed intent with an explicit target scope, such as all resources, one resource, a resource set, or background/no user-visible scope.
- Mode-gate the controller. If the controller is run-mode only, exclude or hide its environment/control resources in publish output.

DON'T:

- Don't put long-running orchestration directly in resource constructors or random fluent methods.
- Don't let every command, event handler, and background probe mutate shared state independently.
- Don't use resource names or call order as implicit operation dependencies.

## Pick the lightest controller shape

Centralizing lifecycle logic in a controller service does not always mean adding a queue.

DO:

- Use a stateless lifecycle orchestrator when operations are per-resource, independent, and do not mutate shared controller state. Invoke its methods directly from resource lifecycle callbacks so Aspire's built-in per-resource concurrency is preserved.
- Keep shared external interactions concurrency-safe without a controller queue when a narrower primitive already exists, such as a coalesced login manager or per-client retry helper.
- Escalate to a serialized queue/reconciler only when operations contend on shared mutable state, dashboard command state, cancel/delete/reset workflows, drift probes, or an environment-level operation model.
- Scope serialization to the smallest conflict domain. Use one global queue only when a single intent owns the whole resource set and fans out internally; use keyed/per-resource serialization for independent per-resource lifecycle callbacks.

DON'T:

- Don't introduce a serialized control loop just to move code out of fluent extension methods.
- Don't funnel independent per-resource lifecycle callbacks through one global single-reader queue; it serializes unrelated resources, increases startup latency, and adds shutdown-loop failure modes.
- Don't make unrelated resources wait behind a long-running operation unless they really share the same external state or invariant.

## Serialized control loop

Use a serialized control loop only after the decision gate above says one is needed. A queued reconciler should have one synchronization boundary for the state it owns.

DO:

- Funnel public methods, dashboard commands, lifecycle callbacks, CLI/MCP commands, and background probes through the same serialized queue.
- Use a single-reader queue/channel when operations can be requested concurrently.
- Create each queued item with a typed intent, caller cancellation token, and `TaskCompletionSource` using `RunContinuationsAsynchronously`.
- Register accepted queued operations before writing to the queue so rapid callers cannot enqueue conflicting operations before command states refresh.
- Keep the locked state small: current intent, active operation snapshot, queued operation scopes, and coalescing flags.
- Never `await` while holding the controller state lock.
- Start background loops lazily and idempotently with `Interlocked` or an equivalent guard.
- Tie reader-loop lifetime to explicit channel completion/disposal, not only to host shutdown, when the controller must process shutdown-time intents such as stopped/finished transitions.
- Fence writers after the loop exits. Enqueue-and-await must fail fast rather than waiting on a `TaskCompletionSource` that no reader can complete.
- Complete every queued operation exactly once: success, failure, or cancellation.
- Re-enable command state in `finally`, using a non-cancelable token when needed so canceled requests do not leave commands disabled.

DON'T:

- Don't execute mutating command bodies inline when they can re-enter the same state from another path.
- Don't rely only on dashboard disabled-state to prevent concurrency; enforce conflicts in the controller.
- Don't let operation continuations run inline while holding queue or state machinery.
- Don't await a queued operation from code already running inside the single reader loop, including event handlers published by that operation; the loop cannot service the nested operation until the current one returns.
- Don't leave a latched "loop started" flag set after loop failure or completion unless writers are also closed/fenced.

## Command state and dashboard integration

Command state should reflect controller state, not independently recomputed ad hoc checks.

DO:

- Define command metadata centrally: name, display name, description, confirmation, icon, highlighted state, arguments, validation, execute callback.
- Use `UpdateState` callbacks that ask the controller whether a command is enabled, disabled, or hidden.
- Disable commands affected by the active or queued operation; keep unaffected resource commands enabled when safe.
- Keep read-only diagnostic commands enabled during operations when they help users recover.
- Make cancel a special command: enable it only for affected resources in cancelable states and disable it once cancellation has been requested.
- Publish no-op resource updates to refresh command state before and after operations; command state is evaluated during snapshot publication.
- Add active-operation properties such as operation name, phase, status, target, and start time to affected resources.
- Return structured command results with success, canceled, and failure shapes. Include machine-readable diagnostics when agents will consume the result.

DON'T:

- Don't expose destructive commands without confirmation text and clear result data.
- Don't leave commands disabled after failures or cancellations.
- Don't make resource commands guess global controller state from resource snapshots alone.

## Reentrant and idempotent operations

Controller intents should be safe to retry after cancellation, process restart, partial external success, or drift.

DO:

- Re-read persisted and live state at operation execution time.
- Separate "forget local state" from "delete live external resource" APIs.
- Preserve explicit user overrides when resetting generated state; discard inferred state that should not survive a context change.
- Reconcile live children to the desired app model when the controller owns them.
- Treat location/context changes as workflows: validate, delete incompatible live resources if required, persist the new intent, reset state, then reprovision.
- Keep prompts and long user interactions outside the serialized operation queue. Queue only the apply/reconcile intent after the user completes the interaction.
- Coalesce background probes so at most one is queued or running.
- Serialize background drift checks through the same queue, but avoid disabling user commands for read-only probes unless the probe mutates state.

DON'T:

- Don't assume an operation that failed before completion left no external state.
- Don't overwrite user-provided values while refreshing dynamic command inputs.
- Don't use health checks for reconciliation side effects.

## Dependency-aware fan-out

Serialization at the controller boundary does not mean all internal work must be sequential.

DO:

- Fan out independent resources within a single intent only after the controller has accepted the operation.
- Preserve dependency ordering with per-resource completion tasks or another explicit dependency graph.
- Complete each per-resource task through a small set of paths: success, failure, or cancellation.
- Preserve an existing incomplete per-resource completion task when other components may already be awaiting it.
- Publish state to both visible resources and any surrogate/resources that represent the same external operation.
- Propagate broad state changes to child resources without recursion when graphs can be deep.
- Publish `ConnectionStringAvailableEvent` or other readiness signals after outputs/references are available, including for children that derive values from a parent.

DON'T:

- Don't make unrelated resources wait for the entire batch when their prerequisites are already done.
- Don't replace a task that existing waiters might be awaiting.
- Don't hide a precise terminal state with a generic failure state after a lower-level operation already published the detailed status.

## Cancellation and drift

Long-running controllers need explicit cancellation and drift behavior.

DO:

- Link operation cancellation to the caller token and expose a cancel command when the external operation can be interrupted.
- Mark resources as canceling before waiting for external cancellation to finish.
- Treat external cancellation races as expected; use persisted external state as the source of truth for final status.
- Run drift detection only after the aggregate environment is running.
- Mark drifted/missing resources with actionable states and expose commands that can reprovision, delete, forget state, or inspect live state.

DON'T:

- Don't queue unlimited background drift checks.
- Don't mark resources drifted when there is not enough persisted state to identify live resources.
- Don't block the serialized queue while waiting for a user to respond to a notification.

## Testing

DO test:

- Concurrent mutating commands serialize when they target different resources.
- Independent resources still run concurrently when the controller is only a stateless lifecycle orchestrator.
- Conflicting active or queued operations fail fast even if dashboard command state has not refreshed.
- Command states disable and re-enable on success, failure, and cancellation.
- Cancel command availability for active, queued, nonaffected, and noncancelable resource states.
- Background probes are coalesced and do not flicker command states when they are read-only.
- Per-resource completion tasks unblock dependents and are not replaced while incomplete.
- Dynamic command inputs preserve user-entered values and re-enable after loading failures when custom input is allowed.
- Diagnostic commands return structured data without live external service dependencies in unit tests.
- Queue lifetime behavior: after the reader loop stops or the controller is disposed, new operations fail fast and already queued operations complete or cancel.

DON'T:

- Don't test controller concurrency only through dashboard snapshots; directly exercise the controller queue and command execution paths.
- Don't skip a regression test for accidental global serialization. Use a gated fake external client where all independent resources must rendezvous before any operation is released.
- Don't rely on live cloud or external services for ordinary controller unit tests.
