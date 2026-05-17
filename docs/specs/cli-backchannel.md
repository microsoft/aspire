# CLI Auxiliary Backchannel

This document describes the design philosophy and patterns for the CLI-to-AppHost RPC communication channel.

## Philosophy

The auxiliary backchannel exists because the CLI and AppHost are **separately versioned components** that need to communicate reliably across version boundaries. Users may run:

- A new CLI against an old AppHost (updated CLI, existing project)
- An old CLI against a new AppHost (CI environment with pinned CLI)

This creates a **compatibility matrix** that we must handle gracefully. The backchannel is designed with these principles:

### 1. Never Break Existing Clients

Once a method signature ships, it ships forever. We don't remove methods or change their signatures. Instead, we deprecate and add new methods alongside them.

### 2. Design for Extensibility from Day One

Every method takes a **single request object** and returns a **single response object**. This allows us to add optional properties later without breaking the wire format.

**Bad** (can't add parameters without breaking):
```csharp
Task<Logs> GetLogsAsync(string resourceName, bool follow)
```

**Good** (can add `TailLines`, `Filter`, etc. later):
```csharp
Task<GetLogsResponse> GetLogsAsync(GetLogsRequest request)
```

### 3. Capability Negotiation Over Version Numbers

Rather than exposing a version number and maintaining a compatibility table, we use **capability strings**. The client asks "what can you do?" and adapts accordingly.

```text
CLI: "What capabilities do you have?"
AppHost: ["aux.v1", "aux.v2"]
CLI: "Great, I'll use v2 methods"
```

If the method doesn't exist (old AppHost), the CLI catches the exception and falls back.

### 4. The Contract is the Interface

The C# interface **is** the spec. We don't maintain a separate IDL or proto file. The interface with its XML docs is the source of truth:

```csharp
public interface IAuxiliaryBackchannel
{
    Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null);
    Task<GetResourcesResponse> GetResourcesAsync(GetResourcesRequest? request = null);
    IAsyncEnumerable<ResourceSnapshot> WatchResourcesAsync(WatchResourcesRequest? request = null);
    // ...
}
```

## Contract Rules

When adding or modifying backchannel methods, follow these rules:

```csharp
// =============================================================================
// Auxiliary Backchannel Contract Rules:
//
// 1. All methods take a single request object (nullable where sensible)
// 2. All methods return a response object (or IAsyncEnumerable<T> for streaming)
// 3. Request/response types are sealed classes with { get; init; } properties
// 4. Required properties are only used for values that shipped with the
//    method from day one and are validated immediately at the boundary
// 5. Optional properties are nullable (T?) - can be added without breaking
// 6. Collection properties deserialize missing/null values as empty collections
// 7. Empty request classes are allowed (for future expansion)
// 8. Method names: Get*Async, Watch*Async (streaming), Call*Async (actions)
// =============================================================================
```

### Why These Rules?

**Rule 1 & 2 (Request/Response objects)**: Allows adding parameters and return fields without changing the method signature.

**Rule 3 (Sealed classes with init)**: Immutable after construction, thread-safe, clear intent.

**Rule 4 & 5 (Required vs nullable)**: Makes the contract explicit. Required = must be set by every supported peer for that method. Nullable = optional, can be added later.

**Rule 6 (Collections default to empty)**: JSON can carry `null` for any property even if the C# property is non-nullable. Optional collection properties must treat missing and explicit `null` as empty when that is compatible with older peers.

**Rule 7 (Empty request classes)**: Even if a method needs no parameters today, wrap it in a request object. Tomorrow you might need to add filtering, pagination, or options.

**Rule 8 (Naming convention)**: Consistent naming makes the API predictable.

## Model Authoring Hygiene

Backchannel DTOs are the wire contract. Nullable reference type annotations and property initializers are not enough, because `System.Text.Json` can deserialize explicit `null` into non-nullable reference properties. Every model change must classify each wire member before choosing its C# shape.

Use this workflow whenever adding a method, DTO, property, enum value, or primitive payload that crosses a JSON-RPC boundary:

1. Classify every member as required identity, optional scalar, optional collection, legacy alias, retired field, or capability-gated behavior.
2. Pick the C# shape from the table below. Do not add non-nullable reference properties unless there is a boundary validator or null-to-empty setter that makes the wire contract true.
3. Decide the invalid-data behavior at the boundary: normalize compatible old data, skip malformed stream/list items with a diagnostic, fall back for old peers, or fail the command with a clear malformed-payload message.
4. Register every new method and DTO in the contract guardrails (`BackchannelContractTests`) and JSON serializer guardrails (`BackchannelJsonSerializerContextTests`).
5. Add a wire-shaped test for explicit `null`, missing properties, unknown enum/discriminator values, and malformed required identities.

| Field kind | C# shape | Boundary behavior |
|------------|----------|-------------------|
| Required identity | Nullable during deserialization or `required` only if present since the method shipped | Validate immediately at the RPC boundary. Throw or skip with a clear diagnostic; don't let consumers fail with `NullReferenceException`. |
| Optional scalar | `T?` | Consumers choose their display/default behavior. Missing and explicit `null` are valid. |
| Optional collection | Backing field initialized to `[]` with `init => field = value ?? []` | Missing and explicit `null` deserialize as empty for old-payload compatibility. |
| Legacy alias | `[Obsolete]` property that maps to the replacement | Keep reading it until the compatibility contract is intentionally broken. |
| Retired field | Comment or manifest entry that says the JSON property name must not be reused | Mirrors proto `reserved` fields; a future property must not reuse the old wire name with a different meaning. |
| Capability-gated behavior | Nullable request/response fields plus a capability check | Old peers get a fallback or a clear unsupported error. |

Example optional collection:

```csharp
internal sealed class GetSomethingResponse
{
    private SomethingData[] _items = [];

    public SomethingData[] Items
    {
        get => _items;
        init => _items = value ?? [];
    }
}
```

Example legacy alias:

```csharp
internal sealed class ResourceSnapshot
{
    public string? ResourceType { get; init; }

    [Obsolete("Use ResourceType instead.")]
    public string? Type
    {
        get => ResourceType;
        init => ResourceType = value;
    }
}
```

When a field is required because the command cannot make sense without it, validate at the boundary that receives the wire payload. For streams, document whether malformed items are skipped with a warning or fail the operation. Auxiliary resource snapshots and log lines should skip malformed items when possible; publish/execute payloads that identify an operation should fail clearly when required identity is absent.

### Invalid Data Policy

Invalid wire data should never fail as an incidental `NullReferenceException`, serializer exception, or unexplained connection drop. Pick one policy per payload:

| Policy | When to use it | Expected behavior |
|--------|----------------|-------------------|
| Normalize | Old peers omit a member or send explicit `null` for a value that can safely mean empty/default | Coalesce to `[]`, `null`, `false`, or another documented value and continue. |
| Skip with diagnostic | A stream/list item is malformed but the rest of the command can still produce useful output | Log/debug a diagnostic naming the member and skip only that item. |
| Fallback | A method/capability does not exist on an old peer | Use the older method or return a documented empty/unavailable result. |
| Fail clearly | Required operation identity is absent, or continuing would produce misleading behavior | Throw or return a failed response with `Malformed AppHost ... payload: required member 'X' was null or missing.` |

Prefer skip/normalize for observational data like resource snapshots and log lines. Prefer fail clearly for operation-bearing payloads such as publishing activities, command output, generated files, and required request identities.

### Enums and Discriminators

Prefer strings for wire discriminators unless a value is truly closed. If a C# enum crosses the JSON-RPC boundary, it must have a safe default/unknown behavior and tests for unknown wire values. A newer peer can always add a value that an older peer does not understand; old clients should skip, default, or fail with a clear compatibility message rather than surfacing a serializer exception.

### Retired JSON Fields

JSON has no field numbers, so removed or renamed properties need an explicit tombstone:

```csharp
// Retired JSON properties:
// - "Type": legacy name for "ResourceType"; keep the obsolete alias until the compatibility window ends.
```

Do not reuse retired property names with different meaning. If a field is renamed, prefer keeping an obsolete alias that maps to the new property.

## Versioning Strategy

### Capability Strings

```csharp
internal static class AuxiliaryBackchannelCapabilities
{
    public const string V1 = "aux.v1";  // 13.1 baseline
    public const string V2 = "aux.v2";  // 13.2+ with request objects
    public const string V3 = "aux.v3";  // 13.4+ with batched console log streaming
}
```

### What Shipped When

| Version | Capability | Methods |
|---------|------------|---------|
| 13.1 | `aux.v1` | `GetAppHostInformationAsync()`, `GetDashboardMcpConnectionInfoAsync()`, `StopAppHostAsync()` |
| 13.2 | `aux.v2` | All v1 methods + new request-object-based methods |
| 13.4 | `aux.v3` | All v2 methods + `GetConsoleLogBatchesAsync(GetConsoleLogsRequest)` |

### Console Log Request Compatibility

`GetConsoleLogsRequest.ResourceName` was required when the v2 console log methods shipped. In v3 it is optional: a `null` resource name requests logs for all resources. V2 callers that need all-resource logs should continue to use the legacy `GetResourceLogsAsync` method rather than sending a null `ResourceName` to v2 console log methods.

### Compatibility Matrix

| CLI | AppHost | Behavior |
|-----|---------|----------|
| Old | Old | Works (v1) |
| Old | New | Works (v1 methods still exist) |
| New | Old | Works (CLI detects missing capability, falls back) |
| New | New | Works (uses the highest shared capability) |

## Adding New Methods

### Step 1: Define the Request/Response Types

```csharp
internal sealed class GetSomethingRequest
{
    public string? Filter { get; init; }  // Optional from day one
}

internal sealed class GetSomethingResponse
{
    private SomethingData[] _items = [];

    public SomethingData[] Items
    {
        get => _items;
        init => _items = value ?? [];
    }
}
```

### Step 2: Add to the Server

```csharp
public Task<GetSomethingResponse> GetSomethingAsync(
    GetSomethingRequest? request = null,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

### Step 3: Add to JSON Serializer Context (for AOT)

```csharp
[JsonSerializable(typeof(GetSomethingRequest))]
[JsonSerializable(typeof(GetSomethingResponse))]
internal partial class BackchannelJsonSerializerContext : JsonSerializerContext
```

### Step 4: Add to CLI Client with Fallback

```csharp
public async Task<GetSomethingResponse> GetSomethingAsync(...)
{
    if (!SupportsV3)
    {
        // Fall back to older behavior or return empty/default
        return new GetSomethingResponse { Items = [] };
    }
    
    return await _rpc.InvokeAsync<GetSomethingResponse>("GetSomethingAsync", [request], ct);
}
```

The guardrail tests intentionally fail when:

- A v2 RPC method with a request object is added without being listed in the method contract map.
- A DTO referenced by another backchannel DTO is missing from the contract inventory.
- A non-nullable optional collection relies only on `= []` instead of an explicit null-to-empty setter.
- A new boundary type is missing from the JSON serializer context coverage list.
- A closed enum crosses the boundary without safe unknown/default handling.

## Adding New Properties

This is the beauty of request objects - just add the property:

```csharp
internal sealed class GetResourcesRequest
{
    public string? Filter { get; init; }
    public int? Limit { get; init; }     // NEW - old clients send null, old servers ignore it
}
```

No version bump needed. No new capability needed. It just works.

New response properties must be optional from the receiver's point of view. If a newer AppHost adds a field to an existing response, older CLIs ignore it. If a newer CLI consumes the field, it must also handle old AppHosts omitting it or sending `null`.

## DTO Change Checklist

Use this checklist for any PR that changes backchannel interfaces or DTOs:

- Does every method still take one request object and return one response object or one stream item type?
- Is each new property optional, nullable, or deserialize-time-defaulted so old peers can omit it?
- If a property is required, was it required when the method first shipped, and is it validated immediately at the receiving boundary?
- Did any JSON property get renamed, removed, or change type? If so, keep an `[Obsolete]` alias or add a retired-field tombstone.
- Did a collection property use a backing field with `init => field = value ?? []`?
- Did a wire enum or discriminator get a new value, and do older consumers handle unknown values cleanly?
- Is every request/response/stream item registered in the source-generated JSON serializer context?
- Are there wire-shaped tests for missing fields, explicit `null`, unknown fields, and any new fallback behavior?
- Is a new method or behavior protected by a capability string or a clear fallback?

## Transport Details

- **Protocol**: JSON-RPC 2.0 over StreamJsonRpc
- **Transport**: Unix domain sockets
- **Socket path**: `{temp}/auxi.sock.{hash}` (hash from AppHost project path)
- **Serialization**: System.Text.Json with source generation for AOT

## Thread Safety

- Request/response types are immutable (`init` properties)
- CLI caches capabilities in `ImmutableHashSet<string>`
- Server methods are stateless - they resolve services per-call

## What NOT to Do

❌ **Don't add positional parameters to methods**
```csharp
// BAD - can't extend
Task<Logs> GetLogsAsync(string name, bool follow, int? tail)
```

❌ **Don't remove or rename methods**
```csharp
// BAD - breaks old clients
// Removed: GetResourceSnapshotsAsync
```

❌ **Don't change property types**
```csharp
// BAD - breaks serialization
public int Count { get; init; }  // was string
```

❌ **Don't make optional properties required**
```csharp
// BAD - breaks old clients that don't send it
public required string Filter { get; init; }  // was optional
```

❌ **Don't rely on property initializers for wire nulls**
```csharp
// BAD - explicit JSON null overwrites the initializer
public string[] Items { get; init; } = [];
```

Use the null-to-empty backing-field pattern when `null` should be compatible with an empty collection.

The explicit setter matters: property initializers only handle missing JSON properties, while old AppHosts can send explicit `null` values that would otherwise overwrite the initializer.

❌ **Don't reuse retired JSON property names**
```csharp
// BAD - old payloads may still send "Type" with the legacy meaning
public string? Type { get; init; } // New meaning
```

## Summary

The backchannel is designed for **long-term compatibility**. The patterns may seem overly cautious for internal APIs, but they pay off when users mix CLI and AppHost versions in the real world. When in doubt, add a new method rather than modifying an existing one.
