---
name: aspire-init
description: "One-time skill for completing Aspire initialization after `aspire init` has dropped the skeleton AppHost and aspire.config.json. This skill scans the repository, discovers projects, wires up the AppHost (TypeScript or C#), configures dependencies and OpenTelemetry, validates that `aspire start` works, and self-removes on success."
---

# Aspire Init

This is a **one-time setup skill**. It completes the Aspire initialization that `aspire init` started. After this skill finishes successfully, it should be deleted — the evergreen `aspire` skill handles ongoing AppHost work.

## Guiding principles

### Minimize changes to the user's code

The default stance is **adapt the AppHost to fit the app, not the other way around**. The user's services already work — the goal is to model them in Aspire without breaking anything.

- Prefer `WithEnvironment()` to match existing env var names over asking users to rename vars in their code
- Use `WithHttpEndpoint(port: <existing-port>)` to match hardcoded ports rather than changing the service
- Map existing `docker-compose.yml` config 1:1 before optimizing
- Don't restructure project directories, rename files, or change build scripts

### Surface tradeoffs, don't decide silently

Sometimes a small code change unlocks significantly better Aspire integration. When this happens, **present the tradeoff to the user and let them decide**. Examples:

- **Connection strings**: A service reads `DATABASE_URL` but Aspire injects `ConnectionStrings__mydb`. You can use `WithEnvironment("DATABASE_URL", db.Resource.ConnectionStringExpression)` (zero code change) or suggest the service reads from config so `WithReference(db)` just works (enables service discovery, health checks, auto-retry).
  → Ask: *"Your API reads DATABASE_URL. I can map that with WithEnvironment (no code change) or you could switch to reading ConnectionStrings:mydb which unlocks WithReference and automatic service discovery. Which do you prefer?"*

- **Port binding**: A service hardcodes `PORT=3000`. You can match it with `WithHttpEndpoint(port: 3000)` (zero change) or suggest reading from env so Aspire can assign ports dynamically and avoid conflicts.
  → Ask: *"Your frontend hardcodes port 3000. I can match that, but if you read PORT from env instead, Aspire can assign ports dynamically and avoid conflicts when running multiple services. Want me to make that change?"*

- **OTel setup**: Service has its own tracing config pointing to Jaeger. You can leave it (Aspire won't show its traces) or suggest switching the exporter to read `OTEL_EXPORTER_OTLP_ENDPOINT` (which Aspire injects).
  → Ask: *"Your API exports traces to Jaeger directly. I can leave that, or switch it to use the OTEL_EXPORTER_OTLP_ENDPOINT env var so traces show up in the Aspire dashboard. The Jaeger endpoint would still work in non-Aspire environments. Want me to update it?"*

**Format for presenting tradeoffs:**
1. Explain what the current code does
2. Show the zero-change option and what it gives you
3. Show the small-change option and the extra benefits
4. Ask which they prefer
5. If they decline the change, implement the zero-change option without complaint

### When in doubt, ask

If you're unsure whether something is a service, whether two services depend on each other, whether a port is significant, or whether a Docker Compose service should be modeled — ask. Don't guess at architectural intent.

## Prerequisites

Before running this skill, `aspire init` must have already:

- Dropped a skeleton AppHost file (`apphost.ts` or `apphost.cs`) at the configured location
- Created `aspire.config.json` at the repository root

Verify both exist before proceeding.

## Determine your context

Read `aspire.config.json` at the repository root. Key fields:

- **`appHost.language`**: `"typescript/nodejs"` or `"csharp"` — determines which syntax and tooling to use
- **`appHost.path`**: path to the AppHost file or project directory — this is where you'll edit code

For C# AppHosts, there are two sub-modes:

- **Single-file mode**: `appHost.path` points directly to an `apphost.cs` file using the `#:sdk` directive. No `.csproj` needed.
- **Full project mode**: `appHost.path` points to a directory containing a `.csproj` and `apphost.cs`. This was created because a `.sln`/`.slnx` was found.

Check which mode you're in by looking at what exists at the `appHost.path` location.

## Workflow

Follow these steps in order. If any step fails, diagnose and fix before continuing.

### Step 1: Scan the repository

Analyze the repository to discover all projects and services that could be modeled in the AppHost.

**What to look for:**

- **.NET projects**: `*.csproj` and `*.fsproj` files. For each, run:
  - `dotnet msbuild <project> -getProperty:OutputType` — `Exe`/`WinExe` = runnable service, `Library` = skip
  - `dotnet msbuild <project> -getProperty:TargetFramework` — must be `net8.0` or newer
  - `dotnet msbuild <project> -getProperty:IsAspireHost` — skip if `true`
- **Node.js/TypeScript apps**: directories with `package.json` containing a `start`, `dev`, or `main`/`module` entry
- **Python apps**: directories with `pyproject.toml`, `requirements.txt`, or `main.py`/`app.py`
- **Go apps**: directories with `go.mod`
- **Java apps**: directories with `pom.xml` or `build.gradle`
- **Dockerfiles**: standalone `Dockerfile` entries representing services
- **Docker Compose**: `docker-compose.yml` or `compose.yml` files — these are a goldmine. Parse them to extract:
  - **Services**: each named service maps to a potential AppHost resource
  - **Images**: container images used (e.g., `postgres:16`, `redis:7`) → these become `AddContainer()` or typed Aspire integrations (e.g., `AddPostgres()`, `AddRedis()`)
  - **Ports**: published port mappings → `WithHttpEndpoint()` or `WithEndpoint()`
  - **Environment variables**: env vars and `.env` file references → `WithEnvironment()`
  - **Volumes**: named/bind volumes → `WithVolume()` or `WithBindMount()`
  - **Dependencies**: `depends_on` → `WithReference()` and `WaitFor()`
  - **Build contexts**: `build:` entries → `AddDockerfile()` pointing to the build context directory
  - Prefer typed Aspire integrations over raw `AddContainer()` when the image matches a known integration (use `aspire docs search` to check). For example, `postgres:16` → `AddPostgres()`, `redis:7` → `AddRedis()`, `rabbitmq:3` → `AddRabbitMQ()`.
- **Static frontends**: Vite, Next.js, Create React App, or other frontend framework configs

**Ignore:**

- The AppHost directory/file itself
- `node_modules/`, `.modules/`, `dist/`, `build/`, `bin/`, `obj/`, `.git/`
- Test projects (directories named `test`/`tests`/`__tests__`, projects referencing xUnit/NUnit/MSTest, or test-only package.json scripts)

### Step 2: Present findings and confirm with the user

Show the user what you found. For each discovered project/service, show:

- Name (project or directory name)
- Type (.NET service, Node.js app, Python app, Dockerfile, etc.)
- Framework/runtime info (e.g., net10.0, Node 20, Python 3.12)
- Whether it exposes HTTP endpoints

Ask the user:

1. Which projects to include in the AppHost (pre-select all discovered runnable services)
2. For C# AppHosts: which .NET projects should receive ServiceDefaults references (pre-select all .NET services)

### Step 3: Create ServiceDefaults (C# only)

> **Skip this step for TypeScript AppHosts.** OTel for non-.NET services is handled in Step 7.

If no ServiceDefaults project exists in the repo, create one:

```bash
dotnet new aspire-servicedefaults -n <SolutionName>.ServiceDefaults -o <path>
```

Place it alongside the AppHost (e.g., `src/` or solution root). If a `.sln` exists, add it:

```bash
dotnet sln <solution> add <ServiceDefaults.csproj>
```

If a ServiceDefaults project already exists (look for references to `Microsoft.Extensions.ServiceDiscovery` or `Aspire.ServiceDefaults`), skip creation and use the existing one.

### Step 4: Wire up the AppHost

Edit the skeleton AppHost file to add resource definitions for each selected project. Use the appropriate syntax based on language.

#### TypeScript AppHost (`apphost.ts`)

```typescript
import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Node.js/TypeScript app
const api = await builder
    .addNodeApp("api", "./api", "src/index.ts")
    .withHttpEndpoint({ env: "PORT" });

// Vite frontend
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withReference(api)
    .waitFor(api);

// .NET project
const dotnetSvc = await builder
    .addProject("catalog", "./src/Catalog/Catalog.csproj");

// Dockerfile-based service
const worker = await builder
    .addDockerfile("worker", "./worker");

// Python app
const pyApi = await builder
    .addPythonApp("py-api", "./py-api", "app.py");

await builder.build().run();
```

#### C# AppHost — single-file mode (`apphost.cs`)

```csharp
#:sdk Aspire.AppHost.Sdk@<version>
#:property IsAspireHost=true

// Project references
#:project ../src/Api/Api.csproj
#:project ../src/Web/Web.csproj

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api");

var web = builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
```

#### C# AppHost — full project mode (`apphost.cs` + `.csproj`)

Edit `apphost.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api");

var web = builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
```

And add project references:

```bash
dotnet add <AppHost.csproj> reference <Api.csproj>
dotnet add <AppHost.csproj> reference <Web.csproj>
```

#### Non-.NET services in a C# AppHost

```csharp
// Node.js app (requires Aspire.Hosting.NodeJs)
var frontend = builder.AddNpmApp("frontend", "../frontend", "start");

// Dockerfile-based service
var worker = builder.AddDockerfile("worker", "../worker");

// Python app (requires Aspire.Hosting.Python)
var pyApi = builder.AddPythonApp("py-api", "../py-api", "app.py");
```

Add required hosting NuGet packages:

```bash
dotnet add <AppHost.csproj> package Aspire.Hosting.NodeJs
dotnet add <AppHost.csproj> package Aspire.Hosting.Python
```

**Important rules:**

- Use `aspire docs search` and `aspire docs get` to look up the correct builder API for each resource type. Do not guess API shapes.
- Check `.modules/aspire.ts` (TypeScript) or NuGet package APIs (C#) to confirm available methods.
- Use meaningful resource names derived from the project/directory name.
- Wire up `WithReference()`/`withReference()` and `WaitFor()`/`waitFor()` for services that depend on each other (ask the user if relationships are unclear).
- Use `WithExternalHttpEndpoints()`/`withExternalHttpEndpoints()` for user-facing frontends.

### Step 5: Configure dependencies

#### TypeScript AppHost

**package.json** — if one exists at the root, augment it (do not overwrite). Add/merge:

```json
{
  "type": "module",
  "scripts": {
    "start": "npx tsc && node --enable-source-maps apphost.js"
  }
}
```

If no root `package.json` exists, create a minimal one:

```json
{
  "name": "<repo-name>-apphost",
  "version": "1.0.0",
  "type": "module",
  "scripts": {
    "start": "npx tsc && node --enable-source-maps apphost.js"
  }
}
```

Never overwrite existing `scripts`, `dependencies`, or `devDependencies` — merge only. Do not manually add Aspire SDK packages — `aspire restore` handles those.

Run `aspire restore` to generate the `.modules/` directory with TypeScript SDK bindings, then `npm install`.

**tsconfig.json** — augment if it exists:

- Ensure `".modules/**/*.ts"` and `"apphost.ts"` are in `include`
- Ensure `"module"` is `"nodenext"` or `"node16"` (ESM required)
- Ensure `"moduleResolution"` matches

If no `tsconfig.json` exists and `aspire restore` didn't create one, create a minimal one:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "nodenext",
    "moduleResolution": "nodenext",
    "esModuleInterop": true,
    "strict": true,
    "outDir": "./dist",
    "rootDir": "."
  },
  "include": ["apphost.ts", ".modules/**/*.ts"]
}
```

**ESLint** — only augment if config already exists. If it uses `parserOptions.project` or `parserOptions.projectService`, ensure the AppHost tsconfig is discoverable. Do not create ESLint configuration from scratch.

#### C# AppHost

**Full project mode**: dependencies are managed via the `.csproj` and `dotnet add package`/`dotnet add reference` (already handled in Steps 3-4).

**Single-file mode**: dependencies are managed via `#:sdk` and `#:project` directives in the `apphost.cs` file.

**NuGet feeds**: If `aspire.config.json` specifies a non-stable channel (preview, daily), ensure the appropriate NuGet feed is configured. For single-file mode this is automatic; for project mode, ensure a `NuGet.config` is in scope.

### Step 6: Add ServiceDefaults to .NET projects (C# AppHost only)

> **Skip this step for TypeScript AppHosts.**

For each .NET project that the user selected for ServiceDefaults:

```bash
dotnet add <Project.csproj> reference <ServiceDefaults.csproj>
```

Then check each project's `Program.cs` (or equivalent entry point) and add if not already present:

```csharp
builder.AddServiceDefaults();  // Add early, after builder creation
```

And before `app.Run()`:

```csharp
app.MapDefaultEndpoints();
```

Be careful with code placement — look at existing structure (top-level statements vs `Startup.cs` vs `Program.Main`). Do not duplicate if already present.

### Step 7: Wire up OpenTelemetry for non-.NET services

For non-.NET services included in the AppHost, configure OpenTelemetry so the Aspire dashboard shows their traces, metrics, and logs. This is the equivalent of what ServiceDefaults does for .NET.

**Node.js/TypeScript services:**

```bash
npm install @opentelemetry/sdk-node @opentelemetry/auto-instrumentations-node @opentelemetry/exporter-otlp-grpc
```

Add an instrumentation file that reads `OTEL_EXPORTER_OTLP_ENDPOINT` (injected by Aspire automatically).

**Python services**: suggest `opentelemetry-distro` and `opentelemetry-exporter-otlp`.

**Other languages**: point the user to OpenTelemetry docs for their language. The OTLP endpoint is injected via environment variables by Aspire.

**Important**: Ask the user before modifying any service code. OTel setup may conflict with existing instrumentation. Present it as a recommendation, not an automatic change.

### Step 8: Validate

```bash
aspire start
```

Check that:

1. The dashboard URL is printed
2. All modeled resources appear in `aspire describe`
3. No startup errors in `aspire logs`

If it fails, diagnose and iterate. Common issues:

- **TypeScript**: missing `npm install`, TS compilation errors, port conflicts
- **C# project mode**: missing project references, NuGet restore needed, TFM mismatches, build errors
- **C# single-file**: `#:project` paths wrong, missing SDK directive
- **Both**: missing environment variables, port conflicts
- **Certificate errors**: if HTTPS fails, run `aspire certs trust` and retry

### Step 9: Update solution file (C# full project mode only)

If a `.sln`/`.slnx` exists, verify all new projects are included:

```bash
dotnet sln <solution> list
```

Ensure both the AppHost and ServiceDefaults projects appear.

### Step 10: Clean up

After successful validation:

1. Stop the running AppHost: `aspire stop`
2. **Delete this skill** — remove the `aspire-init/` skill directory from all locations where it was installed (check `.agents/skills/`, `.github/skills/`, `.claude/skills/`)
3. Confirm the evergreen `aspire` skill is present for ongoing AppHost work

## Key rules

- **Never overwrite existing files** — always augment/merge
- **Ask the user before modifying service code** (especially OTel and ServiceDefaults injection)
- **Respect existing project structure** — don't reorganize the repo
- **This is a one-time skill** — delete it after successful init
- **If stuck, use `aspire doctor`** to diagnose environment issues

## Looking up APIs and integrations

Before writing AppHost code for an unfamiliar resource type or integration, **always** look it up:

```bash
# Search for documentation on a topic
aspire docs search "redis"
aspire docs search "node app endpoints"

# Get a specific doc page by slug (returned from search results)
aspire docs get "redis-integration"
```

Use `aspire docs search` to find the right builder methods, configuration options, and patterns. Use `aspire docs get <slug>` to read the full doc page. Do not guess API shapes — Aspire has many resource types with specific overloads.

To add an integration package (which unlocks typed builder methods):

```bash
aspire add Aspire.Hosting.Redis
aspire add Aspire.Hosting.NodeJs
aspire add Aspire.Hosting.Python
```

After adding, run `aspire restore` (TypeScript) or `dotnet restore` (C#) to update available APIs, then check what methods are now available.

## AppHost wiring reference

This section covers the patterns you'll need when writing Step 4 (Wire up the AppHost). Refer back to it as needed.

### Service communication: `WithReference` vs `WithEnvironment`

**`WithReference()`** is the primary way to connect services. It does two things:

1. Injects the referenced resource's connection information (connection string or URL) into the consuming service
2. Enables Aspire service discovery — .NET services can resolve the referenced resource by name

```csharp
// C#: api gets the database connection string injected automatically
var db = builder.AddPostgres("pg").AddDatabase("mydb");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(db);

// C#: frontend gets service discovery URL for api
var frontend = builder.AddProject<Projects.Web>("web")
    .WithReference(api);
```

```typescript
// TypeScript equivalent
const db = await builder.addPostgres("pg").addDatabase("mydb");
const api = await builder.addProject("api", "./src/Api/Api.csproj")
    .withReference(db);
```

**How non-.NET services consume references**: They receive environment variables. The naming convention is:
- Connection strings: `ConnectionStrings__<resourceName>` (e.g., `ConnectionStrings__mydb=Host=...`)
- Service URLs: `services__<resourceName>__<endpointName>__0` (e.g., `services__api__http__0=http://localhost:5123`)

**`WithEnvironment()`** injects raw environment variables. Use this for custom config that isn't a service reference:

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .WithEnvironment("FEATURE_FLAG_X", "true")
    .WithEnvironment("API_KEY", someParameter);
```

**When to use which:**
- Connecting service A to service B or a database/cache/queue → `WithReference()`
- Passing configuration values, feature flags, API keys → `WithEnvironment()`
- Never manually construct connection strings with `WithEnvironment()` when `WithReference()` would work

### Endpoints and ports

**`WithHttpEndpoint()`** — expose an HTTP endpoint. For services that serve HTTP traffic:

```csharp
// Let Aspire assign a random port (recommended for most cases)
var api = builder.AddProject<Projects.Api>("api")
    .WithHttpEndpoint();

// Use a specific port
var api = builder.AddProject<Projects.Api>("api")
    .WithHttpEndpoint(port: 5000);

// For non-.NET services that read the port from an env var
var nodeApi = builder.AddNpmApp("api", "../api", "start")
    .WithHttpEndpoint(env: "PORT");  // Aspire injects PORT=<assigned-port>
```

**`WithHttpsEndpoint()`** — same as above but for HTTPS.

**`WithEndpoint()`** — expose a non-HTTP endpoint (gRPC, TCP, custom protocols):

```csharp
var grpcService = builder.AddProject<Projects.GrpcService>("grpc")
    .WithEndpoint("grpc", endpoint =>
    {
        endpoint.Port = 5050;
        endpoint.Protocol = "grpc";
    });
```

**`WithExternalHttpEndpoints()`** — mark a resource's HTTP endpoints as externally visible. Use this for user-facing frontends so the URL appears prominently in the dashboard:

```csharp
var frontend = builder.AddNpmApp("frontend", "../frontend", "dev")
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();
```

**Port injection for non-.NET services**: Many frameworks (Express, Vite, Flask) need to know which port to listen on. Use the `env:` parameter:
- `withHttpEndpoint({ env: "PORT" })` (TypeScript)
- `.WithHttpEndpoint(env: "PORT")` (C#)

Aspire assigns a port and injects it as the specified environment variable. The service should read it and listen on that port.

### URL labels and dashboard niceties

Customize how endpoints appear in the Aspire dashboard:

```csharp
// Named endpoints for clarity
var api = builder.AddProject<Projects.Api>("api")
    .WithHttpEndpoint(name: "public", port: 8080)
    .WithHttpEndpoint(name: "internal", port: 8081);
```

**Custom domains with `dev.localhost`**: For a nicer local dev experience, use `WithUrlForEndpoint()` to give services friendly URLs that resolve to localhost:

```csharp
var frontend = builder.AddNpmApp("frontend", "../frontend", "dev")
    .WithHttpEndpoint(env: "PORT")
    .WithUrlForEndpoint("http", url => url.Host = "frontend.dev.localhost");

var api = builder.AddProject<Projects.Api>("api")
    .WithUrlForEndpoint("http", url => url.Host = "api.dev.localhost");
```

> Note: `*.dev.localhost` resolves to `127.0.0.1` on most systems without any `/etc/hosts` changes.

Use `aspire docs search "url for endpoint"` to check the latest API shape if unsure.

### Dependency ordering: `WaitFor` and `WaitForCompletion`

**`WaitFor()`** — delay starting a resource until another resource is healthy/ready:

```csharp
var db = builder.AddPostgres("pg").AddDatabase("mydb");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WaitFor(db);  // Don't start api until db is healthy
```

Always pair `WithReference()` with `WaitFor()` for infrastructure dependencies (databases, caches, queues). Services that depend on other services should generally also wait for them.

**`WaitForCompletion()`** — wait for a resource to run to completion (exit successfully). Use for init containers, database migrations, or seed data scripts:

```csharp
var migration = builder.AddProject<Projects.MigrationRunner>("migration")
    .WithReference(db)
    .WaitFor(db);

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WaitForCompletion(migration);  // Don't start until migration finishes
```

### Container lifetimes

By default, containers are stopped when the AppHost stops. Use **persistent lifetime** to keep containers running across restarts (useful for databases during development):

```csharp
var db = builder.AddPostgres("pg")
    .WithLifetime(ContainerLifetime.Persistent);
```

This prevents data loss when restarting the AppHost — the container stays running and the AppHost reconnects.

**TypeScript equivalent:**

```typescript
const db = await builder.addPostgres("pg")
    .withLifetime("persistent");
```

Recommend persistent lifetime for databases and caches during local development.

### Explicit start (manual start)

Some resources shouldn't auto-start with the AppHost. Mark them for explicit start:

```csharp
var debugTool = builder.AddContainer("profiler", "myregistry/profiler")
    .WithLifetime(ContainerLifetime.Persistent)
    .ExcludeFromManifest()
    .WithExplicitStart();
```

The resource appears in the dashboard but stays stopped until the user manually starts it. Useful for debugging tools, admin UIs, or optional services.

### Parent resources (grouping in the dashboard)

Group related resources under a parent for a cleaner dashboard:

```csharp
var postgres = builder.AddPostgres("pg");
var ordersDb = postgres.AddDatabase("orders");
var inventoryDb = postgres.AddDatabase("inventory");
// ordersDb and inventoryDb appear nested under pg in the dashboard
```

This happens automatically for databases added to a server resource. For custom grouping of arbitrary resources, use `WithParentRelationship()`:

```csharp
var backend = builder.AddResource(new ContainerResource("backend-group"));
var api = builder.AddProject<Projects.Api>("api")
    .WithParentRelationship(backend);
var worker = builder.AddProject<Projects.Worker>("worker")
    .WithParentRelationship(backend);
```

Use `aspire docs search "parent relationship"` to verify the current API shape.

### Volumes and data persistence

```csharp
// Named volume (managed by Docker, persists across container recreations)
var db = builder.AddPostgres("pg")
    .WithDataVolume("pg-data");

// Bind mount (maps to a host directory)
var db = builder.AddPostgres("pg")
    .WithBindMount("./data/pg", "/var/lib/postgresql/data");
```

```typescript
const db = await builder.addPostgres("pg")
    .withDataVolume("pg-data");
```

