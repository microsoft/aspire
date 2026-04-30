---
name: aspireify
description: "One-time skill for completing Aspire initialization in an existing app after `aspire init` has dropped the skeleton AppHost. Use this skill when an `aspire.config.json` exists but the AppHost has not yet been wired up."
---

# Aspireify

This is a **one-time setup skill**. It completes the Aspire initialization that `aspire init` started. After this skill finishes successfully, the evergreen `aspire` skill handles ongoing AppHost work. Do not delete this skill unless the user explicitly asks.

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

**Do not assume APIs exist.** Before writing any AppHost code, look up the correct API using `aspire docs search` and `aspire docs get`. Follow the tiered preference: Tier 1 (first-party `Aspire.Hosting.*`) → Tier 2 (community `CommunityToolkit.Aspire.Hosting.*`) → Tier 3 (raw `AddExecutable`/`AddDockerfile`/`AddContainer`). See the "Looking up APIs and integrations" section below for full discovery workflow, tier details, and auto-managed values.

**Don't invent APIs** — if docs search and integration list don't return it, it doesn't exist. Fall back to Tier 3. **API shapes differ between C# and TypeScript** — always check the correct language docs.

### Choosing the right JavaScript resource type

For JavaScript/TypeScript apps, pick the right resource type (`AddViteApp`, `AddNodeApp`, or `AddJavaScriptApp`) and configure dev scripts and port binding. See [references/javascript-apps.md](references/javascript-apps.md) for the selection table, dev script patterns, framework-specific port binding, and browser suppression.

### Never call it ".NET Aspire"

Always refer to the product as just **Aspire**, never ".NET Aspire". This applies to all comments in generated AppHost code, messages to the user, and any documentation you produce.

### Dashboard URL must include auth token

When printing or displaying the Aspire dashboard URL to the user, always include the full login token query parameter. The dashboard requires authentication — a bare URL like `http://localhost:18888` won't work. Use the full URL as printed by `aspire start` (e.g., `http://localhost:18888/login?t=<token>`).

### Redis and auto-TLS

Aspire's infrastructure automatically provisions TLS certificates for container resources that register `WithHttpsCertificateConfiguration` callbacks. `AddRedis()` registers one by default, which means **Redis will get TLS automatically** when the Aspire dev cert infrastructure is active. This is usually fine, but some apps expect plain (non-TLS) Redis.

If Redis health checks fail with `SslStream` / `RedisConnectionException` errors about SSL/TLS handshake failures, the cause is this auto-TLS behavior. **Do not fall back to `AddContainer()`.** Instead, disable the certificate on the Redis resource:

```csharp
var redis = builder.AddRedis("redis")
    .WithoutHttpsCertificate();  // plain Redis, no TLS
```

`WithoutHttpsCertificate()` suppresses the auto-TLS cert injection so Redis stays on plain TCP. Use this when the consuming services don't support TLS Redis connections.

### Prefer HTTPS over HTTP

Always set up HTTPS endpoints by default. Use `WithHttpsEndpoint()` instead of `WithHttpEndpoint()` unless HTTPS doesn't work for a specific integration. For JavaScript and Python apps, call `WithHttpsDeveloperCertificate()` to configure the dev cert. If HTTPS causes issues for a specific resource, fall back to HTTP and leave a comment explaining why. See the "Endpoints and ports" section in the AppHost wiring reference below for detailed patterns and examples.

### Never hardcode URLs — use endpoint references

When a service needs another service's URL as an environment variable, **always** pass an endpoint reference — never a hardcoded string. Hardcoded URLs break whenever Aspire assigns different ports. See the "Cross-service environment variable wiring" section in the AppHost wiring reference below for examples.

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

**Important: Never delete `.env` files automatically.** After migrating all values into the AppHost, explicitly ask the user:
> "I've migrated all the values from your `.env` file into the AppHost. The `.env` file is no longer needed for running via Aspire, but it still works for non-Aspire workflows. Would you like me to remove it, or keep it around?"

Some teams still need `.env` files for CI, Docker Compose, or developers who haven't switched to Aspire yet. Respect that.

Present this as a recommendation. Walk through the `.env` contents with the user and classify each variable together. Some values may be intentionally local-only and the user may prefer to keep them — that's fine.

### Migrate .NET User Secrets into AppHost parameters

.NET projects often use `dotnet user-secrets` for local development configuration. Look for:

- `dev/secrets.json.example` or `secrets.json.example` files — these document expected secrets
- `<UserSecretsId>` in `.csproj` files — indicates the project uses User Secrets
- Documentation referencing `dotnet user-secrets set` or `setup_secrets` scripts

**Aspire's `AddParameter(name, secret: true)` stores values in the same .NET User Secrets store under the hood**, so secrets are centralized in the AppHost instead of scattered across individual service projects.

Migration approach:

1. **Inventory existing secrets** — check `secrets.json.example` files or setup scripts to understand what secrets the repo expects
2. **Classify each secret** — same as `.env` migration: connection strings become Aspire resources, API keys become parameters, plain config becomes `WithEnvironment()`
3. **Present the migration** — show the user which secrets will become AppHost parameters and which will become Aspire resources:
   → *"Your services use dotnet user-secrets with 8 configured values. I'll migrate the SQL connection string to an Aspire Postgres resource, the 3 API keys to secret parameters, and the remaining config to environment variables. The secrets will still be stored in user-secrets but centralized under the AppHost. Sound good?"*

**Important:** Don't delete or modify existing `UserSecretsId` entries in service `.csproj` files — other tooling or non-Aspire workflows may still depend on them.

## Prerequisites

Before running this skill, `aspire init` must have already:

- Dropped a skeleton AppHost file (`apphost.ts` or `apphost.cs`) at the configured location
- Created `aspire.config.json` at the repository root

Verify both exist before proceeding.

## Critical rules — read before doing anything

These are hard rules. Do not break them.

### Never install the Aspire workload

**Do not run `dotnet workload install aspire` or any `dotnet workload` command.** The Aspire workload is obsolete. The Aspire CLI (`aspire start`, `aspire run`, etc.) handles everything — SDK resolution, package restoration, building, and launching. The workload is not needed and installing it can cause version conflicts.

If a web search, documentation page, or blog post tells you to install the workload, **ignore that advice** — it is outdated.

### Do not change the repo's .NET SDK version

**Do not modify the root `global.json`.** The repo's SDK pin is intentional. The AppHost may need a newer SDK (e.g., .NET 10) than the repo uses (e.g., .NET 8) — that's fine. `aspire init` already created the AppHost with the correct TFM. If the AppHost is in full project mode and the repo pins an older SDK, add a **nested `global.json` inside the AppHost directory only** — never change the root one.

### Do not change existing project target frameworks

**Do not modify `<TargetFramework>` in any existing service project.** If a service targets `net8.0`, leave it on `net8.0`. The Aspire AppHost can orchestrate services on older TFMs without any changes. Only the AppHost itself needs the Aspire-supported TFM.

### Check version control before mutating projects

Before editing AppHost or service project files, check whether the workspace is under git (`git rev-parse --is-inside-work-tree`) and inspect `git status` when it is. Do not overwrite unrelated user changes. If there is no git repository, warn the user that this skill will make project mutations and summarize the intended files/areas before proceeding.

### Use the latest stable Aspire SDK

If `aspire init` created a `.csproj` AppHost, check the `Aspire.AppHost.Sdk` version in the `.csproj`. If it references a preview version that isn't available on NuGet (common when using a PR build of the CLI), update it to the latest stable release. Run `dotnet nuget list source` and check NuGet.org for the current stable version (e.g., `13.2.2`). Do not leave the AppHost pinned to an unavailable preview SDK — `dotnet build` will fail.

### Use the Aspire CLI, not raw dotnet commands, for Aspire operations

- Use `aspire start` to launch the AppHost (not `dotnet run`)
- Use `aspire add <integration>` to add hosting integrations (not `dotnet add package`)
- Use `aspire restore` to restore TypeScript AppHost dependencies
- Use `aspire docs search` to look up APIs

Standard `dotnet` commands (`dotnet build`, `dotnet add reference`, `dotnet sln add`) are fine for normal .NET operations like building projects, adding project references, and managing solution membership.

## Determine your context

Read `aspire.config.json` at the repository root **if it exists**. Key fields:

- **`appHost.language`**: `"typescript/nodejs"` or `"csharp"` — determines which syntax and tooling to use
- **`appHost.path`**: path to the AppHost file or project directory — this is where you'll edit code

For C# AppHosts, there are two sub-modes:

- **Single-file mode**: `appHost.path` points directly to an `apphost.cs` file using the `#:sdk` directive. No `.csproj` needed. Configuration lives in `aspire.config.json`.
- **Full project mode**: A directory containing a `.csproj`, `Program.cs`, and `Properties/launchSettings.json`. This was created by `aspire init` using the `aspire-apphost` template because a `.sln`/`.slnx` was found. **The template-generated AppHost is correct and complete** — it has proper SDK references, launch profiles with randomized ports, and a skeleton `Program.cs`. Do not recreate or hand-edit the `.csproj` or `launchSettings.json`. Configuration lives in `Properties/launchSettings.json` inside the AppHost project directory (standard .NET launch settings), **not** in `aspire.config.json`.

### Configuration files — which is which

**Do not confuse these files. Do not create or modify config files for the user's service projects — only for the AppHost.**

| File | Where it lives | What it's for | Who uses it |
|------|---------------|---------------|-------------|
| `aspire.config.json` | Repo root | AppHost path, language, profiles (ports, env vars) | Single-file and polyglot AppHosts only |
| `Properties/launchSettings.json` | Inside the AppHost project dir | Launch profiles (ports, env vars) | Full project mode `.csproj` AppHosts (standard .NET) |
| `appsettings.json` | Inside service projects | Service-specific configuration | The services themselves — **do not create or modify these** |
| `launchSettings.json` in service projects | Inside service project dirs | Service launch config | The services themselves — **do not create or modify these** |

**Key rule:** When the AppHost is a `.csproj` project, it's a standard .NET project. It uses `Properties/launchSettings.json` for launch profiles, just like any other .NET project. `aspire init` creates this file with the correct ports. Do not create a duplicate `aspire.config.json` for project-mode AppHosts.

Check which mode you're in by looking at what exists at the `appHost.path` location. If there's no `aspire.config.json`, look for a `.csproj` AppHost directory with `Properties/launchSettings.json` instead.

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
  - **Profiles**: if any service has a `profiles:` key, the compose file uses profiles to organize services into groups (e.g., `cloud`, `storage`, `mssql`, `postgres`). When profiles exist:
    1. List the available profiles and what services each includes
    2. Ask the user which profile(s) to target for the AppHost (e.g., *"Your docker-compose uses profiles: cloud, mssql, postgres, storage, redis. Which represent your local dev stack?"*)
    3. Only model services that belong to the selected profile(s) — skip the rest
    4. If a service has no `profiles:` key, it runs in all profiles — always include it
  - **Services**: each named service (in the selected profiles) maps to a potential AppHost resource
  - **Images**: container images used (e.g., `postgres:16`, `redis:7`) → these become `AddContainer()` or typed Aspire integrations (e.g., `AddPostgres()`, `AddRedis()`)
  - **Ports**: published port mappings → `WithHttpsEndpoint()` or `WithEndpoint()`
  - **Environment variables**: env vars and `.env` file references → `WithEnvironment()`. Watch for `${VAR}` interpolation syntax — trace these back to `.env` files and migrate them to AppHost parameters
  - **Volumes**: named/bind volumes → `WithVolume()` or `WithBindMount()`
  - **Dependencies**: `depends_on` → `WithReference()` and `WaitFor()`
  - **Build contexts**: `build:` entries → `AddDockerfile()` pointing to the build context directory
  - Prefer typed Aspire integrations over raw `AddContainer()` when the image matches a known integration (use `aspire docs search` to check). For example, `postgres:16` → `AddPostgres()`, `redis:7` → `AddRedis()`, `rabbitmq:3` → `AddRabbitMQ()`.
  - For complex compose files, also load [references/docker-compose.md](references/docker-compose.md) for detailed migration patterns.
- **Static frontends**: Vite, Next.js, Create React App, or other frontend framework configs
- **`.env` files**: Scan for `.env`, `.env.local`, `.env.development`, `.env.example`, etc. These contain configuration that should be migrated into AppHost parameters (see Guiding Principles above)
- **User Secrets**: Scan for `secrets.json.example` files and `<UserSecretsId>` in `.csproj` files. These indicate .NET User Secrets are in use — migrate them into AppHost parameters (see Guiding Principles above)
- **Package manager**: Detect which Node.js package manager the repo uses by looking for lock files: `pnpm-lock.yaml` → pnpm, `yarn.lock` → yarn, `package-lock.json` or none → npm. Use the detected package manager for all install/run commands throughout this skill.

**Ignore:**

- The AppHost directory/file itself
- `node_modules/`, `.modules/`, `dist/`, `build/`, `bin/`, `obj/`, `.git/`
- Test projects (directories named `test`/`tests`/`__tests__`, projects referencing xUnit/NUnit/MSTest, or test-only package.json scripts)

### Step 2: Check prerequisites and smoke-test the skeleton

Before investing time in wiring, run `aspire doctor` to verify the environment is ready:

```bash
aspire doctor
```

This checks for a working .NET SDK, container runtime (Docker/Podman), trusted dev certificates, and deprecated workloads. **Fix any failures before proceeding** — discovering that Docker isn't running *after* you've wired 10 services wastes significant time.

Common issues caught by `aspire doctor`:
- **Container runtime not running**: Start Docker Desktop or Podman before proceeding — container resources will fail to start without it.
- **Deprecated aspire workload installed**: If installed by Visual Studio, it can't be removed via CLI — this is a warning, not a blocker.
- **Untrusted dev certificate**: Run `aspire certs trust` to fix HTTPS endpoint failures.

Once the environment is clean, verify the Aspire skeleton boots:

```bash
aspire start
```

The empty AppHost should start successfully — the dashboard should come up and the process should run without errors. You won't see any resources yet (that's expected), but if `aspire start` fails here, fix the issue before proceeding.

For full project mode, the AppHost was created from the `aspire-apphost` template and should work out of the box. If it fails, common causes are:

- **SDK version mismatch**: The repo's root `global.json` may pin an older SDK (e.g., 8.0). The AppHost directory needs its own nested `global.json` pinning the Aspire-supported SDK. Create one if missing (see Critical Rules above).
- **Port conflicts**: If another Aspire app is running, the randomly assigned ports may conflict. Stop other instances first.

For single-file mode:

- **Missing profiles in `aspire.config.json`**: The file must have a `profiles` section with `applicationUrl`. Re-run `aspire init` to regenerate.
- **Missing dependencies**: For TypeScript, ensure the `.modules/aspire.js` SDK is available. Run `aspire restore` if needed.

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

**Before creating ServiceDefaults, check for existing observability setup.** Many repos already have their own OpenTelemetry, Polly (HTTP resilience), or health check wiring — often in a shared extension method or SDK package. Search for:

- `AddOpenTelemetry`, `UseOpenTelemetry`, `AddTracing`, `WithTracing`, `WithMetrics` — existing OTel setup
- `AddStandardHttp`, `AddPolicyHandler`, `AddHttpStandardResilienceHandler` — existing Polly/resilience config
- `AddHealthChecks`, `MapHealthChecks` — existing health check registration
- Custom SDK extensions (e.g., `UseBitwardenSdk()`, `UseCompanySdk()`) — these often bundle OTel, health checks, and auth in one call

If the repo already has OTel/health checks/resilience in a shared extension, **strip those from the generated ServiceDefaults** to avoid duplication. Only keep the parts that don't overlap. For example, if `UseBitwardenSdk()` already sets up OTel tracing and metrics, the ServiceDefaults should skip the OTel builder calls and only add service discovery and health endpoint mapping.

Present the overlap to the user: *"Your services already set up OpenTelemetry via `UseBitwardenSdk()`. I'll create ServiceDefaults without the OTel setup to avoid duplication."*

**Placement is your decision.** Where to put ServiceDefaults depends on the repo's structure:

- If the AppHost has its own nested SDK boundary (nested `global.json`), ServiceDefaults should live next to the AppHost in that same boundary so it can target the same TFM.
- If the repo's root SDK is compatible with the AppHost's TFM, ServiceDefaults can live alongside existing source (e.g., in `src/`).
- If a ServiceDefaults project already exists (look for references to `Microsoft.Extensions.ServiceDiscovery` or `Aspire.ServiceDefaults`), skip creation and use the existing one.

To create one:

```bash
dotnet new aspire-servicedefaults -n <SolutionName>.ServiceDefaults -o <path>
```

If a `.sln` exists and the ServiceDefaults project is compatible with the solution's SDK, add it:

```bash
dotnet sln <solution> add <ServiceDefaults.csproj>
```

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

#### C# AppHost — full project mode (`Program.cs` + `.csproj`)

In full project mode the AppHost has a `.csproj`, so prefer typed project references via `AddProject<T>()` — they give compile-time safety, auto-restart on rebuild, and the typed `Projects.*` namespace. Reserve `AddCSharpApp("name", "../path")` for cases where a project reference isn't possible (e.g., the service uses an SDK that can't be referenced from `Aspire.AppHost.Sdk`, or the AppHost is single-file mode).

Edit the AppHost's `Program.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api");

var web = builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
```

And add project references so the `Projects.*` namespace is generated:

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

- **Look up APIs before writing code** — see "Looking up APIs and integrations" section below. Do not guess API shapes.
- Use meaningful resource names derived from the project/directory name.
- Wire up `WithReference()`/`withReference()` and `WaitFor()`/`waitFor()` for services that depend on each other (ask the user if relationships are unclear).
- Use `WithExternalHttpEndpoints()`/`withExternalHttpEndpoints()` for user-facing frontends.

### Step 6: Configure dependencies

#### TypeScript AppHost

See [references/javascript-apps.md](references/javascript-apps.md) for `package.json`, `tsconfig.json`, and ESLint configuration patterns. Key points:

- Augment existing files, never overwrite
- Run `aspire restore` to generate `.modules/`, then install deps with the repo's package manager
- Do not manually add Aspire SDK packages — `aspire restore` handles those

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

For .NET services, ServiceDefaults handles OTel automatically. For everything else, the services need a small setup to export telemetry. Aspire automatically injects `OTEL_EXPORTER_OTLP_ENDPOINT` into all managed resources — the services just need to read it.

**Present this to the user as an option, not a mandatory step.** Some users may want to add OTel later, and that's fine — their services will still run, they just won't appear in the dashboard's trace/metrics views.

**For each service that doesn't already have OTel, ask:**
> "Would you like me to add OpenTelemetry instrumentation to `<service>`? This lets the Aspire dashboard show its traces, metrics, and logs. I'll need to add a few packages and an instrumentation setup file."

If they say yes, follow the per-language setup guides in [references/opentelemetry.md](references/opentelemetry.md).

### Step 9: Offer dev experience enhancements

Before validating, present the user with optional quality-of-life improvements. These aren't required for `aspire start` to work, but they make the local dev experience significantly nicer.

**Suggest each of these individually — don't apply without asking:**

1. **Cookie and session isolation with `dev.localhost`**: When multiple services run on `localhost`, they share cookies and session storage — which can cause hard-to-debug auth problems. Using `*.dev.localhost` subdomains isolates each service's cookies and storage. Note: URLs still include ports (e.g., `frontend.dev.localhost:5173`), but the subdomain isolation prevents cross-service cookie collisions.
   > "Would you like me to set up `dev.localhost` subdomains for your services? This gives each service its own cookie/session scope so they don't interfere with each other. URLs will look like `frontend.dev.localhost:5173` — the `*.dev.localhost` domain resolves to 127.0.0.1 automatically on most systems, no `/etc/hosts` changes needed."

   **How to do it — pick the right config file based on AppHost mode** (see "Configuration files — which is which" earlier in this doc):

   - **Single-file mode** (`apphost.cs` with `#:sdk` directive) and **polyglot AppHosts** (TypeScript, Python, Go, …): edit the `profiles` section in `aspire.config.json` at the repo root.
   - **Full project mode** (`.csproj` AppHost): edit `Properties/launchSettings.json` inside the AppHost project directory. **Do not edit `aspire.config.json` for project-mode AppHosts** — they read launch profiles from `launchSettings.json`, so changes to `aspire.config.json` will be ignored.

   In both cases, replace `localhost` with `<projectname>.dev.localhost` in `applicationUrl`, and use descriptive subdomains like `otlp.dev.localhost` and `resources.dev.localhost` for the infrastructure URLs. This is the same mechanism `aspire new` uses.

   Example — `aspire.config.json` (single-file / polyglot):

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

   Equivalent — `Properties/launchSettings.json` (full project mode):

   ```json
   {
     "profiles": {
       "https": {
         "commandName": "Project",
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

2. **Custom URL labels in the dashboard** (display text only): Rename endpoint URLs in the Aspire dashboard for clarity:
   ```csharp
   .WithUrlForEndpoint("https", url => url.DisplayText = "Web UI")
   ```

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
5. **Dashboard is accessible**: Confirm the dashboard URL is printed and can be opened (remember: include the login token — see "Dashboard URL must include auth token" above).
6. **Telemetry reaches the dashboard**: After resources are running, verify that traces and structured logs appear in the Aspire dashboard. Open the dashboard, navigate to the Traces view, and confirm at least one trace is visible from a service. If no telemetry flows:
   - Check whether the service's OTel setup conditionally disables export (e.g., based on a `selfHosted` flag, a config key like `OpenTelemetry:Enabled`, or an environment check). Many SDKs default OTel OFF for self-hosted/local modes — set the enabling flag explicitly via `WithEnvironment()`.
   - Check that `OTEL_EXPORTER_OTLP_ENDPOINT` is being injected by Aspire (it should be automatic for services modeled with `AddCSharpApp`/`AddProject`).
   - Generate a trace by making an HTTP request to one of the services (e.g., `curl https://localhost:<port>/healthz` or any known endpoint). Some services don't emit traces until they receive traffic.
   - Check service logs for OTLP exporter errors (connection refused, TLS handshake failures).

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

Get the dashboard URL (with login token) from `aspire start` output. Get resource status from `aspire describe`. If any resource shows `Finished`, confirm from logs that it was an intentional one-shot resource that exited successfully before including it as success. This summary is the user's confirmation that init worked — make it complete and accurate.

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
2. Confirm the evergreen `aspire` skill is present for ongoing AppHost work.
3. Do not delete the `aspireify/` skill directory unless the user explicitly asks.

## Key rules

- **Never overwrite existing files** — always augment/merge
- **Ask the user before modifying service code** (especially OTel and ServiceDefaults injection)
- **Respect existing project structure** — don't reorganize the repo
- **If stuck, use `aspire doctor`** to diagnose environment issues
- **Never hardcode URLs in `withEnvironment`** — when a service needs another service's URL (e.g., `VITE_APP_WS_SERVER_URL`), pass an endpoint reference, NOT a string literal. Use `room.getEndpoint("http")` (TS) or `room.GetEndpoint("http")` (C#) and pass that to `withEnvironment`. Hardcoded URLs break when ports change.
- **Never use `withUrlForEndpoint` to set `dev.localhost` URLs** — `dev.localhost` configuration belongs in `aspire.config.json` profiles, not in AppHost code. `withUrlForEndpoint` is ONLY for setting display labels (e.g., `url.DisplayText = "Web UI"`).

## References

- For AppHost wiring patterns, API lookup, endpoint configuration, and resource wiring, see [references/apphost-wiring.md](references/apphost-wiring.md).
- For solution-backed C# AppHosts (`.sln`/`.slnx` + `.csproj` AppHost), see [references/full-solution-apphosts.md](references/full-solution-apphosts.md).
- For repos with `docker-compose.yml` or `compose.yml`, see [references/docker-compose.md](references/docker-compose.md).
- For per-language OpenTelemetry setup (Node, Python, Go, Java), see [references/opentelemetry.md](references/opentelemetry.md).
- For JavaScript/TypeScript resource types, dev scripts, and TypeScript AppHost config, see [references/javascript-apps.md](references/javascript-apps.md).
