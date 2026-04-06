---
name: aspire-init
description: "One-time skill for completing Aspire initialization after `aspire init` has dropped the skeleton AppHost and aspire.config.json. This skill scans the repository, discovers projects, wires up the AppHost (TypeScript or C#), configures dependencies and OpenTelemetry, validates that `aspire start` works, and self-removes on success."
---

# Aspire Init

This is a **one-time setup skill**. It completes the Aspire initialization that `aspire init` started. After this skill finishes successfully, it should be deleted — the evergreen `aspire` skill handles ongoing AppHost work.

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
- **Dockerfiles**: standalone `Dockerfile` or `docker-compose.yml` entries representing services
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

### Step 8: Trust development certificates

```bash
aspire certs trust
```

### Step 9: Validate

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

### Step 10: Update solution file (C# full project mode only)

If a `.sln`/`.slnx` exists, verify all new projects are included:

```bash
dotnet sln <solution> list
```

Ensure both the AppHost and ServiceDefaults projects appear.

### Step 11: Clean up

After successful validation:

1. Stop the running AppHost: `aspire stop`
2. **Delete this skill** — remove the `aspire-init/` skill directory from all locations where it was installed (check `.agents/skills/`, `.github/skills/`, `.claude/skills/`)
3. Confirm the evergreen `aspire` skill is present for ongoing AppHost work

## Key rules

- **Never overwrite existing files** — always augment/merge
- **Use `aspire docs search` before guessing APIs** — look up the correct builder methods
- **Ask the user before modifying service code** (especially OTel and ServiceDefaults injection)
- **Respect existing project structure** — don't reorganize the repo
- **This is a one-time skill** — delete it after successful init
- **If stuck, use `aspire doctor`** to diagnose environment issues
