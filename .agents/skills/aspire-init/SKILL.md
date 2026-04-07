---
name: aspire-init
description: "One-time skill for completing Aspire initialization after `aspire init` has dropped the skeleton AppHost and aspire.config.json. This skill scans the repository, discovers projects, wires up the AppHost (TypeScript or C#), configures dependencies and OpenTelemetry, validates that `aspire start` works, and self-removes on success."
---

# Aspire Init

This is a **one-time setup skill**. It completes the Aspire initialization that `aspire init` started. After this skill finishes successfully, it should be deleted — the evergreen `aspire` skill handles ongoing AppHost work.

Keep this as **one skill with context-specific references**. Load the reference files that match the repo you discover instead of trying to keep every edge case in the main document.

## Guiding principles

### Minimize changes to the user's code

The default stance is **adapt the AppHost to fit the app, not the other way around**. The user's services already work — the goal is to model them in Aspire without breaking anything.

- Prefer `WithEnvironment()` to match existing env var names over asking users to rename vars in their code
- Prefer Aspire-managed ports (`WithHttpsEndpoint(env: "PORT")`, `WithHttpEndpoint(env: "PORT")`, or no explicit port when supported) over fixed ports
- Only preserve a specific port when the user confirms it is actually significant (for example: external callbacks, OAuth redirect URIs, browser extensions, webhooks, or a repo-documented hard requirement)
- Map existing `docker-compose.yml` config 1:1 before optimizing
- Don't restructure project directories, rename files, or change build scripts

### Surface tradeoffs, don't decide silently

Sometimes a small code change unlocks significantly better Aspire integration. When this happens, **present the tradeoff to the user and let them decide**. Examples:

- **Connection strings**: A service reads `DATABASE_URL` but Aspire injects `ConnectionStrings__mydb`. You can use `WithEnvironment("DATABASE_URL", db.Resource.ConnectionStringExpression)` (zero code change) or suggest the service reads from config so `WithReference(db)` just works (enables service discovery, health checks, auto-retry).
  → Ask: *"Your API reads DATABASE_URL. I can map that with WithEnvironment (no code change) or you could switch to reading ConnectionStrings:mydb which unlocks WithReference and automatic service discovery. Which do you prefer?"*

- **Port binding**: A service hardcodes `PORT=3000`. You can preserve that with `WithHttpsEndpoint(port: 3000)` (zero code change) or switch the service to read `PORT` from env so Aspire can manage ports dynamically and avoid conflicts.
  → Ask: *"Your frontend is currently fixed to port 3000. Unless that exact port is important for something external, I recommend switching it to read PORT from env so Aspire can manage the port and avoid conflicts. If you need 3000 to stay stable, I can preserve it. Which do you want?"*

- **OTel setup**: Service has its own tracing config pointing to Jaeger. You can leave it (Aspire won't show its traces) or suggest switching the exporter to read `OTEL_EXPORTER_OTLP_ENDPOINT` (which Aspire injects).
  → Ask: *"Your API exports traces to Jaeger directly. I can leave that, or switch it to use the OTEL_EXPORTER_OTLP_ENDPOINT env var so traces show up in the Aspire dashboard. The Jaeger endpoint would still work in non-Aspire environments. Want me to update it?"*

**Format for presenting tradeoffs:**
1. Explain what the current code does
2. Show the zero-change option and what it gives you
3. Show the small-change option and the extra benefits
4. Ask which they prefer
5. If they decline the change, implement the zero-change option without complaint

### When in doubt, ask

If you're unsure whether something is a service, whether two services depend on each other, whether a port is truly significant, or whether a Docker Compose service should be modeled — ask. Don't guess at architectural intent.

### Always use latest Aspire APIs — verify before you write

**Do not assume APIs exist.** Aspire evolves fast. Before writing any AppHost code, look up the correct API. Follow this **tiered preference** when choosing how to model a resource:

#### Tier 1: First-party Aspire hosting packages (always prefer)

Packages named `Aspire.Hosting.*` — these are maintained by the Aspire team and ship with every release. Examples:

| Package | Unlocks |
|---------|---------|
| `Aspire.Hosting.Python` | `AddPythonApp()`, `AddUvicornApp()` |
| `Aspire.Hosting.JavaScript` | `AddJavaScriptApp()`, `AddNodeApp()`, `AddViteApp()`, `.WithYarn()`, `.WithPnpm()` |
| `Aspire.Hosting.PostgreSQL` | `AddPostgres()`, `AddDatabase()` |
| `Aspire.Hosting.Redis` | `AddRedis()` |

#### Tier 2: Community Toolkit packages (use when no first-party exists)

Packages named `CommunityToolkit.Aspire.Hosting.*` — maintained by the community, documented on aspire.dev, and installable via `aspire add`. Examples:

| Package | Unlocks |
|---------|---------|
| `CommunityToolkit.Aspire.Hosting.Golang` | `AddGolangApp()` — handles `go run .`, working dir, PORT env |
| `CommunityToolkit.Aspire.Hosting.Rust` | `AddRustApp()` |
| `CommunityToolkit.Aspire.Hosting.Java` | Java hosting support |

These provide typed APIs with proper endpoint handling, health checks, and dashboard integration — significantly better than raw executables.

#### Tier 3: Raw fallbacks (last resort)

`AddExecutable()`, `AddDockerfile()`, `AddContainer()` — use only when no Tier 1 or Tier 2 package exists for the technology, or when the user's setup is too custom for a typed integration.

#### How to discover available packages

Before writing any builder call:

1. Run `aspire docs search "<technology>"` (e.g., `aspire docs search "golang"`, `aspire docs search "python"`)
2. Run `aspire docs get "<slug>"` to read the full API surface and installation instructions
3. Run `aspire list integrations` to see all available packages (requires Aspire MCP — if unavailable, rely on docs search)
4. Install with `aspire add <integration-name>` (e.g., `aspire add communitytoolkit-golang`)
5. For TypeScript, run `aspire restore` then check `.modules/aspire.ts` to see what's available

**Don't invent APIs** — if the docs search and integration list don't return it, it doesn't exist. Fall back to Tier 3 and note the limitation to the user.

**API shapes differ between C# and TypeScript** — always check the correct language docs.

### Choosing the right JavaScript resource type

The `Aspire.Hosting.JavaScript` package provides three resource types. Pick the right one:

| Signal | Use | Example |
|--------|-----|---------|
| Vite app (has `vite.config.*`) | `AddViteApp(name, dir)` | Frontend SPA, Vite + React/Vue/Svelte |
| App runs via package.json script only | `AddJavaScriptApp(name, dir, { runScriptName })` | CRA app, Next.js, monorepo root scripts |
| App has a specific Node entry file (`.js`/`.ts`) and uses a dev script like `ts-node-dev` | `AddNodeApp(name, dir, "entry.js")` + `.WithRunScript("start:dev")` | Express/Fastify API, Socket.IO server |

**Key distinctions:**
- `AddNodeApp` is for apps that run a **specific file** with Node (e.g., an Express server at `src/index.ts`). Use `.WithRunScript("start:dev")` to override the dev-time command (e.g., `ts-node-dev`).
- `AddJavaScriptApp` runs a **package.json script** — simpler, good when the script handles everything.
- `AddViteApp` is `AddJavaScriptApp` with Vite-specific defaults (auto-HTTPS config augmentation, `dev` as default script).

### JavaScript dev scripts

Use `.WithRunScript()` to control which package.json script runs during development:

```typescript
// Express API with TypeScript: uses ts-node-dev for hot reload in dev
const api = await builder
    .addNodeApp("api", "./api", "src/index.ts")
    .withRunScript("start:dev")                      // runs "yarn start:dev" (ts-node-dev)
    .withYarn()
    .withHttpEndpoint({ env: "PORT" });

// Vite frontend: default "dev" script is fine, just add yarn
const web = await builder
    .addViteApp("web", "./frontend")
    .withYarn();
```

### Framework-specific port binding

Not all frameworks read ports from env vars the same way:

| Framework | Port mechanism | AppHost pattern |
|-----------|---------------|-----------------|
| Express/Fastify | `process.env.PORT` | `.withHttpEndpoint({ env: "PORT" })` |
| Vite | `--port` CLI arg or `server.port` in config | `.withHttpEndpoint({ env: "PORT" })` — Aspire's Vite integration handles this automatically |
| Next.js | `PORT` env or `--port` | `.withHttpEndpoint({ env: "PORT" })` |
| CRA | `PORT` env | `.withHttpEndpoint({ env: "PORT" })` |

When the framework supports reading the port from an env var or Aspire already handles it, **prefer that over pinning a fixed port**. Managed ports make repeated local runs more reliable and work better when multiple services or multiple Aspire apps are running.

**Suppress auto-browser-open:** Many dev servers (Vite, CRA, Next.js) auto-open a browser on start. Add `.withEnvironment("BROWSER", "none")` to prevent this in Aspire-managed apps. Vite also respects `server.open: false` in its config.

### Never call it ".NET Aspire"

Always refer to the product as just **Aspire**, never ".NET Aspire". This applies to all comments in generated AppHost code, messages to the user, and any documentation you produce.

### Dashboard URL must include auth token

When printing or displaying the Aspire dashboard URL to the user, always include the full login token query parameter. The dashboard requires authentication — a bare URL like `http://localhost:18888` won't work. Use the full URL as printed by `aspire start` (e.g., `http://localhost:18888/login?t=<token>`).

### Prefer HTTPS over HTTP

Always set up HTTPS endpoints by default. Use `WithHttpsEndpoint()` instead of `WithHttpEndpoint()` unless HTTPS doesn't work for a specific integration.

For JavaScript and Python apps, call `WithHttpsDeveloperCertificate()` to configure the ASP.NET Core dev cert for serving HTTPS. Some apps may also need `WithDeveloperCertificateTrust(true)` so they trust the dev cert for outbound calls (e.g., to the dashboard OTLP collector). If HTTPS causes issues for a specific resource, fall back to HTTP and leave a comment explaining why.

```csharp
// JavaScript/Vite — HTTPS with dev cert
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithHttpsDeveloperCertificate()
    .WithHttpsEndpoint(env: "PORT");

// Python — HTTPS with dev cert
var pyApi = builder.AddUvicornApp("py-api", "../py-api", "app:main")
    .WithHttpsDeveloperCertificate();

// .NET — HTTPS works out of the box, no extra config needed
var api = builder.AddCSharpApp("api", "../src/Api");
```

> **Note**: These certificate APIs are experimental (`ASPIRECERTIFICATES001`). Use `aspire docs search "certificate configuration"` to check the latest API shape. If `WithHttpsDeveloperCertificate` causes errors for a resource type, fall back to `WithHttpEndpoint()`.

### Never hardcode URLs — use endpoint references

When a service needs another service's URL as an environment variable, **always** pass an endpoint reference — never a hardcoded string. Hardcoded URLs break whenever Aspire assigns different ports.

```typescript
// ✅ CORRECT — endpoint reference, Aspire resolves the actual URL at runtime
const roomEndpoint = await room.getEndpoint("http");
builder.addViteApp("frontend", "./frontend")
    .withEnvironment("VITE_APP_WS_SERVER_URL", roomEndpoint);

// ❌ WRONG — hardcoded URL, breaks when ports change
builder.addViteApp("frontend", "./frontend")
    .withEnvironment("VITE_APP_WS_SERVER_URL", "http://localhost:3002");
```

Similarly, **never use `withUrlForEndpoint` / `WithUrlForEndpoint` to set `dev.localhost` URLs**. That API is ONLY for setting display labels in the dashboard (e.g., `url.DisplayText = "Web UI"`). `dev.localhost` configuration belongs in `aspire.config.json` profiles — see Step 9.

### Optimize for local dev, not deployment

This skill is about getting a great **local development experience**. Don't worry about production deployment manifests, cloud provisioning, or publish configuration — that's a separate concern for later.

This means:

- Prefer `ContainerLifetime.Persistent` for databases and caches so data survives AppHost restarts
- Use `WithDataVolume()` to persist data across container recreations
- Cookie and session isolation with `*.dev.localhost` subdomains is encouraged
- Don't add production health check probes, scaling config, or cloud resource definitions
- If services reference external third-party APIs/services (e.g., a hardcoded Stripe URL, an external database host, a SaaS webhook endpoint), consider modeling those as parameters or connection strings in the AppHost so they're visible and configurable from one place:

```csharp
// Instead of the service hardcoding "https://api.stripe.com"
var stripeUrl = builder.AddParameter("stripe-url", secret: false);
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithEnvironment("STRIPE_API_URL", stripeUrl);
```

This makes the external dependency visible in the dashboard and lets developers easily swap endpoints (e.g., to a Stripe test endpoint) without digging through service code. Present this as an option to the user — don't silently refactor their external service calls.

### Migrate `.env` files into AppHost parameters

Many projects use `.env` files for configuration. These should be migrated into the AppHost so that all config is centralized and visible in the dashboard. Scan for `.env`, `.env.local`, `.env.development`, etc. and propose migrating their contents:

- **Secrets** (API keys, tokens, passwords, connection strings): use `AddParameter(name, secret: true)`. Aspire stores these securely via user secrets and prompts the developer to set them.
- **Non-secret config** (feature flags, URLs, mode settings): use `AddParameter(name, secret: false)` with a default value, or `WithEnvironment()` directly.
- **Values that map to Aspire resources** (e.g., `DATABASE_URL=postgres://...`, `REDIS_URL=redis://...`): replace with actual Aspire resources (`AddPostgres`, `AddRedis`) and `WithReference()` — the connection string is then managed by Aspire.

```csharp
// Before: .env file with DATABASE_URL=postgres://user:pass@localhost:5432/mydb
//         STRIPE_KEY=sk_test_abc123
//         DEBUG=true

// After: modeled in AppHost
var db = builder.AddPostgres("pg").AddDatabase("mydb");
var stripeKey = builder.AddParameter("stripe-key", secret: true);

var api = builder.AddCSharpApp("api", "../src/Api")
    .WithReference(db)                              // replaces DATABASE_URL
    .WithEnvironment("STRIPE_KEY", stripeKey)       // secret, stored securely
    .WithEnvironment("DEBUG", "true");              // plain config
```

**The goal is to make `.env` files unnecessary** so all configuration flows through the AppHost. This means:
- No more "did you copy the .env.example?" onboarding friction
- Secrets are stored securely (not in plaintext files that get accidentally committed)
- All service config is visible in one place (the dashboard)

**Important: Never delete `.env` files automatically.** After migrating all values into the AppHost, explicitly ask the user:
> "I've migrated all the values from your `.env` file into the AppHost. The `.env` file is no longer needed for running via Aspire, but it still works for non-Aspire workflows. Would you like me to remove it, or keep it around?"

Some teams still need `.env` files for CI, Docker Compose, or developers who haven't switched to Aspire yet. Respect that.

Present this as a recommendation. Walk through the `.env` contents with the user and classify each variable together. Some values may be intentionally local-only and the user may prefer to keep them — that's fine.

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
- **Full project mode**: `appHost.path` points to a directory containing a `.csproj` and `apphost.cs`. This was created because a `.sln`/`.slnx` was found — full project mode is required so the AppHost can be opened in Visual Studio alongside the rest of the solution.

Check which mode you're in by looking at what exists at the `appHost.path` location.

If you're in **full project mode**, also load [references/full-solution-apphosts.md](references/full-solution-apphosts.md). It covers:

- mixed-SDK solution boundaries
- when to add or avoid solution membership
- ServiceDefaults in solution-backed repos
- legacy `Program.cs` / `Startup.cs` / `IHostBuilder` migration decisions
- validation specific to `.csproj` AppHosts

## Workflow

Follow these steps in order. If any step fails, diagnose and fix before continuing. **The goal is a working `aspire start` — keep going until every resource starts cleanly and the dashboard is accessible. Do not stop at partial success.**

### Step 1: Scan the repository

Analyze the repository to discover all projects and services that could be modeled in the AppHost.

**What to look for:**

- **.NET projects**: `*.csproj` files. For each, run:
  - `dotnet msbuild <project> -getProperty:OutputType` — `Exe`/`WinExe` = runnable service, `Library` = skip
  - `dotnet msbuild <project> -getProperty:TargetFramework` — must be `net8.0` or newer
  - `dotnet msbuild <project> -getProperty:IsAspireHost` — skip if `true`
- **Solution files**: `*.sln` or `*.slnx` — if found, the C# AppHost **must** use full project mode (with `.csproj`) so it can be opened in Visual Studio alongside the rest of the solution. This is a hard requirement.
- **Node.js/TypeScript apps**: directories with `package.json` containing a `start`, `dev`, or `main`/`module` entry. For each, also check:
  - Does it have a `vite.config.*` file? → use `AddViteApp`
  - Does it have a specific entry file (e.g., `src/index.ts`, `server.js`) and a `build` script that compiles TypeScript? → use `AddNodeApp` with `.WithRunScript()` and `.WithBuildScript()`
  - Otherwise → use `AddJavaScriptApp`
- **Monorepo/workspace detection**: Check root `package.json` for `"workspaces"` field (Yarn/npm) or `pnpm-workspace.yaml` (pnpm). If this is a monorepo:
  - **Map workspace packages** — each workspace with a runnable script (`start`, `dev`) is a potential Aspire resource
  - **Root scripts that delegate** — some monorepos have root-level scripts like `"start": "yarn --cwd ./subdir start"`. Model the *actual app directory* as the resource, not the root
  - **Path resolution** — `appDirectory` is relative to the AppHost location. In monorepos you often need `../`, `../../`, or similar paths. Double-check these
  - **Shared dependencies** — `.WithYarn()` / `.WithPnpm()` on each resource handles workspace-aware installs automatically
- **Python apps**: directories with `pyproject.toml`, `requirements.txt`, or `main.py`/`app.py`
- **Go apps**: directories with `go.mod`
- **Java apps**: directories with `pom.xml` or `build.gradle`
- **Dockerfiles**: standalone `Dockerfile` entries representing services
- **Docker Compose**: `docker-compose.yml` or `compose.yml` files — these are a goldmine. Parse them to extract:
  - **Services**: each named service maps to a potential AppHost resource
  - **Images**: container images used (e.g., `postgres:16`, `redis:7`) → these become `AddContainer()` or typed Aspire integrations (e.g., `AddPostgres()`, `AddRedis()`)
  - **Ports**: published port mappings → `WithHttpsEndpoint()` or `WithEndpoint()`
  - **Environment variables**: env vars and `.env` file references → `WithEnvironment()`
  - **Volumes**: named/bind volumes → `WithVolume()` or `WithBindMount()`
  - **Dependencies**: `depends_on` → `WithReference()` and `WaitFor()`
  - **Build contexts**: `build:` entries → `AddDockerfile()` pointing to the build context directory
  - Prefer typed Aspire integrations over raw `AddContainer()` when the image matches a known integration (use `aspire docs search` to check). For example, `postgres:16` → `AddPostgres()`, `redis:7` → `AddRedis()`, `rabbitmq:3` → `AddRabbitMQ()`.
- **Static frontends**: Vite, Next.js, Create React App, or other frontend framework configs
- **`.env` files**: Scan for `.env`, `.env.local`, `.env.development`, `.env.example`, etc. These contain configuration that should be migrated into AppHost parameters (see Guiding Principles above)
- **Package manager**: Detect which Node.js package manager the repo uses by looking for lock files: `pnpm-lock.yaml` → pnpm, `yarn.lock` → yarn, `package-lock.json` or none → npm. Use the detected package manager for all install/run commands throughout this skill.

**Ignore:**

- The AppHost directory/file itself
- `node_modules/`, `.modules/`, `dist/`, `build/`, `bin/`, `obj/`, `.git/`
- Test projects (directories named `test`/`tests`/`__tests__`, projects referencing xUnit/NUnit/MSTest, or test-only package.json scripts)

### Step 2: Smoke-test the skeleton

Before investing time in wiring, verify that the Aspire skeleton boots correctly:

```bash
aspire start
```

The empty AppHost should start successfully — the dashboard should come up and the process should run without errors. You won't see any resources yet (that's expected), but if `aspire start` fails here, the problem is in the generated `aspire.config.json` or the skeleton AppHost file. Fix the issue before proceeding.

Common failures at this stage:

- **Missing profiles in `aspire.config.json`**: The file must have a `profiles` section with `applicationUrl`. Re-run `aspire init` to regenerate it.
- **Missing dependencies**: For TypeScript, ensure `@aspect/aspire-hosting` or the `.modules/aspire.js` SDK is available. Run `aspire restore` if needed.
- **Port conflicts**: If another Aspire app is running, the randomly assigned ports may conflict. Stop other instances first.

Once it boots, stop it (Ctrl+C) and continue.

### Step 3: Present findings and confirm with the user

Show the user what you found. For each discovered project/service, show:

- Name (project or directory name)
- Type (.NET service, Node.js app, Python app, Dockerfile, etc.)
- Framework/runtime info (e.g., net10.0, Node 20, Python 3.12)
- Whether it exposes HTTP endpoints

Ask the user:

1. Which projects to include in the AppHost (pre-select all discovered runnable services)
2. For C# AppHosts: which .NET projects should receive ServiceDefaults references (pre-select all .NET services)

### Step 4: Create ServiceDefaults (C# only)

> **Skip this step for TypeScript AppHosts.** OTel is handled in Step 8.

If the AppHost is in **full project mode**, consult [references/full-solution-apphosts.md](references/full-solution-apphosts.md) before making ServiceDefaults changes. Some existing solutions need bootstrap updates before `AddServiceDefaults()` and `MapDefaultEndpoints()` can be applied safely.

If no ServiceDefaults project exists in the repo, create one:

```bash
dotnet new aspire-servicedefaults -n <SolutionName>.ServiceDefaults -o <path>
```

Place it alongside the AppHost (e.g., `src/` or solution root). If a `.sln` exists, add it:

```bash
dotnet sln <solution> add <ServiceDefaults.csproj>
```

If a ServiceDefaults project already exists (look for references to `Microsoft.Extensions.ServiceDiscovery` or `Aspire.ServiceDefaults`), skip creation and use the existing one.

### Step 5: Wire up the AppHost

Edit the skeleton AppHost file to add resource definitions for each selected project. Use the appropriate syntax based on language.

#### TypeScript AppHost (`apphost.ts`)

```typescript
import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Express/Node.js API with TypeScript — needs build for publish
const api = await builder
    .addNodeApp("api", "./api", "dist/index.js")   // production entry point
    .withRunScript("start:dev")                      // dev: runs ts-node-dev or similar
    .withBuildScript("build")                        // publish: compiles TS first
    .withYarn()                                      // or .withPnpm() — match the repo
    .withHttpsDeveloperCertificate()
    .withHttpsEndpoint({ env: "PORT" });

// Vite frontend — HTTPS with dev cert, suppress auto-browser
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withBuildScript("build")
    .withYarn()
    .withHttpsDeveloperCertificate()
    .withEnvironment("BROWSER", "none")              // prevent auto-opening browser
    .withReference(api)
    .waitFor(api);

// .NET project — HTTPS works out of the box
const dotnetSvc = await builder
    .addCSharpApp("catalog", "./src/Catalog");

// Dockerfile-based service
const worker = await builder
    .addDockerfile("worker", "./worker");

// Python app — HTTPS with dev cert
const pyApi = await builder
    .addPythonApp("py-api", "./py-api", "app.py")
    .withHttpsDeveloperCertificate();

await builder.build().run();
```

#### C# AppHost — single-file mode (`apphost.cs`)

```csharp
#:sdk Aspire.AppHost.Sdk@<version>
#:property IsAspireHost=true

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddCSharpApp("api", "../src/Api");

var web = builder.AddCSharpApp("web", "../src/Web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
```

#### C# AppHost — full project mode (`apphost.cs` + `.csproj`)

Edit `apphost.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddCSharpApp("api", "../src/Api");

var web = builder.AddCSharpApp("web", "../src/Web")
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
// Node.js app (Tier 1: Aspire.Hosting.JavaScript) — HTTPS with dev cert
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithHttpsDeveloperCertificate();

// Python app (Tier 1: Aspire.Hosting.Python) — HTTPS with dev cert
var pyApi = builder.AddPythonApp("py-api", "../py-api", "app.py")
    .WithHttpsDeveloperCertificate();

// Go app (Tier 2: CommunityToolkit.Aspire.Hosting.Golang)
var goApi = builder.AddGolangApp("go-api", "../go-api")
    .WithHttpsEndpoint(env: "PORT");

// Dockerfile-based service (Tier 3: fallback for unsupported languages)
var worker = builder.AddDockerfile("worker", "../worker");
```

Add required hosting packages — use `aspire add` or `dotnet add package`:

```bash
# Tier 1: first-party
aspire add javascript    # or: dotnet add <AppHost.csproj> package Aspire.Hosting.JavaScript
aspire add python        # or: dotnet add <AppHost.csproj> package Aspire.Hosting.Python

# Tier 2: community toolkit
aspire add communitytoolkit-golang
# or: dotnet add <AppHost.csproj> package CommunityToolkit.Aspire.Hosting.Golang
```

Always check `aspire list integrations` and `aspire docs search "<language>"` to find the best available integration before falling back to `AddExecutable`/`AddDockerfile`.

**Important rules:**

- Use `aspire docs search` and `aspire docs get` to look up the correct builder API for each resource type. Do not guess API shapes.
- Check `.modules/aspire.ts` (TypeScript) or NuGet package APIs (C#) to confirm available methods.
- Use meaningful resource names derived from the project/directory name.
- Wire up `WithReference()`/`withReference()` and `WaitFor()`/`waitFor()` for services that depend on each other (ask the user if relationships are unclear).
- Use `WithExternalHttpEndpoints()`/`withExternalHttpEndpoints()` for user-facing frontends.

### Step 6: Configure dependencies

#### TypeScript AppHost

**package.json** — if one exists at the root, augment it (do not overwrite). Add/merge these scripts that delegate to the Aspire CLI:

```json
{
  "type": "module",
  "scripts": {
    "dev": "aspire run",
    "build": "tsc",
    "watch": "tsc --watch"
  }
}
```

If no root `package.json` exists, create a minimal one matching the canonical Aspire template:

```json
{
  "name": "<repo-name>",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "aspire run",
    "build": "tsc",
    "watch": "tsc --watch"
  },
  "engines": {
    "node": "^20.19.0 || ^22.13.0 || >=24"
  }
}
```

**Important**: Scripts should point to `aspire run`/`aspire start` — the Aspire CLI handles TypeScript compilation internally. Do not use `npx tsc && node apphost.js` patterns.

Never overwrite existing `scripts`, `dependencies`, or `devDependencies` — merge only. Do not manually add Aspire SDK packages — `aspire restore` handles those.

Run `aspire restore` to generate the `.modules/` directory with TypeScript SDK bindings, then install dependencies with the repo's package manager (`npm install`, `pnpm install`, or `yarn`).

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

### Step 7: Add ServiceDefaults to .NET projects (C# AppHost only)

> **Skip this step for TypeScript AppHosts.**

If any selected .NET service still uses a legacy `IHostBuilder` / `Startup.cs` bootstrap, consult [references/full-solution-apphosts.md](references/full-solution-apphosts.md) before editing it. Do not assume ServiceDefaults can be dropped into old hosting patterns unchanged.

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

### Step 8: Wire up OpenTelemetry

OpenTelemetry makes your services' traces, metrics, and logs visible in the Aspire dashboard. For .NET services, ServiceDefaults handles this automatically. For everything else, the services need a small setup to export telemetry. Aspire automatically injects `OTEL_EXPORTER_OTLP_ENDPOINT` into all managed resources — the services just need to read it.

**Present this to the user as an option, not a mandatory step.** Some users may want to add OTel later, and that's fine — their services will still run, they just won't appear in the dashboard's trace/metrics views.

**For each service that doesn't already have OTel, ask:**
> "Would you like me to add OpenTelemetry instrumentation to `<service>`? This lets the Aspire dashboard show its traces, metrics, and logs. I'll need to add a few packages and an instrumentation setup file."

If they say yes, follow the per-language guide below.

#### Node.js/TypeScript services

```bash
# Use the repo's package manager (npm/pnpm/yarn)
npm install @opentelemetry/sdk-node @opentelemetry/auto-instrumentations-node @opentelemetry/exporter-otlp-grpc
# or: pnpm add ...
# or: yarn add ...
```

Create an instrumentation file (e.g., `instrumentation.ts` or `instrumentation.js`):

```typescript
import { NodeSDK } from '@opentelemetry/sdk-node';
import { getNodeAutoInstrumentations } from '@opentelemetry/auto-instrumentations-node';
import { OTLPTraceExporter } from '@opentelemetry/exporter-otlp-grpc';
import { OTLPMetricExporter } from '@opentelemetry/exporter-otlp-grpc';
import { PeriodicExportingMetricReader } from '@opentelemetry/sdk-metrics';

const sdk = new NodeSDK({
  traceExporter: new OTLPTraceExporter(),
  metricReader: new PeriodicExportingMetricReader({
    exporter: new OTLPMetricExporter(),
  }),
  instrumentations: [getNodeAutoInstrumentations()],
  serviceName: process.env.OTEL_SERVICE_NAME,
});

sdk.start();
```

Then ensure the service loads it early — either via `--require`/`--import` in the start script or by importing it as the first line of the entry point.

#### Python services

```bash
pip install opentelemetry-distro opentelemetry-exporter-otlp
opentelemetry-bootstrap -a install  # auto-detect and install framework instrumentations
```

Add to the service's startup (e.g., top of `main.py` or as a separate `instrumentation.py`):

```python
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry import trace, metrics
import os

resource = Resource.create({"service.name": os.environ.get("OTEL_SERVICE_NAME", "unknown")})

# Traces
trace.set_tracer_provider(TracerProvider(resource=resource))
trace.get_tracer_provider().add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))

# Metrics
metrics.set_meter_provider(MeterProvider(
    resource=resource,
    metric_readers=[PeriodicExportingMetricReader(OTLPMetricExporter())],
))
```

Or more simply, run with the auto-instrumentation wrapper:

```bash
opentelemetry-instrument uvicorn main:app --host 0.0.0.0
```

#### Go services

```bash
go get go.opentelemetry.io/otel
go get go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc
go get go.opentelemetry.io/otel/sdk/trace
go get go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp
```

Add initialization in `main()`:

```go
import (
    "go.opentelemetry.io/otel"
    "go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
    sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

func initTracer() func() {
    exporter, _ := otlptracegrpc.New(context.Background())
    tp := sdktrace.NewTracerProvider(sdktrace.WithBatcher(exporter))
    otel.SetTracerProvider(tp)
    return func() { tp.Shutdown(context.Background()) }
}
```

Wrap HTTP handlers with `otelhttp.NewHandler()` for automatic HTTP span creation.

#### Java services

Point the user to the [OpenTelemetry Java Agent](https://opentelemetry.io/docs/zero-code/java/agent/) — it's the easiest approach:

```bash
java -javaagent:opentelemetry-javaagent.jar -jar myapp.jar
```

The agent auto-instruments common frameworks. Aspire injects `OTEL_EXPORTER_OTLP_ENDPOINT` automatically.

### Step 9: Offer dev experience enhancements

Before validating, present the user with optional quality-of-life improvements. These aren't required for `aspire start` to work, but they make the local dev experience significantly nicer.

**Suggest each of these individually — don't apply without asking:**

1. **Cookie and session isolation with `dev.localhost`**: When multiple services run on `localhost`, they share cookies and session storage — which can cause hard-to-debug auth problems. Using `*.dev.localhost` subdomains isolates each service's cookies and storage. Note: URLs still include ports (e.g., `frontend.dev.localhost:5173`), but the subdomain isolation prevents cross-service cookie collisions.
   > "Would you like me to set up `dev.localhost` subdomains for your services? This gives each service its own cookie/session scope so they don't interfere with each other. URLs will look like `frontend.dev.localhost:5173` — the `*.dev.localhost` domain resolves to 127.0.0.1 automatically on most systems, no `/etc/hosts` changes needed."

   **How to do it:** Update the `profiles` section in `aspire.config.json` — replace `localhost` with `<projectname>.dev.localhost` in `applicationUrl`, and use descriptive subdomains like `otlp.dev.localhost` and `resources.dev.localhost` for the infrastructure URLs. This is the same mechanism `aspire new` uses.

   > ⚠️ **Do NOT use `withUrlForEndpoint` / `WithUrlForEndpoint` in the AppHost for `dev.localhost`** — the config file is the right place. `withUrlForEndpoint` is ONLY for dashboard display labels.

   Real-world example:

   ```json
   {
     "profiles": {
       "https": {
         "applicationUrl": "https://myproject.dev.localhost:17042;http://myproject.dev.localhost:15042",
         "environmentVariables": {
           "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://otlp.dev.localhost:21042",
           "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://resources.dev.localhost:22042"
         }
       }
     }
   }
   ```

   Use the project/repo name (lowercased) as the subdomain prefix for `applicationUrl`. Use `otlp` and `resources` for the infrastructure URLs. Keep the existing port numbers — just swap `localhost` for the appropriate `*.dev.localhost` subdomain.

2. **Custom URL labels in the dashboard** (display text only): Rename endpoint URLs in the Aspire dashboard for clarity. This is the ONLY valid use of `withUrlForEndpoint` — setting `DisplayText`, nothing else:
   ```csharp
   .WithUrlForEndpoint("https", url => url.DisplayText = "Web UI")
   ```
   Never set `url.Url` in this callback — that's what `aspire.config.json` profiles are for.

3. **OpenTelemetry** (if not done in Step 8): "Would you like me to add observability to your services so they appear in the Aspire dashboard's traces and metrics views?"

Present these as a batch: "I have a few optional dev experience improvements I can make. Want to hear about them?"

### Step 10: Validate

```bash
aspire start
```

Once the app is running, use the Aspire CLI to verify everything is wired up correctly:

1. **Resources are modeled**: `aspire describe` — confirm all expected resources appear with correct types, endpoints, and states.
2. **Environment flows correctly**: `aspire describe` — check that environment variables (connection strings, ports, secrets from parameters) are injected into each resource as expected. Verify `.env` values that were migrated to parameters are present.
3. **OTel is flowing** (if configured in Step 8): `aspire otel` — verify that services instrumented with OpenTelemetry are exporting traces and metrics to the Aspire dashboard collector.
4. **No startup errors**: `aspire logs <resource>` — check logs for each resource to ensure clean startup with no crashes, missing config, or connection failures.
5. **Dashboard is accessible**: Confirm the dashboard URL (including the login token) is printed and can be opened. The full URL looks like `http://localhost:18888/login?t=<token>` — always include the token.

**This skill is not done until `aspire start` runs without errors and every resource is in an expected terminal/runtime state.** Acceptable end states are:

- **Healthy / Running** for long-lived services
- **Finished** only for resources that were intentionally modeled as one-shot tasks (for example migrations or seed steps) **and** only if they exited cleanly with no errors
- **Not started** only when that is intentional and understood (for example, an optional resource the user chose not to run yet)

Treat these as failure states unless you intentionally designed for them:

- **Finished** for long-lived APIs, frontends, workers, or databases
- **Finished** after an exception, crash, or non-zero exit
- unhealthy, degraded, failed, or crash-looping resources

If anything lands in an unexpected state, diagnose it, fix it, and run `aspire start` again. Keep iterating until the app behaves as expected — do not move on to Step 11 with crash-shaped "success".

Once everything is healthy, print a summary for the user:

```
✅ Aspire init complete!

Dashboard: <full dashboard URL including login token>

Resources:
  <name>  <type>  <status>
  <name>  <type>  <status>
  ...

<any notes about optional steps skipped, e.g., "OTel not configured — run the aspire skill to add it later">
```

Get the dashboard URL from `aspire start` output (always include the `?t=<token>` parameter). Get resource status from `aspire describe`. If any resource shows `Finished`, confirm from logs that it was an intentional one-shot resource that exited successfully before including it as success. This summary is the user's confirmation that init worked — make it complete and accurate.

Common issues:

- **TypeScript**: missing dependency install, TS compilation errors, port conflicts
- **C# project mode**: missing project references, NuGet restore needed, TFM mismatches, build errors
- **C# single-file**: `#:project` paths wrong, missing SDK directive
- **Both**: missing environment variables, port conflicts
- **Certificate errors**: if HTTPS fails, run `aspire certs trust` and retry

### Step 11: Update solution file (C# full project mode only)

If a `.sln`/`.slnx` exists, verify all new projects are included:

```bash
dotnet sln <solution> list
```

Ensure both the AppHost and ServiceDefaults projects appear.

### Step 12: Clean up

After successful validation:

1. **Leave the AppHost running** — the user gets a fully running app with the dashboard open. Do not call `aspire stop`.
2. **Delete this skill** — remove the `aspire-init/` skill directory from all locations where it was installed (check `.agents/skills/`, `.github/skills/`, `.claude/skills/`)
3. Confirm the evergreen `aspire` skill is present for ongoing AppHost work

## Key rules

- **Never overwrite existing files** — always augment/merge
- **Ask the user before modifying service code** (especially OTel and ServiceDefaults injection)
- **Respect existing project structure** — don't reorganize the repo
- **This is a one-time skill** — delete it after successful init
- **If stuck, use `aspire doctor`** to diagnose environment issues
- **Never hardcode URLs in `withEnvironment`** — when a service needs another service's URL (e.g., `VITE_APP_WS_SERVER_URL`), pass an endpoint reference, NOT a string literal. Use `room.getEndpoint("http")` (TS) or `room.GetEndpoint("http")` (C#) and pass that to `withEnvironment`. Hardcoded URLs break when ports change.
- **Never use `withUrlForEndpoint` to set `dev.localhost` URLs** — `dev.localhost` configuration belongs in `aspire.config.json` profiles, not in AppHost code. `withUrlForEndpoint` is ONLY for setting display labels (e.g., `url.DisplayText = "Web UI"`).

## Looking up APIs and integrations

Before writing AppHost code for an unfamiliar resource type or integration, **always** look it up. Follow the tiered preference from the principles section (first-party → community toolkit → raw fallbacks).

```bash
# Search for documentation on a topic
aspire docs search "redis"
aspire docs search "golang"
aspire docs search "python uvicorn"

# Get a specific doc page by slug (returned from search results)
aspire docs get "redis-integration"
aspire docs get "go-integration"

# List ALL available integrations (first-party and community toolkit)
# Note: requires the Aspire MCP server to be connected. If this fails, use aspire docs search instead.
aspire list integrations
```

Use `aspire docs search` to find the right builder methods, configuration options, and patterns. Use `aspire docs get <slug>` to read the full doc page. Use `aspire list integrations` to discover packages you might not have known about. Do not guess API shapes — Aspire has many resource types with specific overloads.

To add an integration package (which unlocks typed builder methods):

```bash
# First-party
aspire add redis
aspire add python
aspire add nodejs

# Community Toolkit
aspire add communitytoolkit-golang
aspire add communitytoolkit-rust
```

After adding, run `aspire restore` (TypeScript) or `dotnet restore` (C#) to update available APIs, then check what methods are now available.

**Always prefer a typed integration over raw `AddExecutable`/`AddContainer`.** Typed integrations handle working directories, port injection, health checks, and dashboard integration automatically.

## References

- For solution-backed C# AppHosts (`.sln`/`.slnx` + `.csproj` AppHost), see [references/full-solution-apphosts.md](references/full-solution-apphosts.md).

## AppHost wiring reference

This section covers the patterns you'll need when writing Step 5 (Wire up the AppHost). Refer back to it as needed.

### Service communication: `WithReference` vs `WithEnvironment`

**`WithReference()`** is the primary way to connect services. It does two things:

1. Injects the referenced resource's connection information (connection string or URL) into the consuming service
2. Enables Aspire service discovery — .NET services can resolve the referenced resource by name

```csharp
// C#: api gets the database connection string injected automatically
var db = builder.AddPostgres("pg").AddDatabase("mydb");
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithReference(db);

// C#: frontend gets service discovery URL for api
var frontend = builder.AddCSharpApp("web", "../src/Web")
    .WithReference(api);
```

```typescript
// TypeScript equivalent
const db = await builder.addPostgres("pg").addDatabase("mydb");
const api = await builder.addCSharpApp("api", "./src/Api")
    .withReference(db);
```

**How services consume references**: Services receive connection info as environment variables. The naming convention is:
- Connection strings: `ConnectionStrings__<resourceName>` (e.g., `ConnectionStrings__mydb=Host=...`)
- Service URLs: `services__<resourceName>__<endpointName>__0` (e.g., `services__api__http__0=http://localhost:5123`)

**`WithEnvironment()`** injects raw environment variables. Use this for custom config that isn't a service reference:

```csharp
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithEnvironment("FEATURE_FLAG_X", "true")
    .WithEnvironment("API_KEY", someParameter);
```

**When to use which:**
- Connecting service A to service B or a database/cache/queue → `WithReference()`
- Passing configuration values, feature flags, API keys → `WithEnvironment()`
- Never manually construct connection strings with `WithEnvironment()` when `WithReference()` would work

### Endpoints and ports

**Prefer HTTPS by default.** Use `WithHttpsEndpoint()` for all services and fall back to `WithHttpEndpoint()` only if HTTPS doesn't work for that resource.

**Prefer Aspire-managed ports by default.** For most local development scenarios, let Aspire assign the port and inject it into the service. This avoids port collisions, makes multiple AppHosts easier to run side-by-side, and keeps cross-service wiring flexible.

**Ask before pinning a fixed port.** If the repo already uses a hardcoded port, do **not** silently preserve it just because it exists. Ask whether that port is actually required. Good reasons to keep a fixed port include:

- OAuth/callback URLs or external webhooks that expect a stable local address
- Browser extensions or desktop/mobile clients that are already hardcoded to a specific port
- Repo docs, scripts, or test tooling that explicitly depend on that exact port

If none of those apply, steer the user toward managed ports.

**`WithHttpsEndpoint()`** — expose an HTTPS endpoint. For services that serve traffic:

```csharp
// Let Aspire assign a random port (recommended for most cases)
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithHttpsEndpoint();

// Use a specific port only when the user confirms it is required
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithHttpsEndpoint(port: 5001);

// For services that read the port from an env var
var nodeApi = builder.AddJavaScriptApp("api", "../api", "start")
    .WithHttpsDeveloperCertificate()
    .WithHttpsEndpoint(env: "PORT");  // Aspire injects PORT=<assigned-port>
```

**`WithHttpsDeveloperCertificate()`** — required for JavaScript and Python apps to serve HTTPS. Configures the ASP.NET Core dev cert. .NET apps handle this automatically.

```csharp
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithHttpsDeveloperCertificate();

var pyApi = builder.AddUvicornApp("api", "../api", "app:main")
    .WithHttpsDeveloperCertificate();
```

> If `WithHttpsDeveloperCertificate()` causes issues for a resource, fall back to `WithHttpEndpoint()` and leave a comment explaining why.

**`WithHttpEndpoint()`** — fallback for HTTP when HTTPS doesn't work:

```csharp
// Use when HTTPS causes issues with a specific integration
var legacy = builder.AddJavaScriptApp("legacy", "../legacy", "start")
    .WithHttpEndpoint(env: "PORT");  // HTTP fallback
```

**`WithEndpoint()`** — expose a non-HTTP endpoint (gRPC, TCP, custom protocols):

```csharp
var grpcService = builder.AddCSharpApp("grpc", "../src/GrpcService")
    .WithEndpoint("grpc", endpoint =>
    {
        endpoint.Port = 5050;
        endpoint.Protocol = "grpc";
    });
```

**`WithExternalHttpEndpoints()`** — mark a resource's HTTP endpoints as externally visible. Use this for user-facing frontends so the URL appears prominently in the dashboard:

```csharp
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithHttpsDeveloperCertificate()
    .WithHttpsEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();
```

**Port injection**: Many frameworks (Express, Vite, Flask) need to know which port to listen on. Use the `env:` parameter:
- `withHttpsEndpoint({ env: "PORT" })` (TypeScript)
- `.WithHttpsEndpoint(env: "PORT")` (C#)

Aspire assigns a port and injects it as the specified environment variable. The service should read it and listen on that port.

**Recommended ask when a repo already hardcodes ports:**

> "I found this service pinned to port 3000 today. Unless that exact port is needed for an external callback or another hard requirement, I recommend switching it to read PORT from env and letting Aspire manage the port. That avoids collisions and makes the AppHost more portable. Should I keep 3000 or make it Aspire-managed?"

### Cross-service environment variable wiring

When a service expects a **specific env var name** for a dependency's URL (not the standard `services__` format from `WithReference`), use `WithEnvironment` with an endpoint reference — **never a hardcoded string**:

```typescript
// ✅ CORRECT — endpoint reference resolves to the actual URL at runtime
const roomEndpoint = await room.getEndpoint("http");

const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withEnvironment("VITE_APP_WS_SERVER_URL", roomEndpoint)  // EndpointReference, not a string
    .withReference(room)   // also sets up standard service discovery
    .waitFor(room);

// ❌ WRONG — hardcoded URL breaks when Aspire assigns different ports
    .withEnvironment("VITE_APP_WS_SERVER_URL", "http://localhost:3002")  // NEVER DO THIS
```

```csharp
// C# equivalent
var roomEndpoint = room.GetEndpoint("http");
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEnvironment("VITE_APP_WS_SERVER_URL", roomEndpoint)
    .WithReference(room)
    .WaitFor(room);
```

Use `WithEnvironment(name, endpointRef)` when the consuming service reads a **specific env var name**. Use `WithReference()` when the service uses Aspire service discovery or standard connection string patterns. You can use both together.

### URL labels and dashboard niceties

Customize how endpoints appear in the Aspire dashboard:

```csharp
// Named endpoints for clarity
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithHttpsEndpoint(name: "public", port: 8443)
    .WithHttpsEndpoint(name: "internal", port: 8444);
```

**Cookie/session isolation with `dev.localhost`**: When multiple services share `localhost`, cookies and session storage can leak between them. Using `*.dev.localhost` subdomains gives each service its own cookie scope. URLs still have ports (e.g., `frontend.dev.localhost:5173`), but the subdomain isolation prevents cross-service collisions.

**The right way**: Update `applicationUrl` in the `profiles` section of `aspire.config.json` — replace `localhost` with `<projectname>.dev.localhost`, and use `otlp.dev.localhost` / `resources.dev.localhost` for infrastructure URLs. **Never** use `withUrlForEndpoint` to set `dev.localhost` URLs — that API is ONLY for dashboard display labels. Example:

```json
{
  "profiles": {
    "https": {
      "applicationUrl": "https://myapp.dev.localhost:17042;http://myapp.dev.localhost:15042",
      "environmentVariables": {
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://otlp.dev.localhost:21042",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://resources.dev.localhost:22042"
      }
    }
  }
}
```

> Note: `*.dev.localhost` resolves to `127.0.0.1` on most systems without any `/etc/hosts` changes.

### Dependency ordering: `WaitFor` and `WaitForCompletion`

**`WaitFor()`** — delay starting a resource until another resource is healthy/ready:

```csharp
var db = builder.AddPostgres("pg").AddDatabase("mydb");
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithReference(db)
    .WaitFor(db);  // Don't start api until db is healthy
```

Always pair `WithReference()` with `WaitFor()` for infrastructure dependencies (databases, caches, queues). Services that depend on other services should generally also wait for them.

**`WaitForCompletion()`** — wait for a resource to run to completion (exit successfully). Use for init containers, database migrations, or seed data scripts:

```csharp
var migration = builder.AddCSharpApp("migration", "../src/MigrationRunner")
    .WithReference(db)
    .WaitFor(db);

var api = builder.AddCSharpApp("api", "../src/Api")
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
var api = builder.AddCSharpApp("api", "../src/Api")
    .WithParentRelationship(backend);
var worker = builder.AddCSharpApp("worker", "../src/Worker")
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
