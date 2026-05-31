# Custom Resource Lifecycle Model

> **Status**: Proposal
> **Issues**: [#13647](https://github.com/microsoft/aspire/issues/13647), [#10365](https://github.com/microsoft/aspire/issues/10365)
> **Audience**: Aspire contributors, integration authors, and advanced users building custom resources.

## Motivation

Custom resources in Aspire today require significant boilerplate. Authors must manually:

- Construct a full `CustomResourceSnapshot` object with `WithInitialState`
- Write and manage a `while` loop inside `OnInitializeResource`
- Fire lifecycle events (`BeforeResourceStartedEvent`, `ResourceEndpointsAllocatedEvent`) by hand
- Set timestamps (`CreationTimeStamp`, `StartTimeStamp`, `StopTimeStamp`) explicitly
- Wire Start/Stop/Restart dashboard commands (which today only work for DCP-backed resources)
- Work around snapshot clobbering — DCP's `ResourceSnapshotBuilder.ToSnapshot` overwrites `Urls`, `EnvironmentVariables`, `Volumes`, `Relationships`, and some `Properties` on every watch event, silently discarding anything the user added via `PublishUpdateAsync`

This makes custom resources hard to author, error-prone, and fragile. The snapshot clobbering issue (#13647) means users cannot reliably add dynamic URLs or properties to DCP-managed resources.

### The problem in one example

```csharp
// User adds a custom URL to a container.
// It appears briefly, then vanishes when DCP refreshes.
builder.AddContainer("nginx", "nginx")
    .OnBeforeResourceStarted(async (res, evt, ct) =>
    {
        await evt.Services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(res, x => x with
            {
                Urls = [.. x.Urls, new("Hello World", "http://localhost/", false)]
            });
    });
```

The "Hello World" URL disappears because `ResourceSnapshotBuilder.ToSnapshot` replaces the entire `Urls` collection with freshly computed DCP-sourced URLs on every state transition.

## Design principles

This proposal keeps a single, consistent level of abstraction. There is **one** lifecycle pattern for custom resources, plus targeted helpers that remove the most painful boilerplate. The framework takes ownership of the ceremony around lifecycle (events, timestamps, commands, state-machine wrap-up) but the author still owns the body of the lifecycle method.

1. **Targeted updates, not wholesale replacement.** Each producer (DCP, orchestrator, user code) updates only its slice of the resource snapshot. Producers cannot clobber each other.

2. **One lifecycle hook, framework-managed ceremony.** A single `OnLifecycle(async (ctx, ct) => ...)` hook. The framework fires lifecycle events, sets timestamps, and wires Start/Stop/Restart commands around it. The author writes whatever logic they need inside it — a loop, an event subscription, a one-shot call.

3. **A focused context, not a snapshot constructor.** A new `ResourceContext` exposes targeted methods (`SetStateAsync`, `SetPropertyAsync`, `AddUrlAsync`) so authors update just the fields they care about, never reconstruct a whole snapshot.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              ResourceNotificationService                    │
│    (merges slices from all producers into one snapshot)     │
│                                                             │
│  ┌────────┐  ┌────────┐  ┌────────┐  ┌────────┐             │
│  │ State  │  │  URLs  │  │  Props │  │  Env   │  ...        │
│  │ slice  │  │ slice  │  │ slice  │  │ slice  │             │
│  └───▲────┘  └───▲────┘  └───▲────┘  └───▲────┘             │
│      │           │           │           │                  │
└──────┼───────────┼───────────┼───────────┼──────────────────┘
       │           │           │           │
  ┌────┴──┐  ┌─────┴─┐   ┌─────┴─┐  ┌──────┴┐
  │  DCP  │  │Orchest│   │ User  │  │ User  │
  │watcher│  │rator  │   │ code  │  │ code  │
  └───────┘  └───────┘   └───────┘  └───────┘
```

Each producer calls targeted update methods that only affect its own slice. The notification service merges all slices into the final `CustomResourceSnapshot` that flows to the dashboard.

## Layer 1: Source-scoped snapshot updates

### Problem

`ResourceSnapshotBuilder.ToSnapshot` does `previous with { Urls = urls, EnvironmentVariables = env, ... }`, replacing every collection wholesale. Any URL, env var, or property added by user code via `PublishUpdateAsync` is silently lost.

### Solution

The notification service tracks collection items **per source**. Each producer identifies itself when updating a collection. The service stores items grouped by source and merges all sources into the rendered snapshot.

```csharp
// Internal API — used by DCP and the orchestrator:
notifications.PublishUrlsAsync(resource, "dcp", dcpUrls);
notifications.PublishEnvironmentAsync(resource, "dcp", dcpEnvVars);
notifications.PublishPropertiesAsync(resource, "dcp", dcpProperties);

// User code continues to use PublishUpdateAsync.
// Collection writes are routed to the "user" source internally.
await notifications.PublishUpdateAsync(resource, s => s with
{
    Urls = [.. s.Urls, new("Hello World", "http://localhost/", false)]
});
// The "Hello World" URL is stored in the "user" source.
// DCP updates only replace the "dcp" source — user URLs are untouched.
```

When DCP calls `PublishUrlsAsync(resource, "dcp", newUrls)`:
1. All previous URLs from source `"dcp"` are replaced with `newUrls`.
2. URLs from source `"user"` and `"orchestrator"` are untouched.
3. The merged snapshot contains URLs from all sources.

### Atomicity

DCP needs to update state + URLs + env + properties in one atomic operation to avoid intermediate states visible to the dashboard. A batch API handles this:

```csharp
// Internal: atomic multi-slice update
notifications.PublishSlicedUpdateAsync(resource, "dcp", batch =>
{
    batch.State = dcpState;
    batch.Urls = dcpUrls;
    batch.EnvironmentVariables = dcpEnv;
    batch.Properties = dcpProperties;
    batch.Volumes = dcpVolumes;
    batch.Relationships = dcpRelationships;
});
```

### Backward compatibility

`PublishUpdateAsync(resource, Func<CustomResourceSnapshot, CustomResourceSnapshot>)` continues to work exactly as today for scalar fields (`State`, `ExitCode`, timestamps). For collection fields, writes are routed to the `"user"` source. This means existing code that appends to `Urls` via `s => s with { Urls = [..s.Urls, myUrl] }` correctly adds to the user slice without affecting DCP-owned URLs.

Layer 1 is independently shippable and fixes #13647 on its own. Nothing else in this spec depends on the layers above also shipping.

## Layer 2: `ResourceContext` and targeted public API

### `ResourceContext`

A new class that provides the state-update surface for resource authors. Instead of constructing whole `CustomResourceSnapshot` records, authors use targeted setters.

```csharp
public class ResourceContext
{
    /// <summary>The resource this context is associated with.</summary>
    public IResource Resource { get; }

    /// <summary>A logger scoped to this resource.</summary>
    public ILogger Logger { get; }

    /// <summary>The host service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// The resource notification service. Exposed directly for convenience so authors
    /// don't have to resolve it from <see cref="Services"/> for common operations like
    /// <c>WaitForResourceAsync</c> / <c>WaitForResourceHealthyAsync</c>.
    /// </summary>
    public ResourceNotificationService Notifications { get; }

    /// <summary>
    /// The distributed application eventing service. Exposed directly for convenience.
    /// </summary>
    public IDistributedApplicationEventing Eventing { get; }

    /// <summary>Updates the resource state shown in the dashboard.</summary>
    public Task SetStateAsync(ResourceStateSnapshot state, CancellationToken ct = default);

    /// <summary>
    /// Updates the resource state. The string should typically be a value from
    /// <see cref="KnownResourceStates"/> so the dashboard renders consistent styling
    /// and behavior. Custom values are allowed but rendered as plain text.
    /// </summary>
    public Task SetStateAsync(string state, CancellationToken ct = default);

    /// <summary>Sets a property shown in the resource details panel.</summary>
    public Task SetPropertyAsync(string name, object? value, bool isSensitive = false, CancellationToken ct = default);

    /// <summary>Adds or updates a URL shown in the dashboard.</summary>
    public Task AddUrlAsync(UrlSnapshot url, CancellationToken ct = default);

    /// <summary>Removes a URL by name.</summary>
    public Task RemoveUrlAsync(string urlName, CancellationToken ct = default);

    /// <summary>
    /// Tracks a disposable that will be disposed when the resource stops.
    /// Use this for connections, clients, or other resources with cleanup
    /// instead of writing try/finally blocks around your loop.
    /// </summary>
    public void Track(IAsyncDisposable disposable);

    /// <summary>Tracks a disposable that will be disposed when the resource stops.</summary>
    public void Track(IDisposable disposable);

    /// <summary>Retrieves a previously tracked object by type.</summary>
    public T Get<T>() where T : class;
}
```

### Targeted methods on `ResourceNotificationService`

In addition to the existing `PublishUpdateAsync`, new public methods for targeted updates so callers who don't have a `ResourceContext` (event handlers, custom commands, lifecycle hooks) can still update single fields:

```csharp
public class ResourceNotificationService
{
    // Existing (kept):
    public Task PublishUpdateAsync(IResource resource,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory);

    // New:
    public Task PublishStateAsync(IResource resource, ResourceStateSnapshot state);
    public Task PublishStateAsync(IResource resource, string state);
    public Task PublishPropertyAsync(IResource resource,
        string name, object? value, bool isSensitive = false);
    public Task PublishUrlAsync(IResource resource, UrlSnapshot url);
    public Task RemoveUrlAsync(IResource resource, string urlName);
}
```

### Builder extensions

Registration-time helpers that replace the need to construct a full `CustomResourceSnapshot`:

```csharp
/// <summary>Sets the resource type shown in the dashboard Type column.</summary>
public static IResourceBuilder<T> WithResourceType<T>(
    this IResourceBuilder<T> builder, string resourceType)
    where T : IResource;

/// <summary>Adds a property visible in the dashboard resource details.</summary>
public static IResourceBuilder<T> WithProperty<T>(
    this IResourceBuilder<T> builder,
    string name, object? value, bool isSensitive = false)
    where T : IResource;
```

These are additive — `WithInitialState` remains for backward compatibility but is marked `[Obsolete(error: false)]` to steer new code toward the helpers.

### Custom commands also receive a `ResourceContext`

To address the most common reason authors reach for `ResourceNotificationService` from a custom command, the `WithCommand` callback signature is extended to provide a `ResourceContext`:

```csharp
public static IResourceBuilder<T> WithCommand<T>(
    this IResourceBuilder<T> builder,
    string name,
    string displayName,
    Func<ResourceContext, ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
    /* existing parameters */)
    where T : IResource;
```

This is purely additive; existing `WithCommand` overloads continue to work.

## Layer 3: One lifecycle hook, framework-managed ceremony

### The single primitive: `OnLifecycle`

A custom resource that wants to participate in the dashboard lifecycle implements one hook:

```csharp
/// <summary>
/// Registers the lifecycle body for this resource. The framework owns the surrounding
/// ceremony (lifecycle events, timestamps, command wiring, cleanup of tracked
/// disposables). The author owns whatever happens inside the callback.
/// </summary>
/// <remarks>
/// The framework calls the callback when the resource should start (auto-start at
/// app startup, or in response to a Start/Restart command). The cancellation token
/// is cancelled when the resource should stop. The callback should observe the token
/// and return promptly. If the callback throws, the framework transitions the resource
/// to <see cref="KnownResourceStates.FailedToStart"/> (if it never reached Running)
/// or <see cref="KnownResourceStates.Exited"/> (if it did) and exposes the failure
/// in the dashboard.
/// </remarks>
public static IResourceBuilder<T> OnLifecycle<T>(
    this IResourceBuilder<T> builder,
    Func<ResourceContext, CancellationToken, Task> lifecycle)
    where T : IResource;
```

That's the entire third layer.

### What the framework handles around `OnLifecycle`

| Concern | Framework behavior |
|---------|-------------------|
| **Registration** | Sets `CreationTimeStamp` and initial `State = NotStarted`. |
| **Start** | Fires `BeforeResourceStartedEvent`, transitions to `Starting`, sets `StartTimeStamp`, then invokes the callback with a fresh `CancellationToken`. |
| **Stop / Restart commands** | Wires Start/Stop/Restart dashboard commands. Stop cancels the token; Restart does stop-then-start. |
| **URL resolution** | Auto-resolves `ResourceUrlAnnotation`s into `UrlSnapshot`s on start. |
| **URL active state** | Marks URLs active on start, inactive on stop. |
| **`WaitFor` support** | Works automatically because `BeforeResourceStartedEvent` fires at the right time. |
| **Normal return** | If the author hasn't already set a terminal state (`Finished`, `Exited`, etc.), the framework sets `State = Finished`, `StopTimeStamp`. Fires `ResourceStoppedEvent`. |
| **Cancellation** | Same as normal return, plus the framework swallows `OperationCanceledException` thrown by the token. |
| **Exception** | If the author has not yet transitioned to `Running`, state → `FailedToStart`. Otherwise state → `Exited` with the exception logged. Either way, `StopTimeStamp` is set and `ResourceStoppedEvent` fires. The resource remains restartable. |
| **Cleanup** | All disposables tracked via `ctx.Track(...)` are disposed in reverse registration order after the callback returns or throws, before the resource transitions to its terminal state. |

The author is responsible for transitioning to `Running` (or another live state) when their resource is actually ready, because only the author knows when that is. The framework cannot guess.

### Resources that opt out of lifecycle

A resource that doesn't implement `IResourceWithLifetime` (or implements a marker like `IResourceWithoutLifecycle`) is skipped by the framework's start/stop machinery, doesn't get Start/Stop/Restart commands, and its `OnLifecycle` registration (if any) is treated as a no-op. This covers things like `ParameterResource` and other configuration-style resources that don't have a meaningful runtime lifecycle.

### Examples

These are the four common patterns. They all use the same `OnLifecycle` hook; the body varies.

#### Periodic (replaces TalkingClock today)

```csharp
builder.AddResource(new TalkingClockResource("clock"))
    .ExcludeFromManifest()
    .WithResourceType("TalkingClock")
    .WithProperty(CustomResourceKnownProperties.Source, "Talking Clock")
    .WithUrl("https://www.speaking-clock.com/", "Speaking Clock")
    .OnLifecycle(async (ctx, ct) =>
    {
        await ctx.SetStateAsync(KnownResourceStates.Running, ct);
        long tick = 0;
        while (!ct.IsCancellationRequested)
        {
            await ctx.SetStateAsync(tick++ % 2 == 0
                ? new ResourceStateSnapshot("Tick", KnownResourceStateStyles.Success)
                : new ResourceStateSnapshot("Tock", KnownResourceStateStyles.Success), ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    });
```

Compare to the [current TalkingClock implementation](../../playground/CustomResources/CustomResources.AppHost/TalkingClockResource.cs): ~30 lines, manual `BeforeResourceStartedEvent` firing, manual timestamps, manual snapshot construction. The above is ~12 lines and the framework handles the rest.

#### Bound to another resource (replaces DevTunnelPort manual subscriptions)

When a resource's lifecycle follows another resource, the author waits for the source resource's state directly using existing notification APIs. No new primitive needed:

```csharp
portBuilder.OnLifecycle(async (ctx, ct) =>
{
    // Wait for the parent tunnel to be ready.
    await ctx.Notifications.WaitForResourceHealthyAsync(tunnel.Resource.Name, ct);

    // Do the port-specific setup.
    var port = (DevTunnelPortResource)ctx.Resource;
    var tunnelPort = port.LastKnownStatus!;
    port.TunnelEndpointAnnotation.AllocatedEndpoint =
        new(port.TunnelEndpointAnnotation, tunnelPort.PortUri!.Host, 443);

    await ctx.SetStateAsync(KnownResourceStates.Running, ct);

    // Wait for the parent tunnel to stop, then return so the framework can
    // tear us down. If the parent restarts, the framework's restart logic
    // will re-invoke this callback.
    await ctx.Notifications.WaitForResourceAsync(
        tunnel.Resource.Name,
        [KnownResourceStates.Finished, KnownResourceStates.Exited, KnownResourceStates.FailedToStart],
        ct);
});
```

This replaces ~130 lines of manual ceremony in the current `DevTunnelPortResource` (`OnResourceReady` + `OnResourceStopped` handlers that manually fire events, set state, allocate endpoints, toggle URL activity, manage timestamps, publish `ResourceStoppedEvent`).

#### Streaming / long-running loop (Kafka monitor)

```csharp
builder.AddResource(new KafkaMonitorResource("kafka-monitor"))
    .WithResourceType("KafkaMonitor")
    .OnLifecycle(async (ctx, ct) =>
    {
        var consumer = new KafkaConsumer(ctx.Get<KafkaConfig>().BootstrapServers);
        ctx.Track(consumer); // disposed by the framework on stop

        await ctx.SetStateAsync(KnownResourceStates.Running, ct);

        await foreach (var msg in consumer.ConsumeAsync(ct))
        {
            await ctx.SetPropertyAsync("last-offset", msg.Offset, ct: ct);
            await ctx.SetPropertyAsync("last-timestamp", msg.Timestamp, ct: ct);
        }
    });
```

#### One-shot setup (external service health probe)

```csharp
builder.AddResource(new ExternalApiResource("payments-api"))
    .WithResourceType("ExternalAPI")
    .WithUrl("https://api.payments.com", "API")
    .OnLifecycle(async (ctx, ct) =>
    {
        var client = new HttpClient();
        ctx.Track(client);

        var response = await client.GetAsync("https://api.payments.com/health", ct);
        response.EnsureSuccessStatusCode();

        await ctx.SetStateAsync(KnownResourceStates.Running, ct);

        // Stay alive until shutdown. Health is reported via a separate health check.
        await Task.Delay(Timeout.Infinite, ct);
    });
```

(Continuous health is best expressed with `WithHealthCheck`, not with the `State` field. The dashboard renders a separate health badge for this.)

### Optional helpers (built on `OnLifecycle`)

The most common patterns above can be packaged as extension methods that build on `OnLifecycle`. These are convenience helpers, not new primitives — they expand to ordinary `OnLifecycle` bodies and live in any namespace the consumer prefers.

```csharp
public static class ResourceLifecycleExtensions
{
    /// <summary>
    /// Convenience: runs <paramref name="onTick"/> on a fixed interval until the
    /// resource is stopped. Equivalent to writing a while loop inside OnLifecycle.
    /// </summary>
    public static IResourceBuilder<T> WithInterval<T>(
        this IResourceBuilder<T> builder,
        TimeSpan period,
        Func<ResourceContext, long, CancellationToken, Task> onTick)
        where T : IResource
    {
        return builder.OnLifecycle(async (ctx, ct) =>
        {
            await ctx.SetStateAsync(KnownResourceStates.Running, ct);
            long tick = 0;
            while (!ct.IsCancellationRequested)
            {
                await onTick(ctx, tick++, ct);
                await Task.Delay(period, ct);
            }
        });
    }
}
```

Authors who like the helper can use it. Authors who want full control use `OnLifecycle` directly. There is exactly one primitive in the public API; everything else is layered on top.

### What `OnLifecycle` does **not** prescribe

To avoid the design debates around composing multiple lifecycle primitives, this proposal deliberately makes these the author's responsibility:

- Whether to use a timer, an event subscription, a stream, or a one-shot call.
- When to transition to `Running`.
- Whether failures are `Exited` or `FailedToStart` (the framework picks based on whether `Running` was reached — see the table above — but the author can override by calling `SetStateAsync` explicitly before throwing or returning).
- What happens if multiple background activities run inside the callback (compose them with `Task.WhenAll` or `Task.WhenAny` — the framework just sees one Task).

This keeps the framework contract small and predictable, and avoids needing rules for "what if I combine `WithInterval` with `OnStarted` with `RunAsync`."

## Start/Stop/Restart routing for custom resources

Today, `ApplicationOrchestrator.StartResourceAsync` and `StopResourceAsync` route directly to `_dcpExecutor`, which only knows about DCP-managed resources. Custom resources get nothing.

With this proposal, the orchestrator gains a custom resource lifecycle path:

```
Dashboard "Stop" click
  → CommandsConfigurationExtensions
    → orchestrator.StopResourceAsync(name)
      → If DCP resource → _dcpExecutor.StopResourceAsync (existing path)
      → If custom resource → cancel the resource's CancellationTokenSource
        → OnLifecycle callback observes cancellation, returns
        → Framework: disposes tracked disposables, sets terminal state,
          sets StopTimeStamp, fires ResourceStoppedEvent

Dashboard "Start" click
  → orchestrator.StartResourceAsync(name)
    → If DCP resource → _dcpExecutor.StartResourceAsync (existing path)
    → If custom resource → create new CTS, re-invoke OnLifecycle callback
      → Framework: fires BeforeResourceStartedEvent, sets Starting,
        sets StartTimeStamp, invokes callback
```

Resources that implement `IResourceWithoutLifecycle` (or otherwise opt out) are skipped — they do not appear in Start/Stop/Restart command lists.

## TypeScript API

The TypeScript API mirrors the C# API through the polyglot code-generation system.

### `ResourceNotificationService`

```typescript
export interface ResourceNotificationService {
    // Existing:
    publishResourceUpdate(resource: Awaitable<Resource>,
        options?: PublishResourceUpdateOptions): ResourceNotificationServicePromise;

    // New:
    publishState(resource: Awaitable<Resource>,
        state: string,
        options?: PublishStateOptions): ResourceNotificationServicePromise;

    publishProperty(resource: Awaitable<Resource>,
        name: string, value: unknown,
        options?: PublishPropertyOptions): ResourceNotificationServicePromise;

    publishUrl(resource: Awaitable<Resource>,
        url: UrlSnapshot): ResourceNotificationServicePromise;

    removeUrl(resource: Awaitable<Resource>,
        urlName: string): ResourceNotificationServicePromise;
}

export interface PublishStateOptions {
    stateStyle?: string;
}

export interface PublishPropertyOptions {
    isSensitive?: boolean;
}

export interface UrlSnapshot {
    name?: string;
    url: string;
    isInternal?: boolean;
}
```

### `ResourceContext` and `onLifecycle`

```typescript
export interface ResourceContext {
    resource(): Promise<Resource>;
    services(): Promise<IServiceProvider>;
    logger(): Promise<ILogger>;

    setState(state: string, options?: PublishStateOptions): Promise<void>;
    setProperty(name: string, value: unknown, options?: PublishPropertyOptions): Promise<void>;
    addUrl(url: UrlSnapshot): Promise<void>;
    removeUrl(urlName: string): Promise<void>;

    track(disposable: AsyncDisposable | Disposable): void;
    get<T>(): T;
}

// On any resource builder:
.withResourceType(resourceType: string)
.withProperty(name: string, value: unknown, options?: PublishPropertyOptions)
.onLifecycle(lifecycle: (ctx: ResourceContext, stopSignal: AbortSignal) => Promise<void>)
```

### TypeScript example

```typescript
const clock = await builder.addResource("clock")
    .withResourceType("TalkingClock")
    .withProperty("aspire.resource.source", "Talking Clock")
    .onLifecycle(async (ctx, stop) => {
        await ctx.setState("Running");
        let tick = 0;
        while (!stop.aborted) {
            await ctx.setState(tick++ % 2 === 0 ? "Tick" : "Tock",
                { stateStyle: "success" });
            await delay(1000, stop);
        }
    });
```

## Migration guide

### From `WithInitialState` + `OnInitializeResource`

```csharp
// Before:
builder.AddResource(myResource)
    .WithInitialState(new CustomResourceSnapshot
    {
        ResourceType = "MyResource",
        CreationTimeStamp = DateTime.UtcNow,
        State = KnownResourceStates.NotStarted,
        Properties = [new("Source", "my-source")]
    })
    .OnInitializeResource(async (resource, @event, token) =>
    {
        await @event.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource, @event.Services), token);
        await @event.Notifications.PublishUpdateAsync(resource, s => s with
        {
            StartTimeStamp = DateTime.UtcNow,
            State = KnownResourceStates.Running
        });
        while (!token.IsCancellationRequested)
        {
            // ... work ...
            await Task.Delay(interval, token);
        }
    });

// After:
builder.AddResource(myResource)
    .WithResourceType("MyResource")
    .WithProperty("Source", "my-source")
    .OnLifecycle(async (ctx, ct) =>
    {
        await ctx.SetStateAsync(KnownResourceStates.Running, ct);
        while (!ct.IsCancellationRequested)
        {
            // ... work ...
            await Task.Delay(interval, ct);
        }
    });
```

The reductions: no manual snapshot construction, no manual event firing, no manual timestamp management, and Start/Stop/Restart commands work from the dashboard without additional wiring.

### From `OnResourceReady` / `OnResourceStopped` (bound lifecycle)

See the DevTunnelPort example above. The pattern is to put the wait-for-parent logic inside `OnLifecycle` using `WaitForResourceAsync` / `WaitForResourceHealthyAsync`, instead of subscribing to events on the parent.

## Implementation phases

| Phase | Scope | Dependencies |
|-------|-------|-------------|
| **1. Source-scoped slices** | Internal plumbing: per-source collection tracking in `ResourceNotificationService`, `PublishSlicedUpdateAsync` for DCP, refactor `ResourceSnapshotBuilder` and `ApplicationOrchestrator`. Fixes #13647. | None |
| **2. `ResourceContext` + public API** | New `ResourceContext` class, targeted public methods on `ResourceNotificationService`, builder helpers (`WithResourceType`, `WithProperty`), `CreationTimeStamp` auto-default, `WithCommand` overload that accepts `ResourceContext`. | Phase 1 |
| **3. `OnLifecycle` hook** | Framework-owned ceremony (events, timestamps, command wiring, cleanup), Start/Stop/Restart routing for custom resources, opt-out via `IResourceWithoutLifecycle`. | Phase 2 |
| **4. TypeScript parity** | Code-gen changes to emit new methods on `ResourceNotificationService`, builders, and the `onLifecycle` hook. | Phase 3 |

Each phase is independently shippable and useful. Phase 1 alone fixes the clobbering bug. Phase 2 alone meaningfully improves authoring. Phase 3 closes the loop on Start/Stop/Restart commands and event firing for custom resources.

## Open questions

1. **Naming.** `OnLifecycle` vs `WithLifecycle` vs `OnRun` vs `WithBody`? `ResourceContext` vs `ResourceUpdater` vs `ResourceController`? Naming TBD pending API review.

2. **Framework state on author-thrown exceptions.** When the callback throws after `Running` was reached, should the framework expose the exception as an `ExitCode`-like value, or just log it? Recommendation: log plus a synthetic non-zero exit code so the dashboard renders the resource with error styling.

3. **`PublishUpdateAsync` collection semantics.** When existing code does `s => s with { Urls = [myUrl] }`, should this replace only the `"user"` source's URLs (breaking: user loses all other URLs from `s.Urls` they might have appended), or set the full merged collection (non-breaking but clobbers DCP URLs)? Recommendation: write to `"user"` source only, and log a warning when collection fields are set via the legacy API to guide migration.

4. **`ResourceContext` lifetime on restart.** On restart, should a fresh `ResourceContext` be created (all tracked disposables disposed, fresh state)? Recommendation: yes — restart is a full stop + start cycle.

5. **Endpoint integration.** Custom resources that expose endpoints (e.g., a user-defined HTTP server resource — see [#16640](https://github.com/microsoft/aspire/issues/16640)) need a way to declare ports that are then surfaced through service discovery and `EndpointReference`. This is adjacent to lifecycle but not covered by this proposal; it's tracked separately.

6. **`ExecutionConfigurationBuilder` integration.** Custom resources that produce a process (env vars + arguments) currently need bespoke coordination (see [#14954](https://github.com/microsoft/aspire/issues/14954)). `IResourceWithArguments` / `IResourceWithEnvironment` interop with `ResourceContext` is a follow-up, not part of this proposal.

7. **Health checks vs `State`.** Continuous health-like signals (`Healthy` / `Degraded`) should be expressed with `WithHealthCheck`, not by overloading the `State` field. This is documentation/guidance, not a code change in this spec.
