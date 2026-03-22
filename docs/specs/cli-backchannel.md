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
// 4. Required properties use 'required' keyword
// 5. Optional properties are nullable (T?) - can be added without breaking
// 6. Empty request classes are allowed (for future expansion)
// 7. Method names: Get*Async, Watch*Async (streaming), Call*Async (actions)
// =============================================================================
```

### Why These Rules?

**Rule 1 & 2 (Request/Response objects)**: Allows adding parameters and return fields without changing the method signature.

**Rule 3 (Sealed classes with init)**: Immutable after construction, thread-safe, clear intent.

**Rule 4 & 5 (Required vs nullable)**: Makes the contract explicit. Required = must be set. Nullable = optional, can be added later.

**Rule 6 (Empty request classes)**: Even if a method needs no parameters today, wrap it in a request object. Tomorrow you might need to add filtering, pagination, or options.

**Rule 7 (Naming convention)**: Consistent naming makes the API predictable.

## Versioning Strategy

### Capability Strings

```csharp
internal static class AuxiliaryBackchannelCapabilities
{
    public const string V1 = "aux.v1";  // 13.1 baseline
    public const string V2 = "aux.v2";  // 13.2+ with request objects
}
```

### What Shipped When

| Version | Capability | Methods |
|---------|------------|---------|
| 13.1 | `aux.v1` | `GetAppHostInformationAsync()`, `GetDashboardMcpConnectionInfoAsync()`, `StopAppHostAsync()` |
| 13.2 | `aux.v2` | All v1 methods + new request-object-based methods |

### Compatibility Matrix

| CLI | AppHost | Behavior |
|-----|---------|----------|
| Old | Old | Works (v1) |
| Old | New | Works (v1 methods still exist) |
| New | Old | Works (CLI detects missing capability, falls back) |
| New | New | Works (uses v2) |

## Adding New Methods

### Step 1: Define the Request/Response Types

```csharp
internal sealed class GetSomethingRequest
{
    public string? Filter { get; init; }  // Optional from day one
}

internal sealed class GetSomethingResponse
{
    public required SomethingData[] Items { get; init; }
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
    if (!SupportsV2)
    {
        // Fall back to v1 behavior or return empty/default
        return new GetSomethingResponse { Items = [] };
    }
    
    return await _rpc.InvokeAsync<GetSomethingResponse>("GetSomethingAsync", [request], ct);
}
```

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

## Summary

The backchannel is designed for **long-term compatibility**. The patterns may seem overly cautious for internal APIs, but they pay off when users mix CLI and AppHost versions in the real world. When in doubt, add a new method rather than modifying an existing one.
