# Aspire.Hosting.RemoteHost

This package provides the remote host server for polyglot Aspire AppHosts. It enables non-.NET languages (like TypeScript) to define and run Aspire applications by communicating with a .NET process via JSON-RPC over Unix domain sockets.

## Overview

The RemoteHost acts as a bridge between polyglot AppHost projects and the Aspire hosting infrastructure. It:

- Exposes Aspire capabilities via JSON-RPC over a Unix domain socket
- Scans assemblies for `[AspireExport]` attributes to discover available capabilities
- Manages object lifecycle with a handle-based registry system
- Supports bidirectional communication for callbacks (e.g., environment configuration lambdas)

## Architecture

```text
┌─────────────────────────────┐
│   TypeScript/JS AppHost    │
│   (or other language)       │
└─────────────┬───────────────┘
              │ JSON-RPC over Unix Socket
              ▼
┌─────────────────────────────┐
│   RemoteHostServer          │
│   ├── JsonRpcServer         │
│   ├── CapabilityDispatcher  │
│   ├── HandleRegistry        │
│   └── AtsMarshaller         │
└─────────────┬───────────────┘
              │
              ▼
┌─────────────────────────────┐
│   Aspire Hosting            │
│   (DistributedApplication)  │
└─────────────────────────────┘
```

## Components

### RemoteHostServer

The main entry point. Call `RemoteHostServer.RunAsync(args)` to start the server:

```csharp
// The server reads AtsAssemblies from appsettings.json
await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args);
```

The server reads the list of assemblies to scan from `appsettings.json`:

```json
{
  "AtsAssemblies": [
    "Aspire.Hosting",
    "Aspire.Hosting.Redis",
    "Aspire.Hosting.PostgreSQL"
  ]
}
```

Alternatively, you can pass assemblies explicitly:

```csharp
using System.Reflection;

// Explicitly pass assemblies to scan for [AspireExport] capabilities
var assemblies = new[] { typeof(SomeAspireType).Assembly };
await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args, assemblies);
```

### CapabilityDispatcher

Discovers and dispatches capability invocations:

- Scans assemblies for `[AspireExport]` and `[AspireContextType]` attributes
- Registers capability handlers for each discovered export
- Routes `invokeCapability` calls to the appropriate handler
- Handles parameter binding, optional parameters, and async methods

### HandleRegistry

Manages the lifecycle of .NET objects exposed to clients:

- Assigns unique handle IDs to objects (format: `{typeId}:{sequenceNumber}`)
- Resolves handle references back to objects
- Supports type-safe retrieval with expected type validation

### AtsMarshaller

Handles serialization between .NET types and JSON:

- Marshals primitive types directly
- Wraps complex objects as handle references
- Supports reference expressions for deferred evaluation
- Creates callback proxies for delegate parameters

## Environment Variables

| Variable | Description |
|----------|-------------|
| `REMOTE_APP_HOST_SOCKET_PATH` | Path to the Unix domain socket. Defaults to `{temp}/aspire/remote-app-host.sock` |
| `REMOTE_APP_HOST_PID` | Parent process ID for orphan detection. If set, the server shuts down when the parent exits |

## Security

The server uses file system permissions for security:
- On Unix/macOS: The socket file is created with mode 0600 (owner read/write only)
- On Windows: Named pipe ACLs restrict access to the current user only

This ensures only the user who started the AppHost can connect to the RPC server.

## Usage

This package is typically not used directly. Instead, the Aspire CLI scaffolds a project that references this package and generates the configuration. The generated project includes:

1. **Program.cs** - Entry point that starts the server:
   ```csharp
   await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args);
   ```

2. **appsettings.json** - Configuration with the list of integration assemblies:
   ```json
   {
     "AtsAssemblies": [
       "Aspire.Hosting",
       "Aspire.Hosting.Redis"
     ]
   }
   ```

The server loads each assembly listed in `AtsAssemblies` and scans them for `[AspireExport]` capabilities that can be invoked via JSON-RPC.

## JSON-RPC Methods

| Method | Description |
|--------|-------------|
| `ping` | Health check, returns "pong" |
| `invokeCapability` | Invoke a capability by ID with arguments |
| `invokeCallback` | Invoke a TypeScript callback from .NET (server→client) |

### invokeCapability

The primary method for interacting with Aspire. Capabilities are identified by `{AssemblyName}/{methodName}`.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "invokeCapability",
  "params": {
    "capabilityId": "Aspire.Hosting/createBuilder",
    "args": {
      "name": "my-app"
    }
  },
  "id": 1
}
```

**Response (success):**
```json
{
  "jsonrpc": "2.0",
  "result": {
    "$handle": "1",
    "$type": "Aspire.Hosting/IDistributedApplicationBuilder"
  },
  "id": 1
}
```

**Response (error):**
```json
{
  "jsonrpc": "2.0",
  "error": {
    "code": -32000,
    "message": "Capability error",
    "data": {
      "code": "CAPABILITY_NOT_FOUND",
      "message": "Unknown capability: Aspire.Hosting/unknownMethod",
      "capability": "Aspire.Hosting/unknownMethod"
    }
  },
  "id": 1
}
```

## Capability Discovery

Capabilities are discovered by scanning assemblies for:

1. **[AspireExport]** - Static methods that can be invoked:
    ```csharp
    /// <summary>
    /// Adds a Redis container.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The Redis resource name.</param>
    /// <param name="port">The optional Redis port.</param>
    /// <returns>The Redis resource builder.</returns>
    [AspireExport("addRedis", Description = "Adds a Redis container")]
    public static IResourceBuilder<RedisResource> AddRedis(
        IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    ```

   XML documentation is the primary source for generated polyglot SDK API documentation. The scanner captures `<summary>`, `<param>`, `<returns>`, and `<remarks>` tags for TypeScript JSDoc. First-level `ats-*` tags override the matching standard tags when polyglot SDK docs need different text: `<ats-summary>`, `<ats-param name="...">`, `<ats-returns>`, and `<ats-remarks>`. An empty `ats-*` override intentionally suppresses the matching standard documentation. The `Description` property remains supported as compatibility metadata and is used as a fallback only when the corresponding XML documentation tag is not available.

   ATS documentation can use `<ats-see cref="kind:identifier.path" />` and `<ats-seealso cref="kind:identifier.path" />` for language-neutral references to generated SDK elements. The supported `kind` values are `type`, `method`, and `field`. The reference target uses dot notation over generated polyglot identifiers instead of C# XML documentation `cref` syntax, for example:

   ```csharp
   /// <summary>
   /// Configures <ats-see cref="type:RedisResource" />.
   /// </summary>
   /// <remarks>
   /// See <ats-see cref="method:RedisResource.withPersistence" /> and
   /// <ats-see cref="field:RedisDefaults.Port" />.
   /// </remarks>
   ```

   ATS references do not support C# generic type syntax. Use the generated polyglot-visible name for the referenced element so each language generator can translate the same target to that language's documentation link syntax. TypeScript currently renders these references as JSDoc `{@link ...}` links.

2. **[AspireContextType]** - Types whose properties are exposed as capabilities. The type ID is derived as `{AssemblyName}/{TypeName}`:
   ```csharp
   [AspireContextType]  // Type ID = Aspire.Hosting/ConfigurationContext
   public class ConfigurationContext
   {
       public string? ConnectionString { get; set; }
   }
   ```

## Error Codes

| Code | Description |
|------|-------------|
| `CAPABILITY_NOT_FOUND` | The requested capability does not exist |
| `INVALID_ARGUMENT` | A required argument is missing or invalid |
| `HANDLE_NOT_FOUND` | The referenced handle does not exist |
| `TYPE_MISMATCH` | The argument type doesn't match the expected type |
| `INTERNAL_ERROR` | An unexpected error occurred during execution |
