# Polyglot AppHost Testing

> **Status:** Draft

This document describes how to write integration tests for Aspire applications using the Aspire CLI as the orchestration layer. This approach is language-agnostic and works with any test framework.

## Table of Contents

1. [Overview](#overview)
2. [Motivation](#motivation)
3. [Architecture](#architecture)
4. [CLI Primitives](#cli-primitives)
5. [Resource Snapshot Schema](#resource-snapshot-schema)
6. [Language Wrapper APIs](#language-wrapper-apis)
7. [Usage Examples](#usage-examples)
8. [Comparison with Aspire.Hosting.Testing](#comparison-with-aspirehostingtesting)
9. [Future Work](#future-work)

---

## Overview

The polyglot testing approach provides a way to write integration tests for Aspire applications without depending on .NET-specific testing infrastructure. Instead of using `Aspire.Hosting.Testing` (which requires invoking the AppHost entry point via reflection and hooking into DiagnosticListener events), tests use the Aspire CLI to:

1. Start the AppHost in detached mode
2. Query resource snapshots to get endpoints, connection strings, and health status
3. Wait for resources to reach desired states
4. Stop the AppHost when done

Each language (TypeScript, Python, Go, etc.) provides a thin wrapper library that spawns CLI commands and parses the structured JSON output.

---

## Motivation

### The Problem

`Aspire.Hosting.Testing` is deeply tied to .NET:

- Requires the AppHost to be a .NET assembly with an entry point
- Uses `DiagnosticListener` to intercept builder lifecycle events
- Invokes the entry point via reflection on a background thread
- Only works from .NET test projects

For polyglot AppHosts (TypeScript, Python, etc.) or when testing from non-.NET test frameworks, this approach doesn't work.

### The Solution

Use the Aspire CLI as a language-agnostic orchestration layer:

```text
┌─────────────────┐     spawn      ┌─────────────┐     runs     ┌─────────────┐
│   Test Runner   │ ────────────▶  │  Aspire CLI │ ──────────▶  │   AppHost   │
│  (Jest, pytest, │                │             │              │ (.NET, TS,  │
│   xUnit, etc.)  │ ◀────────────  │             │ ◀──────────  │  Python)    │
└─────────────────┘   JSON output  └─────────────┘  backchannel └─────────────┘
```

**Benefits:**
- Works with any test framework in any language
- Works with any AppHost (polyglot or .NET)
- CLI handles all the complexity (DCP, containers, service discovery)
- Structured JSON output is easy to parse
- Same primitives can be used for scripting, CI/CD, etc.

---

## Architecture

### Existing Infrastructure

The Aspire CLI already has the building blocks we need:

1. **`aspire start`** - Starts the AppHost in the background and returns immediately
2. **Auxiliary Backchannel** - JSON-RPC connection between CLI and running AppHost
3. **`WatchResourceSnapshotsAsync`** - Streams resource state changes via the backchannel
4. **`aspire stop`** - Gracefully stops a running AppHost
5. **`aspire ps --format json`** - Lists running AppHosts with their PIDs and Dashboard URLs

### New CLI Commands

We add a new `aspire describe` command that exposes resource snapshots:

```bash
aspire describe [--follow] [--project <path>]
```

- **`aspire describe`** - Returns a JSON snapshot of all resources
- **`aspire describe --follow`** - Streams NDJSON snapshots as resources change

Language wrapper libraries build convenience methods on top of these primitives.

---

## CLI Primitives

### `aspire start`

Starts the AppHost in the background.

```bash
aspire start --apphost ./MyApp.AppHost/MyApp.AppHost.csproj
```

By default, outputs human-readable text. Use `--format json` for structured output:

```bash
aspire start --format json --project ./MyApp.AppHost/MyApp.AppHost.csproj
```

**Output (JSON):**
```json
{
  "appHostPath": "/path/to/MyApp.AppHost/MyApp.AppHost.csproj",
  "appHostPid": 12345,
  "cliPid": 12340,
  "dashboardUrl": "http://localhost:15000/login?t=abc123",
  "logFile": "/path/to/MyApp.AppHost/.aspire/logs/apphost.log"
}
```

The command:
1. Spawns the CLI as a child process running `aspire run`
2. Waits for the backchannel connection to be established
3. Returns connection info and exits (JSON if `--format json` specified)

### `aspire stop`

Gracefully stops a running AppHost.

```bash
aspire stop [--project <path>]
```

If `--project` is not specified, stops the AppHost in the current directory (or prompts if multiple are found).

### `aspire describe`

Returns a snapshot of all resources.

```bash
aspire describe [--project <path>]
```

**Output (JSON):**
```json
{
  "resources": [
    {
      "name": "redis",
      "type": "Container",
      "state": "Running",
      "healthStatus": "Healthy",
      "endpoints": [
        {
          "name": "tcp",
          "url": "tcp://localhost:6379",
          "isInternal": false
        }
      ],
      "connectionString": "localhost:6379",
      "properties": {
        "container.image": "redis:7.4",
        "container.id": "abc123def456"
      }
    },
    {
      "name": "api",
      "type": "Project",
      "state": "Running",
      "healthStatus": "Healthy",
      "endpoints": [
        {
          "name": "http",
          "url": "http://localhost:5001",
          "isInternal": false
        },
        {
          "name": "https",
          "url": "https://localhost:5002",
          "isInternal": false
        }
      ],
      "connectionString": null,
      "properties": {
        "project.path": "/path/to/Api/Api.csproj"
      }
    }
  ]
}
```

### `aspire describe --follow`

Streams resource snapshots as NDJSON (newline-delimited JSON).

```bash
aspire describe --follow [--project <path>]
```

**Output (NDJSON):**
```json
{"name":"redis","type":"Container","state":"Starting","healthStatus":null,"endpoints":[],"connectionString":null}
{"name":"redis","type":"Container","state":"Running","healthStatus":"Healthy","endpoints":[{"name":"tcp","url":"tcp://localhost:6379"}],"connectionString":"localhost:6379"}
{"name":"api","type":"Project","state":"Starting","healthStatus":null,"endpoints":[]}
{"name":"api","type":"Project","state":"Running","healthStatus":"Healthy","endpoints":[{"name":"http","url":"http://localhost:5001"}]}
```

Each line is a complete resource snapshot. The stream continues until:
- The AppHost stops
- The command is interrupted (Ctrl+C)
- The `--timeout` is reached (if specified)

### `aspire logs`

Retrieves resource console logs from the `ResourceLoggerService`. These are the same logs displayed in the Aspire Dashboard's console logs view - stdout/stderr output captured from containers, projects, and executables.

> **Note:** These are console logs (stdout/stderr), not OpenTelemetry structured logs. For OTel logs, traces, and metrics, use the Dashboard's telemetry views or configure an external collector.

See [GitHub Issue #8069](https://github.com/microsoft/aspire/issues/8069) for the original feature request.

```bash
aspire logs [resource] [--project <path>] [--follow] [--format json]
```

**Arguments:**
- `resource` - Optional resource name. Required unless using `--follow`.

**Options:**
- `--follow`, `-f` - Stream logs in real-time (like `docker logs -f`)
- `--format json` - Output logs as JSON/NDJSON for programmatic consumption
- `--project <path>` - Path to the AppHost project

**Tip:** Pipe output to files if needed: `aspire logs redis --follow > redis.log`

#### JSON Output

Use `--format json` for structured log output that's easy to parse programmatically.

**Single snapshot (`aspire logs <resource> --format json`):**

Outputs NDJSON (one JSON object per line):
```json
{"resourceName":"redis","content":"1:C 15 Jan 2024 10:30:14.123 * oO0OoO0OoO0Oo Redis is starting","isError":false}
{"resourceName":"redis","content":"1:M 15 Jan 2024 10:30:14.456 * Ready to accept connections","isError":false}
```

**Streaming (`aspire logs --format json --follow`):**

Outputs NDJSON continuously as logs are written:
```json
{"resourceName":"redis","content":"Ready to accept connections","isError":false}
{"resourceName":"api","content":"info: Application started","isError":false}
{"resourceName":"api","content":"warn: Cache miss","isError":false}
```

**Log Format Fields:**
- `resourceName` - The name of the resource that produced the log
- `content` - The log line text (may include embedded timestamps from the source)
- `isError` - Whether this came from stderr

#### Get All Logs

Retrieve logs from all resources at once. Useful for collecting diagnostic information after a test run.

```bash
# Get logs from a specific resource
aspire logs redis

# Stream all logs to a file
aspire logs --follow > all-logs.txt
```

**Output (interleaved with resource prefix):**

```text
[redis] 1:C 15 Jan 2024 10:30:14.123 * oO0OoO0OoO0Oo Redis is starting
[redis] 1:M 15 Jan 2024 10:30:14.456 * Ready to accept connections
[api] info: Application started
[api] info: Listening on http://localhost:5001
[postgres] LOG: database system is ready to accept connections
```

#### Tail/Follow Logs

Stream logs in real-time, similar to `docker logs -f` or `kubectl logs -f`.

```bash
# Follow all logs
aspire logs --follow

# Follow logs for a specific resource
aspire logs api --follow
```

**Termination criteria:**
The stream continues until:
- The AppHost stops (the CLI will exit when the connection is lost)
- The command is interrupted (Ctrl+C)
- The resource enters a terminal state (Exited, Finished, FailedToStart)

> **Note:** Logs from the shutdown process may not be captured if the AppHost stops before the log stream flushes. For complete diagnostic logs in failure scenarios, consider using the AppHost's log file (shown in `aspire start` output).

#### Logs for a Specific Resource

Get logs for a single resource by name.

```bash
# Get all logs for a resource
aspire logs api

# Get last 100 lines
aspire logs postgres --tail 100

# Stream logs for a resource
aspire logs redis --follow
```

**Output (single resource - no prefix needed):**

```text
[2024-01-15T10:30:15.123Z] info: Application started
[2024-01-15T10:30:15.456Z] info: Listening on http://localhost:5001
[2024-01-15T10:30:16.789Z] warn: Cache miss for key 'products'
```

#### CI/CD Integration

The logs command is designed to work well in CI/CD pipelines:

```bash
#!/bin/bash
set -e

# Start the AppHost
aspire start --format json > apphost.json

# Run tests
npm test || TEST_FAILED=1

# Always collect logs (even if tests fail) - pipe to files
aspire logs redis --follow > ./test-artifacts/logs/redis.log 2>&1 &
aspire logs api --follow > ./test-artifacts/logs/api.log 2>&1 &
sleep 2  # Give logs time to collect
kill %1 %2 2>/dev/null || true

# Stop the AppHost
aspire stop

# Exit with test result
exit ${TEST_FAILED:-0}
```

---

## Resource Snapshot Schema

The resource snapshot schema is designed to provide all information needed for testing:

```typescript
interface ResourceSnapshot {
  /** Unique name of the resource */
  name: string;
  
  /** Resource type: "Project", "Container", "Executable", etc. */
  type: string;
  
  /** Current state: "Starting", "Running", "Stopping", "Exited", "FailedToStart", etc. */
  state: string | null;
  
  /** Health status: "Healthy", "Unhealthy", "Degraded", or null if not running/no health checks */
  healthStatus: "Healthy" | "Unhealthy" | "Degraded" | null;
  
  /** Endpoints exposed by the resource */
  endpoints: Endpoint[];
  
  /** Connection string if the resource exposes one */
  connectionString: string | null;
  
  /** Additional properties (container image, project path, etc.) */
  properties: Record<string, string>;
  
  /** Relationships to other resources */
  relationships: Relationship[];
}

interface Endpoint {
  /** Endpoint name (e.g., "http", "https", "tcp") */
  name: string;
  
  /** Full URL including scheme, host, and port */
  url: string;
  
  /** Whether this is an internal endpoint (not exposed externally) */
  isInternal: boolean;
}

interface Relationship {
  /** Name of the related resource */
  resourceName: string;
  
  /** Relationship type: "Parent", "Reference", etc. */
  type: string;
}
```

### State Values

The `state` field uses values from `KnownResourceStates`:

| State | Description |
|-------|-------------|
| `Starting` | Resource is starting up |
| `Running` | Resource is running and ready |
| `Stopping` | Resource is shutting down |
| `Exited` | Executable/project has stopped (check `exitCode` property) |
| `Finished` | Container has stopped (check `exitCode` property) |
| `FailedToStart` | Resource failed to start |
| `Waiting` | Resource is waiting for a dependency |
| `NotStarted` | Resource was not started (e.g., `.WithExplicitStart()`) |

> **Note:** Both `Exited` and `Finished` indicate the resource has stopped. `Exited` is used for executables/projects, while `Finished` is used for containers. Check the `exitCode` property to determine if it was a successful completion (0) or a failure (non-zero).

### Health Status Values

| Status | Description |
|--------|-------------|
| `Healthy` | All health checks passing |
| `Unhealthy` | One or more health checks failing |
| `Degraded` | Health checks indicate degraded state |
| `null` | Resource not running or no health checks configured |

---

## Language Wrapper APIs

Each language provides a wrapper library that builds on the CLI primitives.

### TypeScript / JavaScript

```typescript
import { AspireApp } from '@aspire/testing';

// Start the AppHost
const app = await AspireApp.start({
  project: './MyApp.AppHost/MyApp.AppHost.csproj'
});

try {
  // Wait for a resource to be running and healthy
  await app.waitForResource('redis', { state: 'Running', healthy: true });
  await app.waitForResource('api', { state: 'Running', healthy: true });
  
  // Get resource information
  const redis = app.getResource('redis');
  console.log(redis.connectionString); // "localhost:6379"
  
  const api = app.getResource('api');
  const httpEndpoint = api.getEndpoint('http');
  console.log(httpEndpoint.url); // "http://localhost:5001"
  
  // Make HTTP requests
  const response = await fetch(`${httpEndpoint.url}/api/products`);
  expect(response.ok).toBe(true);
  
} finally {
  // Collect logs before stopping (useful for CI/debugging)
  await app.saveLogs('./test-artifacts/logs');
  
  // Or get logs programmatically
  const logs = await app.getLogs(); // all resources
  const apiLogs = await app.getLogs('api'); // specific resource
  
  // Always stop the AppHost
  await app.stop();
}
```

### Log Collection API

The wrapper provides multiple ways to collect logs:

```typescript
// Save all logs to files (one per resource)
await app.saveLogs('./test-artifacts/logs');

// Get all logs as structured data
const allLogs = await app.getLogs();
// Returns: LogEntry[]

// Get logs for a specific resource
const apiLogs = await app.getLogs('api');

// Get last N lines
const recentLogs = await app.getLogs('api', { tail: 100 });

// Stream logs in real-time
for await (const entry of app.streamLogs()) {
  console.log(`[${entry.resource}] ${entry.line}`);
}

// Stream logs for a specific resource
for await (const entry of app.streamLogs('api')) {
  console.log(entry.line);
}
```

**LogEntry interface:**
```typescript
interface LogEntry {
  resource: string;
  timestamp: Date;
  line: string;
  isError: boolean;
}
```

### Implementation

The TypeScript wrapper:

1. Spawns `aspire start --format json` and parses the JSON output
2. Spawns `aspire describe --follow` in the background to maintain resource state
3. Provides async methods that wait for state changes
4. Can collect logs via `aspire logs` piped to files before cleanup
5. Spawns `aspire stop` on cleanup

```typescript
class AspireApp {
  private resources: Map<string, ResourceSnapshot> = new Map();
  private watcher: ChildProcess | null = null;
  
  static async start(options: StartOptions): Promise<AspireApp> {
    const app = new AspireApp();
    
    // Start the AppHost
    const result = await exec(`aspire start --format json --project ${options.project}`);
    const info = JSON.parse(result.stdout);
    app.appHostPid = info.appHostPid;
    
    // Start watching resources
    app.watcher = spawn('aspire', ['describe', '--follow', '--format', 'json', '--project', options.project]);
    app.watcher.stdout.on('data', (chunk) => {
      for (const line of chunk.toString().split('\n')) {
        if (line.trim()) {
          const snapshot = JSON.parse(line);
          app.resources.set(snapshot.name, snapshot);
          app.emit('resourceChanged', snapshot);
        }
      }
    });
    
    return app;
  }
  
  async waitForResource(name: string, options: WaitOptions): Promise<ResourceSnapshot> {
    const deadline = Date.now() + (options.timeout ?? 60000);
    
    while (Date.now() < deadline) {
      const resource = this.resources.get(name);
      
      // Check for terminal failure states
      if (resource) {
        const state = resource.state;
        if (state === 'FailedToStart' || state === 'Exited' || state === 'Finished') {
          const exitCode = resource.properties?.['resource.exitCode'];
          if (state === 'FailedToStart' || (exitCode !== undefined && exitCode !== 0)) {
            throw new Error(`Resource ${name} failed: state=${state}, exitCode=${exitCode}`);
          }
        }
        
        if (this.matchesConditions(resource, options)) {
          return resource;
        }
      }
      
      await this.waitForChange(name, deadline - Date.now());
    }
    
    throw new Error(`Timeout waiting for resource ${name}`);
  }
  
  getResource(name: string): ResourceSnapshot {
    const resource = this.resources.get(name);
    if (!resource) {
      throw new Error(`Resource ${name} not found`);
    }
    return resource;
  }
  
  async stop(): Promise<void> {
    this.watcher?.kill();
    await exec(`aspire stop`);
  }
}
```

### Python

```python
from aspire.testing import AspireApp

async with AspireApp.start(project='./MyApp.AppHost') as app:
    # Wait for resources
    await app.wait_for_resource('redis', state='Running', healthy=True)
    await app.wait_for_resource('api', state='Running', healthy=True)
    
    # Get endpoints
    api = app.get_resource('api')
    http_url = api.get_endpoint('http').url
    
    # Make requests
    async with httpx.AsyncClient() as client:
        response = await client.get(f'{http_url}/api/products')
        assert response.status_code == 200
```

### .NET

For .NET tests that want CLI-based testing (instead of `Aspire.Hosting.Testing`):

```csharp
await using var app = await AspireApp.StartAsync(new StartOptions
{
    Project = "./MyApp.AppHost/MyApp.AppHost.csproj"
});

await app.WaitForResourceAsync("redis", state: "Running", healthy: true);
await app.WaitForResourceAsync("api", state: "Running", healthy: true);

var api = app.GetResource("api");
var httpEndpoint = api.GetEndpoint("http");

using var httpClient = new HttpClient { BaseAddress = new Uri(httpEndpoint.Url) };
var response = await httpClient.GetAsync("/api/products");
response.EnsureSuccessStatusCode();
```

---

## Usage Examples

### Jest Test

```typescript
import { AspireApp } from '@aspire/testing';
import { describe, it, expect, beforeAll, afterAll } from '@jest/globals';

describe('Product API', () => {
  let app: AspireApp;
  let apiUrl: string;

  beforeAll(async () => {
    app = await AspireApp.start({ project: './MyApp.AppHost' });
    await app.waitForResource('api', { state: 'Running', healthy: true });
    apiUrl = app.getResource('api').getEndpoint('http').url;
  }, 120000); // 2 minute timeout for startup

  afterAll(async () => {
    await app.stop();
  });

  it('should list products', async () => {
    const response = await fetch(`${apiUrl}/api/products`);
    expect(response.ok).toBe(true);
    
    const products = await response.json();
    expect(products).toBeInstanceOf(Array);
  });

  it('should create a product', async () => {
    const response = await fetch(`${apiUrl}/api/products`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Test Product', price: 9.99 })
    });
    expect(response.status).toBe(201);
  });
});
```

### pytest Test

```python
import pytest
from aspire.testing import AspireApp

@pytest.fixture(scope='module')
async def app():
    async with AspireApp.start(project='./MyApp.AppHost') as app:
        await app.wait_for_resource('api', state='Running', healthy=True)
        yield app

@pytest.fixture
def api_url(app):
    return app.get_resource('api').get_endpoint('http').url

@pytest.mark.asyncio
async def test_list_products(api_url):
    async with httpx.AsyncClient() as client:
        response = await client.get(f'{api_url}/api/products')
        assert response.status_code == 200
        assert isinstance(response.json(), list)
```

---

## Comparison with Aspire.Hosting.Testing

| Feature | Aspire.Hosting.Testing | CLI-Based Testing |
|---------|------------------------|-------------------|
| **Language Support** | .NET only | Any language |
| **AppHost Types** | .NET only | .NET, TypeScript, Python, etc. |
| **Builder Access** | Full access to `IDistributedApplicationBuilder` | No builder access |
| **Resource Mutation** | Can modify resources before run | Cannot modify resources |
| **Test Framework** | xUnit, NUnit, MSTest | Any (Jest, pytest, etc.) |
| **Startup Time** | Fast (in-process) | Slower (process spawn) |
| **Debugging** | Easy (same process) | Requires attach to child process |
| **Isolation** | Shares process with test | Full process isolation |

### When to Use Which

**Use `Aspire.Hosting.Testing` when:**
- Testing .NET AppHosts from .NET tests
- Need to modify the AppHost configuration in tests
- Want faster test execution
- Need to access internal services for mocking or override DI
- Testing hosting integrations themselves

**Use CLI-based testing when:**
- Testing polyglot AppHosts (TypeScript, Python, etc.)
- Using non-.NET test frameworks
- Want full process isolation
- Testing the AppHost as a "black box"
- Writing cross-platform CI/CD scripts

---

## Future Work

### Phase 1: Core Implementation
- [ ] Implement `aspire describe` command
- [ ] Implement `aspire describe --follow` command
- [ ] Ensure `aspire start` returns structured JSON
- [ ] Update `aspire stop` to work reliably with detached instances

### Phase 2: TypeScript Wrapper
- [ ] Create `@aspire/testing` npm package
- [ ] Implement `AspireApp.start()`, `stop()`, `waitForResource()`
- [ ] Add resource snapshot parsing and caching
- [ ] Write documentation and examples

### Phase 3: Additional Languages
- [ ] Python wrapper (`aspire-testing` pip package)
- [ ] Go wrapper
- [ ] .NET wrapper (for non-`TEntryPoint` scenarios)

### Phase 4: Enhanced Features
- [ ] Timeout configuration for `waitForResource`
- [ ] Resource filtering in `aspire describe`
- [ ] Log streaming for debugging
- [ ] Integration with test framework fixtures/hooks

---

## References

- [Aspire.Hosting.Testing](../../../src/Aspire.Hosting.Testing/) - Existing .NET testing infrastructure
- [Polyglot AppHost](./polyglot-apphost.md) - Polyglot AppHost architecture
- [CLI Backchannel](../../../src/Aspire.Cli/Backchannel/) - CLI-to-AppHost communication
