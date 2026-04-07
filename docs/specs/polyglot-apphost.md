# Polyglot AppHost Support

> **Note:** This feature is experimental and may change in future releases.

This document describes how the Aspire CLI supports non-.NET app hosts using the **Aspire Type System (ATS)**.

**Why a .NET backend?** Guest language apphosts communicate with a .NET AppHost Server via JSON-RPC rather than running standalone. This lets all languages share Aspire's **40+ hosting integrations** and **deployment publishers**—all written in .NET with years of production hardening. Rewriting these in every target language would be massive duplication, a maintenance nightmare, and prone to inconsistencies. Instead, guest code just *declares* resources (Redis, Postgres, Azure services), and the .NET server handles orchestration (starting containers, service discovery, health checks, the Dashboard) and publishing (generating Bicep, Kubernetes YAML). The trade-off is requiring a .NET runtime and IPC overhead, but this is far cheaper than maintaining N languages × M integrations.

## Table of Contents

1. [Overview](#overview)
2. [Design Philosophy](#design-philosophy)
3. [Architecture](#architecture)
4. [Aspire Type System (ATS)](#aspire-type-system-ats)
5. [JSON-RPC Protocol](#json-rpc-protocol)
6. [Code Generation](#code-generation)
7. [TypeScript Implementation](#typescript-implementation)
8. [CLI Integration](#cli-integration)
9. [Configuration](#configuration)
10. [Adding New Guest Languages](#adding-new-guest-languages)
11. [Local Development Workflow](#local-development-workflow)
12. [Security](#security)

---

## Overview

The polyglot apphost feature allows developers to write Aspire app hosts in non-.NET languages. The **Aspire Type System (ATS)** is the foundation—a portable subset of the .NET type system that can be mapped to any language.

You run `aspire run`, and the CLI starts both the .NET AppHost server and your guest runtime locally on the same machine.

**No IDL required.** Unlike gRPC/protobuf or OpenAPI, ATS doesn't introduce a separate interface definition language. The .NET type system is already expressive enough. Integration authors simply add `[AspireExport]` attributes to existing extension methods—their C# code *is* the schema. The scanner extracts everything it needs from the compiled assemblies.

**Key Concepts:**
- **Primitive** - JSON-native types (`string`, `number`, `boolean`, `null`) that serialize directly
- **ATS Type ID** - A portable type identifier derived from assembly and type name (e.g., `Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource`)
- **Capability** - A named operation (e.g., `Aspire.Hosting.Redis/addRedis`)
- **Handle** - An opaque typed reference to a .NET object
- **DTO** - A serializable data transfer object (marked with `[AspireDto]`)
- **Enum** - A .NET enum type that serializes as its string name

---

## Design Philosophy

ATS leverages .NET's rich type system rather than replacing it. The `[AspireExport]` and `[AspireDto]` attributes mark what should be exposed—the rest is inferred from the C# signatures. This means:

- Integration authors write **normal C# extension methods**
- The scanner **extracts types, parameters, and relationships** at build time
- Code generators produce **idiomatic APIs** in each target language

ATS then flattens .NET's polymorphism into a simple, portable model that any language can work with:

| .NET Concept | ATS Approach |
|--------------|--------------|
| Interface inheritance | Expanded to concrete types at scan time |
| Generic constraints | Resolved to concrete types at scan time |
| Method overloading | **Not supported** - method names must be unique within each target type |
| Capability versioning | **Not needed** - NuGet package version handles compatibility |

**The result**: A flat type system where:
- Each concrete type has a complete list of capabilities
- No type hierarchy to reason about
- No generic type parameters
- Guest languages don't need to understand .NET's type system

### Why Flat?

.NET's type system is powerful but complex. Interfaces, generics, and method overloading require sophisticated type checking that many languages don't support natively. By flattening at scan time:

1. **Go, C, and other non-OOP languages** get a natural API without emulating inheritance
2. **TypeScript, Swift** can optionally use the original interface hierarchy
3. **All languages** see the same capabilities - just grouped differently

### Collision Detection

The same method name can appear on different types (e.g., `withEnvironment` on Redis, Container, Project). However, within a single target type, method names must be unique—even across packages.

The scanner detects and reports conflicts:

```text
Error: Method 'withDataVolume' has multiple definitions for target 'Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource':
  - Aspire.Hosting.Redis/withDataVolume
  - ThirdParty.Integration/withDataVolume

Resolution: Use [AspireExport("uniqueMethodName")] to disambiguate.
```

---

## Architecture

### CLI Evolution

The Aspire CLI has evolved through several stages:

1. **v1: dotnet CLI Wrapper** — Initially, the CLI was a thin wrapper around `dotnet run`, adding Aspire-specific features like dashboard integration, certificate management, and `aspire add` for NuGet packages.

2. **v2: Polyglot Support (Current)** — The CLI now supports non-.NET app hosts. It orchestrates an AppHost Server (.NET) alongside guest runtimes (Node.js, Python). Language-specific logic is moving to the server via `ILanguageSupport` implementations.

3. **v3: Thin Shell (Vision)** — The goal is for the CLI to become a pure orchestrator with no language-specific logic. All detection, scaffolding, and execution details will be provided by the server via `RuntimeSpec`. The CLI will simply interpret and execute these specs.

#### v1: dotnet CLI Wrapper

```mermaid
flowchart LR
    subgraph Dependencies
        SDK[".NET SDK"]
    end

    subgraph Processes
        CLI["Aspire CLI"]
        DotNet["dotnet run"]
        AppHost["AppHost.dll"]
    end

    SDK --> CLI
    CLI -->|"spawns"| DotNet
    DotNet -->|"runs"| AppHost
```

- **Single process model**: CLI spawns `dotnet run`, which hosts everything
- **CLI responsibilities**: Certificate management, dashboard URL display, `aspire add`
- **Language support**: .NET only

#### v2: Polyglot Support (Current)

```mermaid
flowchart TB
    subgraph Dependencies
        SDK[".NET SDK"]
        Node["Node.js / Bun"]
        NuGet["NuGet Packages"]
    end

    subgraph CLI Process
        CLI["Aspire CLI"]
        LangLogic["Language Logic<br/>(detection, some scaffolding)"]
    end

    subgraph Server Process
        AppHost["AppHost Server"]
        RPC["JSON-RPC"]
        ILang["ILanguageSupport"]
        ICodeGen["ICodeGenerator"]
    end

    subgraph Guest Process
        Guest["Guest Runtime<br/>(node apphost.ts)"]
    end

    SDK --> CLI
    SDK --> AppHost
    Node --> Guest
    NuGet --> AppHost

    CLI -->|"builds & starts"| AppHost
    CLI -->|"starts"| Guest
    CLI -.->|"some logic still here"| LangLogic
    AppHost -->|"RuntimeSpec"| CLI
    Guest <-->|"invokeCapability"| RPC
```

- **Two process model**: CLI orchestrates AppHost Server + Guest Runtime
- **CLI responsibilities**: Orchestration, some language detection, template management
- **Mixed ownership**: Language logic split between CLI and server (transitional)

#### v3: Thin Shell (Vision)

```mermaid
flowchart TB
    subgraph Dependencies
        Bundled["Bundled .NET Runtime"]
        Runtimes["Language Runtimes<br/>(node, python, etc.)"]
        NuGet["NuGet Packages"]
    end

    subgraph CLI Process
        CLI["Aspire CLI<br/>(pure orchestrator)"]
        GuestRuntime["GuestRuntime<br/>(interprets RuntimeSpec)"]
    end

    subgraph Server Process
        AppHost["AppHost Server"]
        RPC["JSON-RPC"]
        ILang["ILanguageSupport<br/>(all language logic)"]
        ICodeGen["ICodeGenerator"]
    end

    subgraph Guest Process
        Guest["Guest Runtime"]
    end

    Bundled --> CLI
    Bundled --> AppHost
    Runtimes --> Guest
    NuGet --> AppHost

    CLI -->|"getRuntimeSpec"| RPC
    RPC -->|"RuntimeSpec"| GuestRuntime
    GuestRuntime -->|"executes commands from spec"| Guest
    Guest <-->|"invokeCapability"| RPC
```

- **Two process model**: Same as v2, but cleaner separation
- **CLI responsibilities**: Pure orchestration—no language-specific code
- **All language logic on server**: Detection, scaffolding, RuntimeSpec all from `ILanguageSupport`
- **Bundled runtime**: No external .NET SDK required

### Current Architecture

Today, the CLI orchestrates two processes: the **AppHost Server** (.NET) and the **Guest Runtime** (e.g., Node.js). Language-specific logic is progressively moving to server-side `ILanguageSupport` implementations.

```mermaid
flowchart TB
    subgraph CLI["Aspire CLI"]
        direction TB
        GuestProject["GuestAppHostProject"]
        GuestRuntime["GuestRuntime<br/>(interprets RuntimeSpec)"]
        GuestProject --> GuestRuntime
    end

    subgraph Server["AppHost Server (.NET)"]
        direction TB
        Packages["NuGet Packages<br/>(Aspire.Hosting.*)"]
        LanguageSupport["ILanguageSupport<br/>(discovered from assemblies)"]
        CodeGen["ICodeGenerator<br/>(discovered from assemblies)"]
        Dispatcher["CapabilityDispatcher"]
        RPC["JSON-RPC Server"]

        Packages --> LanguageSupport
        Packages --> CodeGen
        LanguageSupport --> RPC
        CodeGen --> RPC
        Dispatcher --> RPC
    end

    subgraph Guest["Guest Process"]
        direction TB
        UserCode["User Code<br/>(apphost.ts)"]
        SDK["Generated SDK<br/>(.modules/aspire.js)"]
        ATSClient["ATS Client"]
        UserCode --> SDK --> ATSClient
    end

    CLI -->|"getRuntimeSpec<br/>scaffoldAppHost<br/>generateCode"| RPC
    RPC -->|"RuntimeSpec"| GuestRuntime
    GuestRuntime -->|"npm/bun/python<br/>(from RuntimeSpec)"| Guest
    ATSClient <-->|"invokeCapability<br/>(Unix Socket)"| RPC
```

**Key insight:** The CLI is evolving toward being a pure orchestrator, not a language expert. Its core responsibilities are:
- **AppHost orchestration** - Managing the server and guest process lifecycle
- **Template management** - Requesting scaffolding from the server via RPC
- **Integration acquisition** - Adding NuGet packages and triggering SDK regeneration
- **MCP server** - Exposing tools for AI assistants to interact with the running app

All language-specific logic (scaffolding templates, runtime commands, detection) lives in server-side `ILanguageSupport` implementations, discovered from loaded NuGet packages.

**Startup Sequence:**

1. CLI scaffolds an AppHost server project in `$TMPDIR/.aspire/hosts/<hash>/`
2. CLI adds required hosting packages (Redis, Postgres, etc.)
3. CLI builds the .NET project
4. Code generation scans assemblies for `[AspireExport]` and generates SDK
5. CLI starts the AppHost server with socket path
6. CLI starts the Guest runtime
7. Guest connects and invokes capabilities

### Guest Runtime Contract

The CLI passes the connection path via **environment variable**:

| Environment Variable | Description | Example |
|---------------------|-------------|---------|
| `REMOTE_APP_HOST_SOCKET_PATH` | Unix socket path (or named pipe name on Windows) | `/tmp/aspire/host.sock` |

**Security:** The socket is protected by file system permissions (Unix: `0600`, Windows: current user ACL). Only processes running as the same user can connect.

**Guest startup requirements:**
1. Read `REMOTE_APP_HOST_SOCKET_PATH` from environment
2. Connect to the Unix socket (or `\\.\pipe\{name}` on Windows)
3. Invoke capabilities via JSON-RPC
4. Exit cleanly when the connection closes (server shutdown)

```mermaid
sequenceDiagram
    participant CLI as Aspire CLI
    participant Host as AppHost Server
    participant Guest as Guest (TypeScript)

    CLI->>Host: Start (socket path)
    CLI->>Guest: Start (socket path via env var)

    Guest->>Host: invokeCapability("Aspire.Hosting/createBuilder", {})
    Host-->>Guest: { $handle: "1", $type: "Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder" }

    Guest->>Host: invokeCapability("Aspire.Hosting.Redis/addRedis", {builder, name})
    Host-->>Guest: { $handle: "2", $type: "Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource" }

    Guest->>Host: invokeCapability("Aspire.Hosting/build", {builder})
    Host-->>Guest: { $handle: "3", $type: "Aspire.Hosting/Aspire.Hosting.DistributedApplication" }

    Guest->>Host: invokeCapability("Aspire.Hosting/run", {app})
    Host-->>Guest: Started (orchestration running)
```

---

## Aspire Type System (ATS)

ATS is the central type system that bridges .NET and guest languages. Every type crossing the boundary has an **ATS type ID** that serves as its portable identity.

### Type IDs

Type IDs are portable identifiers for .NET types. They are automatically derived from the assembly name and full type name (including namespace).

**Format:** `{AssemblyName}/{FullTypeName}`

| ATS Type ID | .NET Type |
|-------------|-----------|
| `Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder` | `IDistributedApplicationBuilder` |
| `Aspire.Hosting/Aspire.Hosting.DistributedApplication` | `DistributedApplication` |
| `Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext` | `DistributedApplicationExecutionContext` |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource` | `IResource` |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment` | `IResourceWithEnvironment` |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource` | `ContainerResource` |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource` | `ExecutableResource` |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference` | `EndpointReference` |

Declared with `[AspireExport]`:

```csharp
// On a type you own - type ID is derived automatically
// namespace Aspire.Hosting.ApplicationModel, assembly Aspire.Hosting.Redis
[AspireExport]
public class RedisResource : ContainerResource { }
// Type ID = Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource

// At assembly level for types you don't own
[assembly: AspireExport(typeof(IDistributedApplicationBuilder))]
// Type ID = Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder
```

Assembly-level exports are the mechanism for exposing framework or third-party types that live in a different assembly than the capabilities that use them. Even in that case, the ATS type ID still comes from the exported CLR type's assembly and full type name, not from the assembly that declares the attribute.

### Type Categories

ATS categorizes types for serialization and code generation using `AtsTypeCategory`:

| Category | Description | Serialization |
|----------|-------------|---------------|
| `Primitive` | Built-in scalar types | JSON primitive (string, number, boolean) |
| `Enum` | .NET enum types | String (enum member name) |
| `Handle` | Opaque object references | `{ "$handle": "42", "$type": "..." }` |
| `Dto` | Data transfer objects with `[AspireDto]` | JSON object |
| `Callback` | Guest-provided delegate functions | String (callback ID) |
| `Array` | Immutable arrays/readonly collections (for example, `IReadOnlyList<T>`/`IEnumerable<T>`) | JSON array (copied by value) |
| `List` | Mutable `List<T>` | Handle when exposed as property; JSON array when passed as parameter |
| `Dict` | Mutable `Dictionary<K,V>` | Handle when exposed as property; JSON object when passed as parameter |

### Type System Notes (Compatibility and Semantics)

This section captures expectations that keep guest runtimes stable and APIs evolvable.

- **Type identity stability:** ATS type IDs are derived from `{AssemblyName}/{FullTypeName}`. Renames or namespace moves are breaking changes. Prefer additive changes or introduce new types.
- **Nullability and optional parameters:** Use nullable types or optional parameters to express optionality. Guests should treat missing values and explicit `null` as equivalent only when the parameter is declared nullable.
- **DTO evolution:** DTOs are structural JSON objects. Additive changes (new optional fields) are safer than renames/removals. Guests should ignore unknown DTO fields to remain forward-compatible.
- **Enum evolution:** New enum members are non-breaking for hosts, but older guests may not recognize them. Guests should handle unknown enum strings defensively.
- **Capabilities and versioning:** Capability IDs are stable and globally unique. Rename by adding a new capability ID and keeping the old one for compatibility.
- **Handle lifetime:** Handles are valid only while the host process runs. Guests must handle `HANDLE_NOT_FOUND` when a handle is stale or disposed.
- **Callbacks and errors:** Exceptions thrown by guest callbacks surface as `CALLBACK_ERROR` on the host. Guests should treat callback results like normal capability results.
- **Concurrency:** JSON-RPC responses can arrive out of order. Guests should not assume request/response ordering beyond matching `id`.

### Type Exporting and Polymorphism Flattening

ATS doesn't have a closed set of primitive types. Instead, any .NET type can be exported using `[AspireExport]`, and the scanner automatically expands capabilities based on type relationships.

#### How Exporting Works

When a type is marked with `[AspireExport]`, the scanner:

1. **Registers the type** with its ATS type ID (`{AssemblyName}/{FullTypeName}`)
2. **Scans extension methods** that target the type or its interfaces
3. **Expands interface relationships** so each concrete type gets all applicable capabilities

```csharp
// RedisResource is in Aspire.Hosting.Redis assembly
[AspireExport]
public class RedisResource : ContainerResource, IResourceWithConnectionString { }

// This extension method targets IResourceWithEnvironment
public static class ResourceExtensions
{
    [AspireExport("withEnvironment")]
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, ...)
        where T : IResourceWithEnvironment { }
}
```

Because `RedisResource` implements `IResourceWithEnvironment` (via `ContainerResource`), the scanner adds `withEnvironment` to `RedisResource`'s capability list.

#### Cross-Assembly Type Exports

Use `[assembly: AspireExport(typeof(T))]` when a capability assembly needs to expose a type defined in another assembly:

```csharp
// In Aspire.Hosting/Ats/AtsTypeMappings.cs
[assembly: AspireExport(typeof(IConfiguration))]
[assembly: AspireExport(typeof(IHostEnvironment), ExposeProperties = true)]
```

At startup, the remote host creates one ATS context by scanning all loaded assemblies together. That shared scan is what lets one assembly export a type while another assembly contributes capabilities that consume or return it.

- **Type identity still comes from the exported CLR type.** For example, `[assembly: AspireExport(typeof(IConfiguration))]` produces the ATS type ID `Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration`, even though the attribute is declared in `Aspire.Hosting`.
- **Exported handle types are merged by ATS type ID across the full scan.** Re-exporting the same type from multiple assemblies does not create multiple ATS handle types.
- **Capabilities are merged from all scanned assemblies, but capability IDs must remain unique.** If two assemblies export the same capability ID, scanning emits a diagnostic error.
- **Cross-assembly resolution is scan-order independent.** If one assembly exports a type and another references it, the scanner resolves it after collecting the complete type universe from every scanned assembly.

This is how Aspire's core hosting assembly exports framework types such as `IConfiguration`, `IConfigurationSection`, and `IHostEnvironment` while keeping them in the same flattened capability model as types exported directly from integration packages.

#### Flattening in Action

A concrete type gets capabilities from:
- **Direct exports** on the type itself
- **Interface implementations** - capabilities targeting any interface it implements
- **Base class** - capabilities inherited from parent classes

```text
RedisResource capabilities:
├── Aspire.Hosting.Redis/addRedis          (direct - creates Redis)
├── Aspire.Hosting.Redis/withPersistence   (direct - Redis-specific)
├── Aspire.Hosting/withEnvironment         (via IResourceWithEnvironment)
├── Aspire.Hosting/withEndpoint            (via IResourceWithEndpoints)
├── Aspire.Hosting/waitFor                 (via IResourceWithWaitSupport)
└── Aspire.Hosting/getConnectionString     (via IResourceWithConnectionString)
```

Guest languages see a flat list—no need to understand .NET's type hierarchy.

#### Core Types

Some commonly used exported types:

| Type | Purpose |
|------|---------|
| `IDistributedApplicationBuilder` | Entry point for building the app |
| `DistributedApplication` | Built application, ready to run |
| `DistributedApplicationExecutionContext` | Runtime context (run vs publish mode) |
| `EndpointReference` | Reference to a network endpoint |
| `ReferenceExpression` | Dynamic expression with embedded references |

These aren't special—they're just types that happen to be exported. Integration packages export their own types (e.g., `RedisResource`, `PostgresResource`).

### Capabilities

Capabilities are named operations with globally unique IDs. They replace direct method invocation.

**Format:** `{Package}/{MethodName}`

- Package = NuGet package name (derived from assembly)
- MethodName = specified in `[AspireExport]` attribute

| Capability ID | Description |
|---------------|-------------|
| `Aspire.Hosting/createBuilder` | Create a DistributedApplicationBuilder |
| `Aspire.Hosting/addContainer` | Add a container resource |
| `Aspire.Hosting/withEnvironment` | Set an environment variable |
| `Aspire.Hosting/build` | Build the application |
| `Aspire.Hosting/run` | Run the application |
| `Aspire.Hosting.Redis/addRedis` | Add a Redis resource |

Declared on extension methods:

```csharp
[AspireExport("addRedis", Description = "Adds a Redis resource")]
public static IResourceBuilder<RedisResource> AddRedis(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    int? port = null)
{
    // Existing implementation - unchanged
}
// Scanner computes capability ID: Aspire.Hosting.Redis/addRedis
```

### Handles

Handles are opaque references to .NET objects. They carry an ATS type ID for identification.

**Format:** Handle ID is an instance number. Type is provided separately.

```json
{
    "$handle": "42",
    "$type": "Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource"
}
```

**Flat Model:** ATS uses a flat type model where capabilities are grouped by their first parameter type:
- Methods with `IDistributedApplicationBuilder` as first param → on `DistributedApplicationBuilder`
- Methods with `IResourceBuilder<T>` as first param → on all resource builders
- Methods with `DistributedApplication` as first param → on `DistributedApplication`

Type validation happens at runtime when the CLR invokes the method. Invalid type combinations produce `TYPE_MISMATCH` errors.

### DTOs

Data transfer objects for passing structured data. Must be marked `[AspireDto]`:

```csharp
[AspireDto]
public sealed class ContainerMountOptions
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool IsReadOnly { get; init; }
}
```

**Strict Enforcement:**

| Direction | `[AspireDto]` Type | Non-`[AspireDto]` Type |
|-----------|-------------------|----------------------|
| Input (JSON → .NET) | Deserialized | **Error** |
| Output (.NET → JSON) | Serialized | Marshaled as Handle |

### Enums

.NET enum types are fully supported and generate typed enums in guest languages. Enums serialize as their string member names:

```csharp
// Define enum in .NET
public enum ContainerLifetime
{
    Session,
    Persistent
}

// Use in capability
[AspireExport("withLifetime")]
public static IResourceBuilder<T> WithLifetime<T>(
    this IResourceBuilder<T> builder,
    ContainerLifetime lifetime) where T : ContainerResource
```

Generated TypeScript:

```typescript
export enum ContainerLifetime {
    Session = "Session",
    Persistent = "Persistent",
}

// Usage - fully typed
const cache = await builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);
```

The scanner automatically discovers enum types used in capability parameters and return types, adding them to `AtsContext.EnumTypes` for code generation.

### Callbacks

Guest-provided functions the host can invoke during execution. Callbacks are automatically inferred from delegate parameters:

```csharp
[AspireExport("withEnvironmentCallback")]
public static IResourceBuilder<T> WithEnvironmentCallback<T>(
    this IResourceBuilder<T> resource,
    Func<EnvironmentCallbackContext, Task> callback)  // Delegate = callback
    where T : IResourceWithEnvironment
// Scanner computes: Aspire.Hosting/withEnvironmentCallback
```

Callbacks are passed as string IDs **when invoking a capability that accepts a delegate** (e.g., `withEnvironmentCallback`). Example capability args:

```json
{
    "resource": {"$handle": "1", "$type": "Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource"},
    "callback": "callback_1_1234567890"
}
```

When the host later invokes that callback, arguments use **positional keys** (`p0`, `p1`, ...), not parameter names. See [invokeCallback](#invokecallback-host--guest) for the wire format.

`CancellationToken` is special in generated TypeScript:

- **User-authored inputs** accept `AbortSignal | CancellationToken`
- **Host-provided values** are materialized as `CancellationToken`

This keeps local TypeScript ergonomic while preserving remote token handles that
need to round-trip back through the transport layer.

#### Callback Handle Wrapping (TypeScript)

When the host invokes a callback, handle parameters are automatically converted to typed wrapper classes:

1. Host sends: `{"p0": {"$handle": "...", "$type": "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext"}}`
2. SDK looks up type in **handle wrapper registry**
3. SDK creates wrapper: `new EnvironmentCallbackContext(handle, client)`
4. User callback receives typed instance

Generated code registers factories for each wrapper class at module load:

```typescript
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext',
    (handle, client) => new EnvironmentCallbackContext(handle as EnvironmentCallbackContextHandle, client));
```

This enables callbacks to receive properly typed objects:

```typescript
.withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
    // ctx is a typed wrapper, not a raw Handle
    await ctx.environmentVariables.set("KEY", "value");
})
```

### Context Types

Objects passed to callbacks with auto-exposed properties. The type ID is automatically derived from `{AssemblyName}/{FullTypeName}`:

```csharp
// namespace Aspire.Hosting.ApplicationModel
[AspireExport(ExposeProperties = true)]  // Type ID = Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext
public class EnvironmentCallbackContext
{
    // Auto-exposed as "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext.environmentVariables"
    public Dictionary<string, object> EnvironmentVariables { get; }

    // Auto-exposed as "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext.executionContext"
    public DistributedApplicationExecutionContext ExecutionContext { get; }
}
```

### Collection Wrappers (TypeScript)

For mutable .NET collections exposed to TypeScript, the SDK provides wrapper classes with capability-based operations.

#### `AspireDict<K, V>`

Wrapper for `IDictionary<K, V>`:

```typescript
const envVars = ctx.environmentVariables;  // AspireDict<string, string | ReferenceExpression>
await envVars.set("KEY", "value");
await envVars.get("KEY");
await envVars.containsKey("KEY");
await envVars.remove("KEY");
await envVars.keys();
await envVars.count();
```

**Lazy Handle Resolution:** When a dictionary is accessed via a context property, the wrapper lazily fetches the actual dictionary handle on first operation:

```typescript
// Context has handle to EnvironmentCallbackContext
// First operation calls getter capability to get dictionary handle
await ctx.environmentVariables.set("KEY", "value");
```

#### `AspireList<T>`

Wrapper for `IList<T>`:

```typescript
await list.add(item);
await list.get(index);
await list.count();
await list.removeAt(index);
await list.toArray();
```

### Reference Expressions

Dynamic values that reference endpoints, parameters, and other providers:

```json
{
    "$expr": {
        "format": "redis://{0}:{1}",
        "valueProviders": [
            { "$handle": "1", "$type": "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference" },
            "6379"
        ]
    }
}
```

---

## JSON-RPC Protocol

Guest and host communicate via JSON-RPC 2.0 over Unix domain sockets (named pipes on Windows).

### Wire Format

Messages use the LSP-style header format (same as vscode-jsonrpc):

```text
Content-Length: 123\r\n
\r\n
{"jsonrpc":"2.0","id":1,"method":"ping","params":[]}
```

**Framing rules:**
- `Content-Length` is the **byte count** of the JSON body (not character count)
- Body is encoded as **UTF-8**
- Headers end with `\r\n\r\n` (CRLF CRLF)
- `Content-Type` header is optional (defaults to `application/vscode-jsonrpc; charset=utf-8`)

**Implementation note:** Use the `vscode-jsonrpc` npm package or equivalent LSP transport library.

### Methods

| Method | Direction | Purpose |
|--------|-----------|---------|
| `ping` | Guest → Host | Health check |
| `invokeCapability` | Guest → Host | Call a capability |
| `cancelToken` | Guest → Host | Cancel a cancellation token |
| `invokeCallback` | Host → Guest | Invoke guest callback |

> **Note:** There is no `getCapabilities` method. Capabilities are discovered at code-generation time by scanning `[AspireExport]` attributes, not at runtime. The generated SDK contains all available capabilities statically typed.

### ping

Simple health check.

```json
// Request
{"jsonrpc":"2.0","id":1,"method":"ping","params":[]}

// Response
{"jsonrpc":"2.0","id":1,"result":"pong"}
```

### invokeCapability

```json
// Request
{"jsonrpc":"2.0","id":2,"method":"invokeCapability","params":[
    "Aspire.Hosting.Redis/addRedis",
    {
        "builder": {"$handle": "1", "$type": "Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder"},
        "name": "cache",
        "port": 6379
    }
]}

// Response
{"jsonrpc":"2.0","id":2,"result":{
    "$handle": "2",
    "$type": "Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource"
}}
```

### invokeCallback (Host → Guest)

Callback arguments are serialized using **positional keys** (`p0`, `p1`, `p2`, ...), not parameter names. This ensures consistent behavior across all language runtimes.

```json
// Request (single parameter callback)
{"jsonrpc":"2.0","id":100,"method":"invokeCallback","params":[
    "callback_1_1234567890",
    {"p0": {"$handle": "5", "$type": "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext"}}
]}

// Request (multi-parameter callback)
{"jsonrpc":"2.0","id":101,"method":"invokeCallback","params":[
    "callback_2_1234567890",
    {"p0": "hello", "p1": 42, "p2": true}
]}

// Response
{"jsonrpc":"2.0","id":100,"result":null}
```

**Argument extraction:** Guest runtimes must iterate `p0`, `p1`, `p2`, ... in order to reconstruct the parameter list.

### Cancellation

Cancellation tokens enable cooperative cancellation of long-running callbacks.

**Token flow:**
1. Host creates token when invoking a callback that accepts `CancellationToken`
2. If `CancellationToken` is part of the callback signature, the host passes the token ID in the matching positional `pN` argument
3. Host also includes the same token ID as `$cancellationToken` metadata for transport-level cancellation wiring
4. Guest can cancel by calling `cancelToken` RPC method
5. Host disposes token when callback completes

```json
// Callback request with cancellation token
{"jsonrpc":"2.0","id":100,"method":"invokeCallback","params":[
    "callback_1_1234567890",
    {
        "p0": {"$handle": "5", "$type": "Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext"},
        "p1": "ct_a1b2c3d4-e5f6-7890-abcd-ef1234567890",
        "$cancellationToken": "ct_a1b2c3d4-e5f6-7890-abcd-ef1234567890"
    }
]}

// Guest cancels the operation
{"jsonrpc":"2.0","id":101,"method":"cancelToken","params":["ct_a1b2c3d4-e5f6-7890-abcd-ef1234567890"]}

// Response (true if token found and cancelled)
{"jsonrpc":"2.0","id":101,"result":true}
```

**Token ID format:** Opaque string (treat as identifier, do not parse)

### Error Responses

> **Non-standard format:** ATS uses `result.$error` instead of the JSON-RPC `error` field. This simplifies client error handling—check for `$error` in every result.

```json
{
    "jsonrpc": "2.0",
    "id": 1,
    "result": {
        "$error": {
            "code": "CAPABILITY_NOT_FOUND",
            "message": "Unknown capability: Contoso.Aspire/bar",
            "capability": "Contoso.Aspire/bar"
        }
    }
}
```

**Client handling:**
1. Check if `result.$error` exists
2. If present, **throw an exception** with the error details (code, message)
3. If absent, process `result` as the success value

**Note:** ATS does not return partial results—`$error` means complete failure. The error object is always the only content when present.

| Code | Description |
|------|-------------|
| `CAPABILITY_NOT_FOUND` | Unknown capability ID |
| `HANDLE_NOT_FOUND` | Handle doesn't exist |
| `TYPE_MISMATCH` | Handle type incompatible |
| `INVALID_ARGUMENT` | Missing/invalid argument |
| `CALLBACK_ERROR` | Callback invocation failed |
| `INTERNAL_ERROR` | Unexpected error |

### Reserved Fields

Fields starting with `$` are reserved for ATS protocol metadata:

| Field | Purpose |
|-------|---------|
| `$handle` | Handle instance ID |
| `$type` | ATS type ID |
| `$error` | Error response |
| `$expr` | Reference expression |
| `$cancellationToken` | Cancellation token ID |

**DTO authors must not use `$`-prefixed field names.** The host may misinterpret such fields as protocol metadata.

### Supported Types

**Primitives:**

| .NET Type | JSON Type | Notes |
|-----------|-----------|-------|
| `string` | string | |
| `char` | string | Single character |
| `bool` | boolean | |
| `int` | number | 32-bit, safe in JavaScript |
| `long` | number | 64-bit; **precision loss** in JavaScript for values > 2^53 |
| `float`, `double` | number | IEEE 754 double precision |
| `decimal` | number | **Precision loss** in JavaScript; avoid for currency in guest |
| `DateTime` | string | ISO 8601 |
| `DateTimeOffset` | string | ISO 8601 |
| `TimeSpan` | number | **Total milliseconds** (safe for durations < 285 years) |
| `DateOnly` | string | YYYY-MM-DD |
| `TimeOnly` | string | HH:mm:ss |
| `Guid` | string | |
| `Uri` | string | |
| `enum` | string | Enum name |
| `object` | any | Accepts any supported ATS type |

> **Numeric precision:** JavaScript's `number` type uses IEEE 754 double-precision, which can only represent integers exactly up to 2^53-1 (9,007,199,254,740,991). `long` values exceeding this range and `decimal` values may lose precision. Aspire capabilities avoid exposing such values to guests.

**Complex Types:**

| Type | JSON Shape | When Returned |
|------|------------|---------------|
| Handle | `{ "$handle": "42", "$type": "Assembly/Namespace.Type" }` | Always handle |
| DTO | Plain object (requires `[AspireDto]`) | Copied by value |
| Array/IReadOnlyList/IEnumerable | JSON array | Copied by value |
| `List<T>` | JSON array (parameter) or Handle (return/property) | Handle if returned |
| `Dictionary<K,V>` | JSON object (parameter) or Handle (return/property) | Handle if returned |
| Nullable | Value or `null` | Same as inner type |
| ReferenceExpression | `{ "$expr": { "format": "...", "valueProviders": [...] } }` | Special structure |

**Collection return semantics:** When a capability returns `List<T>` or `Dictionary<K,V>`, it's returned as a **handle** so the caller can mutate it (e.g., `AspireList.add()`, `AspireDict.set()`). Arrays and read-only collections are always copied by value.

**TimeSpan Example:**

```csharp
TimeSpan.FromMinutes(5)  →  300000
TimeSpan.FromSeconds(30) →  30000
```

---

## Code Generation

The CLI generates language-specific SDKs from ATS capabilities.

### When It Runs

- First run (no `.modules/` folder)
- Package hash changed (after `aspire add`)
- Development mode (`ASPIRE_REPO_ROOT` set)

### Process

1. Load assemblies from AppHost server build
2. Scan for `[AspireExport]` attributes using `AtsCapabilityScanner`
3. Expand interface targets to concrete implementations
4. Generate language-specific SDK

### Capability Expansion

Capabilities can target either concrete types or interfaces. For example:

```csharp
// Targets concrete type - no expansion needed
[AspireExport("withPersistence")]
public static IResourceBuilder<RedisResource> WithPersistence(
    this IResourceBuilder<RedisResource> builder, ...)
// → Aspire.Hosting.Redis/withPersistence

// Targets interface - expanded to all implementing types
[AspireExport("withEnvironment")]
public static IResourceBuilder<T> WithEnvironment<T>(
    this IResourceBuilder<T> builder, string name, string value)
    where T : IResourceWithEnvironment
// → Aspire.Hosting/withEnvironment (expands to Redis, Container, Project, ...)
```

The scanner performs **2-pass expansion**:

**Pass 1: Collect type hierarchy**
- For each concrete resource type, collect all implemented interfaces
- `RedisResource` → `[IResourceWithEnvironment, IResourceWithEndpoints, ...]`

**Pass 2: Expand capabilities**
- Capabilities targeting interfaces are expanded to concrete types
- `withEnvironment` (targets `IResourceWithEnvironment`) → expands to `[Redis, Container, Project, ...]`

Each capability has:
- `TargetTypeId` - The declared target (e.g., `Aspire.Hosting/IResourceWithEnvironment`)
- `ExpandedTargetTypeIds` - Pre-computed list of concrete types (e.g., `[Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource, Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource, ...]`)

**Language generator usage:**
- Languages with inheritance (TypeScript, Swift) can use `TargetTypeId` for interface-based generation
- Languages without inheritance (Go, C) use `ExpandedTargetTypeIds` for flat generation

```mermaid
flowchart LR
    A["Assemblies<br/>(with CLR types)"] --> B["AtsCapabilityScanner"]
    B --> C["List&lt;AtsCapabilityInfo&gt;<br/>(pure ATS, no CLR types)"]
    C --> D["Code Generation<br/>(group by ExpandedTargetTypeIds)"]
    C --> E["Runtime<br/>(index by CapabilityId)"]
```

### Output (TypeScript)

```text
.modules/
├── .codegen-hash     # Hash of package references for cache invalidation
├── aspire.ts         # Generated SDK (builder classes, wrapper registrations)
├── base.ts           # Base classes, ReferenceExpression, AspireDict, AspireList
└── transport.ts      # JSON-RPC client, Handle, MarshalledHandle, callbacks
```

| File | Contents |
|------|----------|
| `transport.ts` | `AspireClient`, `Handle`, `MarshalledHandle`, callback registry, `registerHandleWrapper` |
| `base.ts` | `DistributedApplicationBuilderBase`, `ResourceBuilderBase`, `ReferenceExpression`, `refExpr`, `AspireDict`, `AspireList` |
| `aspire.ts` | Generated builders, wrapper classes, `createBuilder()`, handle wrapper registrations |

### Type Mapping

Capabilities are grouped by their (expanded) target type to generate builder classes:

| ATS Type ID | TypeScript Class | Capabilities |
|-------------|------------------|--------------|
| `Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder` | `DistributedApplicationBuilder` | `addRedis`, `addContainer`, `build` |
| `Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource` | `RedisBuilder` | `withPersistence`, `withEnvironment`, ... |
| `Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource` | `ContainerBuilder` | `withBindMount`, `withEnvironment`, ... |
| `Aspire.Hosting/Aspire.Hosting.DistributedApplication` | `DistributedApplication` | `run` |

Primitive type mapping:

| ATS Type | TypeScript |
|----------|------------|
| `string` | `string` |
| `number` | `number` |
| `boolean` | `boolean` |
| `any` | `any` |
| `CancellationToken` | `CancellationToken` for returned/host-provided values; `AbortSignal \| CancellationToken` for generated input parameters |
| `{Assembly}/{Type}` | `Handle<'{Assembly}/{Type}'>` (typed handle alias) |

---

## TypeScript Implementation

### Generated SDK Usage

```typescript
import { createBuilder, refExpr, EnvironmentCallbackContext } from './.modules/aspire.js';

const builder = await createBuilder();

// Add resources using fluent chaining
const cache = await builder
    .addRedis("cache")
    .withRedisCommander();

// Get endpoint for reference expressions
const endpoint = await cache.getEndpoint("tcp");

// Create dynamic connection string using tagged template literal
const redisUrl = refExpr`redis://${endpoint}`;

// Add container with environment callback
const api = await builder
    .addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp")
    .withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
        // Access execution context to check run/publish mode
        const execContext = await ctx.executionContext.get();
        const isRunMode = await execContext.isRunMode.get();

        // Set environment variables using AspireDict
        await ctx.environmentVariables.set("MY_CONSTANT", "hello from TypeScript");
        await ctx.environmentVariables.set("REDIS_URL", redisUrl);
    })
    .waitFor(cache)        // Accepts wrapper types directly
    .withReference(cache);

// Build and run
await builder.build().run();
```

### Cancellation Ergonomics

Generated TypeScript APIs accept either `AbortSignal` or `CancellationToken`
for user-supplied cancellation input.

Use `AbortSignal` when you create cancellation in TypeScript:

```typescript
const controller = new AbortController();
const connectionStringExpression = await db.uriExpression.get();
const connectionString = await connectionStringExpression.getValue(controller.signal);
```

When the host gives you a token through callback context or another generated API,
the generated SDK returns a `CancellationToken` so the remote handle can be passed
through other generated calls without losing identity:

```typescript
const cancellationToken = await context.cancellationToken.get();
const connectionStringExpression = await db.uriExpression.get();
const connectionString = await connectionStringExpression.getValue(cancellationToken);
```

Both shapes are accepted by generated input parameters, so local and remote
cancellation can be mixed naturally in the same AppHost.

### Fluent Async Chaining

Methods return `Thenable` wrappers enabling single-await chains:

```typescript
// Single await for entire chain
const cache = await builder
    .addRedis("cache")
    .withRedisCommander()
    .withDataVolume();

// Build and run in one await
await builder.build().run();
```

### Reference Expressions

```typescript
const redis = await builder.addRedis("cache");
const endpoint = await redis.getEndpoint("tcp");

// Tagged template literal
const connectionString = refExpr`redis://${endpoint}`;

await api.withEnvironment("REDIS_URL", connectionString);
```

### Enum Usage

Enums are generated as TypeScript enums with string values matching the C# member names:

```typescript
import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// Use typed enum values instead of strings
const cache = await builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);

// Generated enum definition in aspire.ts:
// export enum ContainerLifetime {
//     Session = "Session",
//     Persistent = "Persistent",
// }
```

### Type Hierarchy

TypeScript uses three layers to bridge the wire format and user API:

| Layer | Description | Example |
|-------|-------------|---------|
| `MarshalledHandle` | Wire format (plain JSON) | `{ $handle: "...", $type: "..." }` |
| `Handle<T>` | TypeScript class with type safety | `Handle<'Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource'>` |
| Wrapper Classes | User-facing API with methods | `RedisBuilder`, `EndpointReference` |

**MarshalledHandle** is what travels over JSON-RPC:

```typescript
interface MarshalledHandle {
    $handle: string;  // "42" (instance number)
    $type: string;    // "Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisResource"
}
```

`Handle<T>` wraps marshalled data with type safety and serialization:

```typescript
class Handle<T extends string> {
    get $handle(): string;
    get $type(): T;
    toJSON(): MarshalledHandle;  // For serialization
}
```

**Wrapper Classes** provide the user API and must include `toJSON()` for use in reference expressions:

```typescript
class EndpointReference {
    constructor(handle: Handle, client: AspireClient) { ... }
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    // Property accessors
    url = { get: async () => ... };
    host = { get: async () => ... };
}
```

---

## CLI Integration

The CLI is responsible for:
- **AppHost orchestration** - Starting/stopping the server and guest processes
- **Template management** - Scaffolding projects via RPC
- **Integration acquisition** - Adding packages via `aspire add`
- **MCP server** - Exposing tools for AI assistants to interact with the running app

The CLI is **not** responsible for language-specific logic. A single `GuestAppHostProject` handles all guest languages by delegating to the server.

> **Note:** The .NET AppHost (`DotNetAppHostProject`) currently has its logic in the CLI. We plan to migrate it to the guest model, where .NET becomes just another language with its own `ILanguageSupport` and `RuntimeSpec`. This will unify the architecture and allow the CLI to be truly language-agnostic.

```mermaid
classDiagram
    class IAppHostProject {
        <<interface>>
        +LanguageId: string
        +DisplayName: string
        +DetectionPatterns: string[]
        +CanHandle(FileInfo): bool
        +ScaffoldAsync()
        +RunAsync()
        +PublishAsync()
        +AddPackageAsync()
    }

    class GuestAppHostProject {
        -ILanguageDiscovery _languageDiscovery
        -GuestRuntime _guestRuntime
        +ResolveLanguageAsync()
    }

    class GuestRuntime {
        -RuntimeSpec _spec
        +InstallDependenciesAsync()
        +RunAsync()
        +PublishAsync()
    }

    IAppHostProject <|.. DotNetAppHostProject
    IAppHostProject <|.. GuestAppHostProject
    GuestAppHostProject --> GuestRuntime
```

**How it works:**

1. `GuestAppHostProject` detects language from `settings.json` or file patterns
2. Asks the server for `RuntimeSpec` via `getRuntimeSpec` RPC
3. `GuestRuntime` interprets the spec to execute commands

| Command | Method | Description |
|---------|--------|-------------|
| `aspire init` | `ScaffoldAsync` | Calls `scaffoldAppHost` RPC, writes files |
| `aspire new` | `ScaffoldAsync` | Create new project with apphost |
| `aspire run` | `RunAsync` | Gets `RuntimeSpec`, executes via `GuestRuntime` |
| `aspire publish` | `PublishAsync` | Gets `RuntimeSpec`, executes in publish mode |
| `aspire add` | `AddPackageAsync` | Add integration package, regenerate SDK |

---

## Configuration

### .aspire/settings.json

Configuration for polyglot app hosts:

```json
{
  "appHostPath": "apphost.ts",
  "language": "typescript",
  "channel": "stable",
  "packages": {
    "Aspire.Hosting.Redis": "9.0.0",
    "Aspire.Hosting.PostgreSQL": "9.0.0"
  }
}
```

| Field | Description |
|-------|-------------|
| `appHostPath` | Path to the apphost file (relative to settings.json) |
| `language` | Language identifier (e.g., `typescript`, `python`). Auto-detected on first run and persisted. |
| `channel` | NuGet channel for package resolution (`stable`, `preview`, etc.) |
| `packages` | Package references added via `aspire add` |

**Language persistence:** On first `aspire run`, if `language` is not set, the CLI detects it from file patterns and saves it to `settings.json`. Subsequent runs use the persisted value.

### apphost.run.json

Launch settings:

```json
{
  "profiles": {
    "https": {
      "applicationUrl": "https://localhost:17193;http://localhost:15069",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

---

## Adding New Guest Languages

Adding a new language requires two components: a **server-side NuGet package** (language support + code generation) and a **CLI registration**.

### Step 1: Create Server-Side NuGet Package

Create a package like `Aspire.Hosting.CodeGeneration.Python` with two implementations:

#### ILanguageSupport (Scaffolding + RuntimeSpec)

```csharp
public interface ILanguageSupport
{
    string Language { get; }
    Dictionary<string, string> Scaffold(ScaffoldRequest request);
    DetectionResult Detect(string directoryPath);
    RuntimeSpec GetRuntimeSpec();
}
```

Example implementation:

```csharp
public sealed class PythonLanguageSupport : ILanguageSupport
{
    public string Language => "python";

    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        return new Dictionary<string, string>
        {
            ["apphost.py"] = "# Aspire Python AppHost\n...",
            ["requirements.txt"] = "aspire-hosting\n"
        };
    }

    public DetectionResult Detect(string directoryPath)
    {
        var appHostPath = Path.Combine(directoryPath, "apphost.py");
        return File.Exists(appHostPath)
            ? DetectionResult.Found("python", "apphost.py")
            : DetectionResult.NotFound;
    }

    public RuntimeSpec GetRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = "python",
            DisplayName = "Python",
            CodeGenLanguage = "Python",
            DetectionPatterns = ["apphost.py"],
            InstallDependencies = new CommandSpec
            {
                Command = "pip",
                Args = ["install", "-r", "requirements.txt"]
            },
            Execute = new CommandSpec
            {
                Command = "python",
                Args = ["{appHostFile}"]
            }
        };
    }
}
```

#### ICodeGenerator (SDK Generation)

```csharp
public interface ICodeGenerator
{
    string Language { get; }
    Dictionary<string, string> GenerateDistributedApplication(AtsContext context);
}
```

The generator receives an `AtsContext` containing all scanned capabilities, types, DTOs, and enums. It produces language-specific SDK files.

### Step 2: Register in CLI

> **Note:** This step is temporary while we determine the language discovery story. In the future, languages may be discovered automatically from NuGet packages or configuration files.

Add the language to `DefaultLanguageDiscovery` in `Aspire.Cli`:

```csharp
private static readonly LanguageInfo[] s_allLanguages =
[
    new LanguageInfo(
        LanguageId: new LanguageId(KnownLanguageId.CSharp),
        DisplayName: KnownLanguageId.CSharpDisplayName,
        PackageName: "", // C# doesn't need a code generation package
        DetectionPatterns: ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"],
        CodeGenerator: "", // C# doesn't use code generation
        AppHostFileName: null), // C# uses .csproj
    new LanguageInfo(
        LanguageId: new LanguageId("typescript/nodejs"),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript", // Matches ICodeGenerator.Language
        AppHostFileName: "apphost.ts"),
    // Add new languages here:
    new LanguageInfo(
        LanguageId: new LanguageId("python"),
        DisplayName: "Python",
        PackageName: "Aspire.Hosting.CodeGeneration.Python",
        DetectionPatterns: ["apphost.py"],
        CodeGenerator: "Python",
        AppHostFileName: "apphost.py"),
];
```

### RuntimeSpec Model

The `RuntimeSpec` tells the CLI how to execute the guest language:

```csharp
public sealed class RuntimeSpec
{
    public required string Language { get; init; }        // "python"
    public required string DisplayName { get; init; }     // "Python"
    public required string CodeGenLanguage { get; init; } // Links to ICodeGenerator
    public required string[] DetectionPatterns { get; init; }
    public CommandSpec? InstallDependencies { get; init; }
    public required CommandSpec Execute { get; init; }
    public CommandSpec? WatchExecute { get; init; }       // For hot reload
    public CommandSpec? PublishExecute { get; init; }
}

public sealed class CommandSpec
{
    public required string Command { get; init; }         // "python", "npm", etc.
    public required string[] Args { get; init; }          // Supports {appHostFile} placeholder
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
}
```

### How Discovery Works

1. **Server loads packages** - NuGet packages are loaded into the AppHost server
2. **DI discovers implementations** - `ILanguageSupport` and `ICodeGenerator` are discovered via reflection/DI
3. **RPC exposes them** - `getRuntimeSpec`, `scaffoldAppHost`, `generateCode` RPCs delegate to discovered implementations
4. **CLI is language-agnostic** - `GuestRuntime` interprets any `RuntimeSpec`

### Reusable Infrastructure

| Component | Project | Purpose |
|-----------|---------|---------|
| JSON-RPC Server | `Aspire.Hosting` | Handles all RPC |
| Capability Dispatcher | `Aspire.Hosting` | Routes `invokeCapability` |
| Handle Registry | `Aspire.Hosting` | Object lifecycle |
| Capability Scanner | `Aspire.Hosting` | Discovers `[AspireExport]` |
| GuestRuntime | `Aspire.Cli` | Interprets `RuntimeSpec` |

### AtsContext for Code Generation

The generator receives:

```csharp
public sealed class AtsContext
{
    public IReadOnlyList<AtsCapabilityInfo> Capabilities { get; init; }
    public IReadOnlyList<AtsTypeInfo> HandleTypes { get; init; }
    public IReadOnlyList<AtsDtoTypeInfo> DtoTypes { get; init; }
    public IReadOnlyList<AtsEnumTypeInfo> EnumTypes { get; init; }
    public IReadOnlyList<AtsDiagnostic> Diagnostics { get; init; }
}
```

**AtsCapabilityInfo** contains:
- `CapabilityId` - Unique ID (e.g., `Aspire.Hosting.Redis/addRedis`)
- `MethodName` - Method name (e.g., `addRedis`)
- `TargetTypeId` - Original declared target (e.g., `Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder`)
- `ExpandedTargetTypeIds` - Concrete types for interface targets
- `ReturnType` - Return type reference with category
- `Parameters` - List of parameter info
- `Description` - Documentation

**AtsEnumTypeInfo** contains:
- `TypeId` - Enum type ID
- `Name` - Simple enum name (e.g., `ContainerLifetime`)
- `Values` - List of enum member names

**Generation steps:**

1. Group capabilities by target type
2. Generate builder classes with methods for each capability
3. Generate DTO classes, enums, handle wrappers
4. Implement JSON-RPC client for the language

---

## Local Development Workflow

This section explains how to develop and test custom language SDKs using a local clone of the Aspire repository.

### Prerequisites

1. Clone the Aspire repository:

   ```bash
   git clone https://github.com/microsoft/aspire.git
   cd aspire
   ```

2. Build the repository:

   ```bash
   ./build.sh   # macOS/Linux
   ./build.cmd  # Windows
   ```

### Polyglot Support

Polyglot support is enabled by default. The `--language` option is available on `aspire init` and `aspire new`, and the CLI can detect and run non-.NET AppHosts without additional configuration.

### Setting Up Local Development Mode

Set the `ASPIRE_REPO_ROOT` environment variable to point to your local Aspire clone. This enables **development mode**, which:

- Uses **project references** instead of NuGet package references for the AppHost server
- **Forces code regeneration** on every run (bypasses the package hash cache)
- Allows you to immediately test changes without publishing packages

```bash
# macOS/Linux
export ASPIRE_REPO_ROOT="/path/to/aspire"

# Windows (PowerShell)
$env:ASPIRE_REPO_ROOT = "D:\aspire"

# Windows (Command Prompt)
set ASPIRE_REPO_ROOT=D:\aspire
```

### Scaffolding a New App

Use `dotnet run` to execute the CLI directly from source:

```bash
# Create a new Python app
dotnet run --project /path/to/aspire/src/Aspire.Cli/Aspire.Cli.csproj -- init --language python

# Create a new TypeScript app
dotnet run --project /path/to/aspire/src/Aspire.Cli/Aspire.Cli.csproj -- init --language typescript
```

The `-l` (or `--language`) flag specifies the target language for scaffolding.

### Running the App

```bash
# Run the app (standard mode)
dotnet run --project /path/to/aspire/src/Aspire.Cli/Aspire.Cli.csproj -- run

# Run with debug output
dotnet run --project /path/to/aspire/src/Aspire.Cli/Aspire.Cli.csproj -- run -d
```

The `-d` (or `--debug`) flag enables additional diagnostic output, useful when developing and troubleshooting language support.

### Development Workflow Tips

1. **Rapid iteration**: With `ASPIRE_REPO_ROOT` set, the generated SDK in `.modules/` is regenerated on each run, so changes to code generation logic are immediately reflected.

2. **Testing code generators**: Modify your `ICodeGenerator` implementation, then run `aspire run` in a test app—the new generated code will be produced automatically.

3. **Testing language support**: Modify your `ILanguageSupport` implementation, then use `aspire init --language <language>` to test scaffolding or `aspire run` to test detection and execution.

4. **Inspecting generated code**: Check the `.modules/` folder in your test app to see the generated SDK files and verify they match your expectations.

### Quick Reference

| Task | Command |
|------|---------|
| Scaffold Python app | `dotnet run --project $ASPIRE_REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj -- init --language python` |
| Scaffold TypeScript app | `dotnet run --project $ASPIRE_REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj -- init --language typescript` |
| Run app | `dotnet run --project $ASPIRE_REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj -- run` |
| Run with debug output | `dotnet run --project $ASPIRE_REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj -- run -d` |
| Add integration | `dotnet run --project $ASPIRE_REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj -- add` |

---

## Security

Both guest and host run locally on the same machine, started by the CLI. This is **not** remote execution.

### Protections

| Protection | Description |
|------------|-------------|
| **Capability allowlist** | Only `[AspireExport]` methods callable |
| **Runtime type validation** | CLR validates types at invocation time |
| **DTO enforcement** | Only `[AspireDto]` types serialized |
| **Socket permissions** | Unix socket owner-only (0600), Windows named pipe current-user ACL |

### What's NOT Exposed

- Arbitrary .NET reflection
- File system access
- Process spawning
- Network operations
- Any type without explicit `[AspireExport]`

---

## Future: Bundled .NET Runtime

> **Note:** The Aspire CLI will be bundled with enough of the .NET toolchain to make it possible to use NuGet to acquire and resolve packages and execute the RPC server runtime. This is not the case yet—it's a future goal.
>
> Currently, integrations are loaded into the RPC server dynamically, so the .NET runtime is still required to be installed separately. In the future, the .NET runtime will be bundled with the CLI, making it a fully self-contained tool that doesn't require a separate .NET installation for polyglot scenarios.
