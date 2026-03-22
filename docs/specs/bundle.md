# Aspire Bundle - Self-Contained Distribution

> **Status:** Draft Specification  
> **Last Updated:** February 2026

This document specifies the **Aspire Bundle**, a self-contained distribution package that provides the Aspire CLI along with all runtime components (Dashboard, DCP) needed to run any Aspire application.

## Table of Contents

1. [Overview](#overview)
2. [Problem Statement](#problem-statement)
3. [Goals and Non-Goals](#goals-and-non-goals)
4. [Architecture](#architecture)
5. [Bundle Layout](#bundle-layout)
6. [Self-Extracting Binary](#self-extracting-binary)
7. [Component Discovery](#component-discovery)
8. [NuGet Operations](#nuget-operations)
9. [Certificate Management](#certificate-management)
10. [AppHost Server](#apphost-server)
11. [CLI Integration](#cli-integration)
12. [Installation](#installation)
13. [Configuration](#configuration)
14. [Size and Distribution](#size-and-distribution)
15. [Security Considerations](#security-considerations)
16. [Build Process](#build-process)

---

## Overview

The Aspire Bundle is a platform-specific archive containing the Aspire CLI and all runtime components:

- **Aspire CLI** (native AOT executable, includes native certificate management)
- **Aspire Managed** (unified self-contained binary: Dashboard + AppHost Server + NuGet Helper)
- **Developer Control Plane (DCP)** (no longer distributed via NuGet)

**Key change**: DCP and Dashboard are now bundled with the CLI installation, not downloaded as NuGet packages. Dashboard, AppHost Server, and NuGet Helper are consolidated into a single `aspire-managed` binary that dispatches via subcommands. Certificate management is handled natively in the CLI (no subprocess needed). This:

- Eliminates large NuGet package downloads on first run
- Ensures version consistency between CLI and runtime components
- Simplifies the Aspire.Hosting SDK (no more MSBuild magic for DCP/Dashboard)
- Makes offline scenarios work reliably

Users download a single archive (~200 MB compressed, ~577 MB on disk), extract it, and have everything needed to run any Aspire application.

---

## Problem Statement

Currently, Aspire has two distribution challenges:

### For Polyglot App Hosts
The polyglot app host requires a globally-installed .NET SDK for:
1. **Dynamic Project Build**: The AppHost Server project is generated and built at runtime
2. **Package Operations**: `aspire add` uses `dotnet package search`
3. **Component Resolution**: DCP and Dashboard come from NuGet

### For All Applications
DCP and Dashboard distribution via NuGet packages causes:
1. **Large first-run downloads**: ~100+ MB of NuGet packages
2. **Version skew**: Dashboard/DCP version can mismatch CLI version
3. **Complex MSBuild targets**: Magic in Aspire.Hosting.AppHost SDK
4. **Offline difficulties**: Needs NuGet cache or internet access

---

## Goals and Non-Goals

### Goals

- **Zero .NET SDK dependency** for polyglot app host scenarios
- **Single download** containing all required runtime components
- **Unified DCP/Dashboard distribution** - bundled with CLI, not via NuGet
- **Offline capable** once the bundle is installed
- **Same functionality** as current approach, simpler distribution
- **Backward compatible** with existing SDK-based workflows

### Non-Goals

- Replacing the .NET SDK for .NET app host development
- Supporting `aspire new` for .NET project templates (requires SDK)
- Auto-updating the bundle (manual download for now)

---

## Architecture

### Component Interaction

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                              ASPIRE BUNDLE                                   │
│                          aspire-{version}-{platform}                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────┐     spawns      ┌───────────────────────────────────┐    │
│  │   aspire     │ ───────────────▶│       ASPIRE-MANAGED              │    │
│  │ (Native AOT) │                 │  (self-contained single binary)   │    │
│  │              │                 │                                   │    │
│  │  Commands:   │                 │  Subcommands:                     │    │
│  │  • run       │                 │  • dashboard  (Aspire Dashboard)  │    │
│  │  • add       │                 │  • server     (AppHost Server)    │    │
│  │  • new       │                 │  • nuget      (NuGet operations)  │    │
│  │  • publish   │                 └───────────────────────────────────┘    │
│  │              │                              │                           │
│  │  Native:     │                              ▼                           │
│  │  • cert mgmt │              ┌───────────────────────────────────┐       │
│  └──────┬───────┘              │        APPHOST SERVER             │       │
│         │                      │  (aspire-managed server)          │       │
│         │  JSON-RPC            │                                   │       │
│         │◀────────────────────▶│  • Aspire.Hosting.* assemblies    │       │
│         │   (socket)           │  • RemoteHostServer endpoint      │       │
│         │                      │  • Dynamic integration loading    │       │
│         │                      └───────────────┬───────────────────┘       │
│         │                                      │                           │
│         │              ┌───────────────────────┼───────────────────┐       │
│         │              ▼                       ▼                   ▼       │
│         │     ┌─────────────┐         ┌─────────────┐     ┌────────────┐  │
│         │     │  DASHBOARD  │         │     DCP     │     │INTEGRATIONS│  │
│         │     │ (aspire-    │         │             │     │            │  │
│         │     │  managed    │         │ dcp/        │     │~/.aspire/  │  │
│         │     │  dashboard) │         └─────────────┘     │ packages/  │  │
│         │     └─────────────┘                              └────────────┘  │
│         │                                                        ▲         │
│         │     ┌─────────────────────────────────────────┐        │         │
│         │     │         USER'S APPHOST                  │────────┘         │
│         │     │    (TypeScript / Python / etc.)         │                  │
│         │     │                                         │                  │
│         │     │    apphost.ts / app.py                  │                  │
│         │     └─────────────────────────────────────────┘                  │
│         │                                                                  │
└─────────┴──────────────────────────────────────────────────────────────────┘
```

### Execution Flow

When a user runs `aspire run` with a TypeScript app host:

1. **CLI reads project configuration** from `.aspire/settings.json`
2. **CLI discovers bundle layout** using priority-based resolution
3. **CLI downloads missing integrations** using aspire-managed's NuGet subcommand
4. **CLI generates `appsettings.json`** for the AppHost Server with integration list
5. **CLI starts AppHost Server** using aspire-managed's server subcommand
6. **CLI starts guest app host** (TypeScript) which connects via JSON-RPC
7. **AppHost Server orchestrates** containers, Dashboard (via aspire-managed dashboard), and DCP

---

## Bundle Layout

### Directory Structure

```text
aspire-{version}-{platform}/
│
├── aspire[.exe]                        # Native AOT CLI (~25 MB)
│                                       # (includes native certificate management)
│
├── managed/                            # Unified managed binary (~65 MB)
│   └── aspire-managed[.exe]            # Self-contained single-file executable
│                                       # Subcommands: dashboard | server | nuget
│
├── dcp/                                # Developer Control Plane (~127 MB)
│   ├── dcp[.exe]                       # Native executable
│   └── ...
│
└── (no more runtime/, dashboard/, aspire-server/, tools/ directories)
```

**Key change from previous layout**: The separate `.NET Runtime` (~106 MB), `dashboard/` (~42 MB), `aspire-server/` (~19 MB), `tools/aspire-nuget/` (~5 MB), and `tools/dev-certs/` directories have been consolidated into a single `managed/aspire-managed` self-contained binary. Certificate management has been moved natively into the CLI itself, eliminating the need for a separate dev-certs tool.

**Total Bundle Size:**
- **Unzipped:** ~220 MB (down from ~323 MB — eliminated separate runtime)
- **Zipped:** ~80 MB

## Self-Extracting Binary

The Aspire CLI can be distributed as a **self-extracting binary** — a single native AOT executable with the full bundle tarball embedded inside. This is the simplest installation method: download one file, run `aspire setup`, done.

### Binary Format

The payload is embedded as a .NET assembly resource (`bundle.tar.gz`) in the native AOT CLI binary. This means the payload lives inside the PE/ELF binary's resource section, which is covered by code signing.

```text
┌─────────────────────────────────────────────────┐
│        Native AOT CLI (~29 MB)                  │
│        (fully functional without payload)       │
│                                                 │
│        Embedded resource: bundle.tar.gz         │
│        (~100 MB compressed payload:             │
│         runtime, dashboard, dcp, etc.)          │
└─────────────────────────────────────────────────┘
```

**Detection**: `Assembly.GetManifestResourceInfo("bundle.tar.gz")` — metadata-only check, no I/O.

**Payload access**: `Assembly.GetManifestResourceStream("bundle.tar.gz")` — demand-paged by the OS, zero memory overhead at startup until extraction is needed.

A CLI binary without an embedded resource (dev build, dotnet tool install, or previously-extracted copy) has no payload. All extraction commands gracefully no-op.

### BundleService

All extraction logic is centralized in `IBundleService` / `BundleService` (`src/Aspire.Cli/Bundles/`):

| Method | Purpose | Used by |
|--------|---------|---------|
| `EnsureExtractedAsync()` | Lazy extraction from `Environment.ProcessPath` | `AppHostServerProjectFactory` |
| `ExtractAsync(dest, force)` | Explicit extraction, returns `BundleExtractResult` | `SetupCommand` |
| `IsBundle` | Whether the CLI binary contains an embedded bundle | DI factory methods |
| `EnsureExtractedAndGetLayoutAsync()` | Ensures extraction + returns discovered layout | `BundleNuGetPackageCache`, etc. |

The service uses a file lock (`.aspire-bundle-lock`) in the extraction directory for cross-process synchronization and is registered as a singleton.

**Extraction flow:**
1. Check for embedded `bundle.tar.gz` resource — if absent, return `NoPayload`
2. Check version marker (`.aspire-bundle-version`) — if version matches, return `AlreadyUpToDate`
3. Clean well-known layout directories (managed, dcp) — preserves `bin/`
4. Extract payload using .NET `TarReader` with path-traversal and symlink validation
5. Set Unix file permissions from tar entry metadata (execute bit, etc.)
6. Write version marker with assembly informational version
7. Validate layout via `LayoutDiscovery`

### Extraction Modes

#### Explicit: `aspire setup`

```bash
aspire setup [--install-path <path>]
```

Best for install scripts — reduces to:

```bash
mkdir -p ~/.aspire/bin
curl -fsSL .../aspire -o ~/.aspire/bin/aspire && chmod +x ~/.aspire/bin/aspire
~/.aspire/bin/aspire setup
export PATH="$HOME/.aspire/bin:$PATH"
```

#### Lazy: first polyglot command

When a polyglot project runs `aspire run`, `AppHostServerProjectFactory.CreateAsync()` calls `BundleService.EnsureExtractedAsync()`. This transparently extracts on first use, before the normal command flow. C# projects never trigger extraction — they use `dotnet` directly.

#### After self-update

`aspire update --self` downloads the new self-extracting binary, swaps it, then calls `BundleService.ExtractAsync(force: true)` to proactively extract the updated payload.

### Version Tracking

The file `.aspire-bundle-version` in the layout root contains the assembly informational version string (e.g., `13.2.0-pr.14398.gabc1234`). This enables:

- **Skip extraction** when version matches (normal startup is free)
- **Re-extract** when CLI binary is updated (version changes)
- **Force re-extract** with `aspire setup --force`

### Platform Notes

- **macOS**: Archives are created with `COPYFILE_DISABLE=1` to suppress `SCHILY.xattr` PAX headers that break .NET's `TarReader`.
- **Unix**: `TarReader` extraction preserves file permissions from tar entry metadata (execute bit, etc.).
- **Windows**: Uses .NET `TarReader` for extraction (no system `tar` dependency). Unix file permissions are not applicable.
- **Layout cleanup**: Before re-extraction, well-known directories are removed to avoid file conflicts.

### Shared Code

| File | Purpose |
|------|---------|
| `src/Aspire.Cli/Bundles/IBundleService.cs` | Interface + `BundleExtractResult` enum |
| `src/Aspire.Cli/Bundles/BundleService.cs` | Implementation with .NET TarReader extraction |

---

## Component Discovery

The CLI and `Aspire.Hosting` both need to discover DCP, Dashboard, and .NET runtime locations. During the transition period, different versions of CLI and Aspire.Hosting may be used together, so both components implement discovery with graceful fallback.

### Discovery Priority

Both CLI and Aspire.Hosting follow this priority order for DCP and Dashboard:

1. **Environment variables** (`ASPIRE_DCP_PATH`, `ASPIRE_DASHBOARD_PATH`, `ASPIRE_RUNTIME_PATH`) - highest priority
2. **Disk discovery** - check for bundle layout next to the executable, then in the parent directory
3. **Assembly metadata** - NuGet package paths embedded at build time (Aspire.Hosting only)

For .NET runtime resolution (used when launching Dashboard):

1. **Environment variable** (`ASPIRE_RUNTIME_PATH`) - set by CLI for guest apphosts
2. **Disk discovery** - check for `runtime/` directory next to the app, then in the parent directory
3. **PATH fallback** - use `dotnet` from system PATH

The parent directory check supports the installed layout where the CLI binary lives in `bin/` (`~/.aspire/bin/aspire`) while bundle components are siblings at the root (`~/.aspire/runtime/`, `~/.aspire/dashboard/`, etc.).

### Environment Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `ASPIRE_LAYOUT_PATH` | Root of the bundle | `/opt/aspire` |
| `ASPIRE_DCP_PATH` | DCP binaries location | `/opt/aspire/dcp` |
| `ASPIRE_DASHBOARD_PATH` | Path used by Aspire.Hosting to locate the dashboard binary (now points to `aspire-managed`) | `/opt/aspire/managed/aspire-managed` |
| `ASPIRE_MANAGED_PATH` | CLI-only path for the `aspire-managed` binary | `/opt/aspire/managed/aspire-managed` |
| `ASPIRE_INTEGRATION_LIBS_PATH` | Path to integration DLLs for aspire-server assembly resolution | `/home/user/.aspire/libs` |
| `ASPIRE_USE_GLOBAL_DOTNET` | Force SDK mode | `true` |
| `ASPIRE_REPO_ROOT` | Dev mode (Aspire repo path, DEBUG builds only) | `/home/user/aspire` |

**Note:** `ASPIRE_INTEGRATION_LIBS_PATH` is set by the CLI when running guest apphosts that require additional hosting integration packages (e.g., `Aspire.Hosting.Redis`). The aspire-server uses this path to resolve integration assemblies at runtime.

### Transition Compatibility

During the transition from NuGet-based to bundle-based distribution, these version combinations must work:

#### Scenario 1: New CLI + New Aspire.Hosting

```text
Bundle CLI ────► runs ────► .NET AppHost (new Aspire.Hosting)
     │                           │
     │ sets ASPIRE_DCP_PATH      │ reads ASPIRE_DCP_PATH
     │ sets ASPIRE_DASHBOARD_PATH│ reads ASPIRE_DASHBOARD_PATH
     │                           │
     └──────────────────────────►│ Uses bundled DCP/Dashboard ✓
```

**Behavior**: CLI detects bundle, sets environment variables. Aspire.Hosting reads them first.

#### Scenario 2: New CLI + Old Aspire.Hosting

```text
Bundle CLI ────► runs ────► .NET AppHost (old Aspire.Hosting)
     │                           │
     │ sets ASPIRE_DCP_PATH      │ ignores (doesn't check env vars)
     │ sets ASPIRE_DASHBOARD_PATH│
     │                           │
     │                           │ Uses NuGet package paths ✓
```

**Behavior**: CLI sets env vars, but old Aspire.Hosting doesn't read them. Falls back to assembly metadata (NuGet packages). Works correctly.

#### Scenario 3: Old CLI + New Aspire.Hosting

```text
Old CLI ────► runs ────► .NET AppHost (new Aspire.Hosting)
     │                        │
     │ (no env vars set)      │ checks env vars → empty
     │                        │ does disk discovery → not found
     │                        │ uses assembly metadata (NuGet) ✓
```

**Behavior**: No env vars set. New Aspire.Hosting tries disk discovery, doesn't find bundle, falls back to NuGet packages.

#### Scenario 4: No CLI (direct `dotnet run`)

```text
dotnet run ────► .NET AppHost (any Aspire.Hosting)
                      │
                      │ checks env vars → empty
                      │ does disk discovery → not found (unless bundle installed)
                      │ uses assembly metadata (NuGet) ✓
```

**Behavior**: Standard SDK workflow. Uses NuGet packages as always.

#### Scenario 5: Bundle installed system-wide

```text
dotnet run ────► .NET AppHost (new Aspire.Hosting)
                      │
                      │ checks env vars → empty
                      │ does disk discovery → finds /opt/aspire/dcp ✓
                      │ uses bundled DCP/Dashboard ✓
```

**Behavior**: Even without CLI, if bundle is installed to a well-known location and AppHost is run from there, disk discovery finds it.

### Why Both Need Discovery

| Component | When it discovers | What it does |
|-----------|------------------|--------------|
| **CLI** | Before launching AppHost | Sets `ASPIRE_DCP_PATH`, `ASPIRE_DASHBOARD_PATH` env vars |
| **Aspire.Hosting** | At AppHost startup | Reads env vars OR does its own disk discovery OR uses NuGet |

This dual-discovery approach ensures:
- **Forward compatibility**: New CLI works with old Aspire.Hosting
- **Backward compatibility**: Old CLI works with new Aspire.Hosting
- **Standalone operation**: Aspire.Hosting works even without CLI

---

## NuGet Operations

The bundle includes NuGet operations via the `aspire-managed nuget` subcommand, which provides package search and restore functionality without requiring the .NET SDK.

### NuGet Helper Commands

```bash
# Search for packages
{managed}/aspire-managed nuget search \
  --query "Aspire.Hosting" \
  --prerelease \
  --take 50 \
  --source https://api.nuget.org/v3/index.json \
  --format json

# Restore packages
{managed}/aspire-managed nuget restore \
  --package "Aspire.Hosting.Redis" \
  --version "13.2.0" \
  --framework net10.0 \
  --output ~/.aspire/packages

# Create flat layout from restored packages (DLLs + XML doc files)
{managed}/aspire-managed nuget layout \
  --assets ~/.aspire/packages/obj/project.assets.json \
  --output ~/.aspire/packages/libs \
  --framework net10.0
```

### Search Output Format

```json
{
  "packages": [
    {
      "id": "Aspire.Hosting.Redis",
      "version": "13.2.0",
      "allVersions": ["13.1.0", "13.2.0"],
      "description": "Redis hosting integration for .NET Aspire",
      "authors": ["Microsoft"],
      "source": "nuget.org",
      "deprecated": false
    }
  ],
  "totalHits": 42
}
```

### Package Cache Structure

```text
~/.aspire/packages/
├── aspire.hosting.redis/
│   └── 13.2.0/
│       ├── aspire.hosting.redis.13.2.0.nupkg
│       └── lib/
│           └── net10.0/
│               └── Aspire.Hosting.Redis.dll
├── aspire.hosting.valkey/
│   └── 13.2.0/
│       └── ...
└── libs/                               # Flat layout for probing
    ├── Aspire.Hosting.Redis.dll
    ├── Aspire.Hosting.Redis.xml         # XML doc file (for IntelliSense/MCP)
    ├── Aspire.Hosting.Valkey.dll
    ├── Aspire.Hosting.Valkey.xml
    └── ...
```

---

## Certificate Management

The CLI includes native HTTPS certificate management via ASP.NET Core's `CertificateManager` library, ported directly into the native AOT binary. This eliminates the need for a separate dev-certs tool or subprocess for certificate operations.

### How It Works

The `CertificateManager` from `aspnetcore/src/Shared/CertificateGeneration/` is vendored into `src/Aspire.Cli/Certificates/CertificateGeneration/`. The original `EventSource`-based logging (AOT-incompatible) has been replaced with `ILogger`, making the code fully native AOT friendly.

Platform-specific implementations handle certificate store operations:
- **Windows**: `WindowsCertificateManager` — Windows certificate store + ACLs
- **macOS**: `MacOSCertificateManager` — Keychain management via `security` CLI
- **Linux**: `UnixCertificateManager` — OpenSSL + NSS databases + .NET trust store

### Certificate Tool Abstraction

The CLI uses an `ICertificateToolRunner` abstraction with a single implementation:

| Implementation | Description |
|----------------|-------------|
| `NativeCertificateToolRunner` | Calls `CertificateManager` directly (no subprocess) |

The `CertificateManager` is registered as a singleton via DI, with `ILogger` injected through the constructor:

```csharp
// Register CertificateManager (platform-specific) and certificate tool runner
builder.Services.AddSingleton(sp => CertificateManager.Create(sp.GetRequiredService<ILogger<NativeCertificateToolRunner>>()));
builder.Services.AddSingleton<ICertificateToolRunner, NativeCertificateToolRunner>();
```

### Key Operations

- **Check trust status**: `CertificateManager.ListCertificates()` + `GetTrustLevel()` — returns structured certificate info
- **Trust certificate**: `CertificateManager.EnsureAspNetCoreHttpsDevelopmentCertificate()` — creates and trusts dev cert
- **Platform detection**: `CertificateManager.Create()` selects the right platform implementation at startup

---

## AppHost Server

### Pre-built vs Dynamic Mode

The bundle includes a pre-built AppHost Server with core hosting only (no integrations). All integrations are downloaded on-demand:

| Condition | Mode | Description |
|-----------|------|-------------|
| Bundle detected | **Pre-built + Dynamic Loading** | Use pre-built server, download integrations as needed |
| No bundle detected | **Dynamic** | Generate and build project (requires SDK) |

### Integration Download Flow

When a project references integrations (e.g., `Aspire.Hosting.Redis`):

1. CLI reads `.aspire/settings.json` for package list
2. CLI checks local cache (`~/.aspire/packages/`)
3. Missing packages are downloaded via NuGet Helper
4. Packages are extracted to flat layout for assembly loading
5. AppHost Server loads integration assemblies at startup

### Pre-built Mode Execution

```bash
# CLI spawns the AppHost Server via aspire-managed
{managed}/aspire-managed server \
  --project {user-project-path} \
  --socket {socket-path}
```

### Integration Assembly Loading Architecture

When the user's project requires integrations not included in the bundle:

1. CLI downloads missing packages using NuGet Helper to a project-specific cache
2. NuGet Helper restores packages and creates a flat DLL layout in `ASPIRE_INTEGRATION_LIBS_PATH`
3. AppHost Server loads integration assemblies via `IntegrationLoadContext`

#### Aspire.TypeSystem

`Aspire.TypeSystem` is a standalone assembly containing the ATS (Aspire Type System) scanner, model types, and codegen contracts (`ICodeGenerator`, `ILanguageSupport`, `AtsContext`). It was extracted from `Aspire.Hosting` to establish a clean boundary — `Aspire.Hosting` does **not** reference `Aspire.TypeSystem`. The ATS attributes (`[AspireExport]`, `[AspireDto]`) remain in `Aspire.Hosting` and are discovered by name-based matching via `AttributeDataReader`.

All types in `Aspire.TypeSystem` are public. It is loaded in the default context and shared with the `IntegrationLoadContext`, so codegen contracts have the same type identity across the boundary.

#### IntegrationLoadContext

Integration assemblies are loaded in a custom `AssemblyLoadContext` ("Aspire.Integrations") that:

- **Probes directories** for assemblies — `ASPIRE_INTEGRATION_LIBS_PATH` and `AppContext.BaseDirectory`
- **Shares `Aspire.TypeSystem`** — always defers to the default context for type identity
- **Performs version unification** — before loading an assembly from the probe directory, checks if the default context already provides it at a higher or equal version; if so, defers to the default

```text
┌──────────────────────────────────────────────────────┐
│ Default Load Context (aspire-managed)                │
│  Aspire.Hosting.RemoteHost.dll (aspire-server)       │
│  Aspire.TypeSystem.dll  ◄── shared                   │
│  Framework assemblies (runtime-provided)             │
├──────────────────────────────────────────────────────┤
│ IntegrationLoadContext ("Aspire.Integrations")       │
│  Aspire.Hosting.dll                                  │
│  Aspire.Hosting.JavaScript.dll                       │
│  Aspire.Hosting.Azure.*.dll                          │
│  Third-party deps (Google.Protobuf, etc.)            │
│  Framework assemblies → deferred via unification     │
└──────────────────────────────────────────────────────┘
```

#### Version Unification

The NuGet restore for integration packages includes transitive dependencies on framework-provided assemblies at potentially older versions (e.g., `System.Diagnostics.DiagnosticSource` 6.0 when the runtime provides 9.0). Loading the old version causes `MissingMethodException` because `Aspire.Hosting` was compiled against newer runtime APIs.

The `IntegrationLoadContext` handles this by attempting to load the assembly from the default context first. If the default context provides a version that is higher or equal, it wins. This works in all deployment models (framework-dependent, self-contained, single-file) because it checks the default context at runtime rather than scanning framework directories on disk.

#### Cross-ALC Communication

All communication between the default context (RemoteHost) and the integration ALC uses:

- **Reflection** — `MethodInfo.Invoke()` for capability dispatch
- **Opaque handles** — objects stored as `object` in `HandleRegistry`, passed by handle ID
- **JSON marshalling** — `AtsMarshaller` converts between JSON and .NET types
- **Shared contracts** — `ICodeGenerator`/`ILanguageSupport` from `Aspire.TypeSystem` have matching type identity, so `IsAssignableFrom` works

---

## CLI Integration

### Transparent Mode Detection

The CLI automatically detects whether to use bundle or SDK mode based on its execution context:

1. **Bundle mode**: CLI is running from within a bundle layout (detected via relative paths)
2. **SDK mode**: CLI is installed via `dotnet tool` or running standalone

No user configuration or flags are required - the experience is identical regardless of installation method.

### Self-Update Command

`aspire update --self` updates the CLI to the latest version:

```bash
# Update the CLI to the latest version
aspire update --self

# Update to a specific channel
aspire update --self --channel daily
```

With self-extracting binaries, the update process is:
1. User selects a channel (stable, staging, daily)
2. Downloads the new self-extracting CLI binary (platform-specific archive)
3. Extracts archive to temp, finds new binary
4. Backs up current binary, swaps in the new one
5. Verifies new binary with `--version`
6. Calls `BundleService.ExtractAsync(force: true)` to proactively extract the embedded payload
7. Saves selected channel to global settings

The old bundle-update path (downloading a full tarball and applying via `IBundleDownloader`) has been removed. The self-extracting binary IS the bundle — one download, one file, everything included.

When running via `dotnet tool`, `aspire update --self` displays instructions to use `dotnet tool update`.

### Mode Detection Algorithm

```csharp
bool ShouldUseBundleMode()
{
    // Check if explicitly disabled via environment variable
    var useSdk = Environment.GetEnvironmentVariable("ASPIRE_USE_GLOBAL_DOTNET");
    if (string.Equals(useSdk, "true", StringComparison.OrdinalIgnoreCase))
        return false;

    // Auto-detect: check if CLI is running from within a bundle layout
    var layoutPath = DiscoverRelativeLayout();
    if (layoutPath != null && ValidateLayout(layoutPath))
        return true;

    // Fall back to SDK mode
    return false;
}
```

### Environment Variable Override

For advanced scenarios (testing, debugging), a single environment variable can force SDK mode:

| Variable | Description |
|----------|-------------|
| `ASPIRE_USE_GLOBAL_DOTNET=true` | Force SDK mode even when running from bundle |

This is not documented for end users - it's for internal testing and edge cases only.

---

## Installation

### One-Line Install Scripts

**Linux/macOS (bash):**
```bash
curl -fsSL https://aka.ms/install-aspire.sh | bash
```

**Windows (PowerShell):**
```powershell
irm https://aka.ms/install-aspire.ps1 | iex
```

### Script Behavior

With self-extracting binaries, install scripts can be simplified to:
1. Detect the current platform (OS + architecture)
2. Download the self-extracting binary to `~/.aspire/bin/aspire`
3. Run `aspire setup` to extract the embedded payload
4. Add `~/.aspire/bin` to PATH
5. Verify installation with `aspire --version`

The install scripts now exclusively support this self-extracting binary flow; archive-based bundles are no longer downloaded or extracted, and CI uploads only the self-extracting CLI binary.

### Installed Layout

The bundle installs components as siblings under `~/.aspire/`, with the CLI binary placed in `bin/` so that both bundle and CLI-only installs share the same PATH entry:

```text
~/.aspire/
├── bin/                    # CLI binary (shared path for both install methods)
│   └── aspire              #   - Self-extracting native AOT CLI (bundle install)
│                           #   - Or SDK-based CLI (CLI-only install)
│
├── .aspire-bundle-version  # Version marker (hex FNV-1a hash, written after extraction)
│
├── managed/                # Unified managed binary (self-contained)
│   └── aspire-managed      # Subcommands: dashboard | server | nuget
│
├── dcp/                    # Developer Control Plane
│   └── dcp
│
├── hives/                  # NuGet package hives (preserved across installs)
│   └── pr-{number}/
│       └── packages/
│
└── globalsettings.json     # Global CLI settings (preserved across installs)
```

**Key behaviors:**
- The CLI lives at `~/.aspire/bin/aspire` regardless of install method
- With self-extracting binaries, the CLI in `bin/` contains the embedded payload; `aspire setup` extracts siblings
- `.aspire-bundle-version` tracks the extracted version — extraction is skipped when hash matches
- `aspire-managed` is a single self-contained binary replacing separate runtime, dashboard, aspire-server, and tools directories
- Certificate management is native to the CLI (no external tool needed)
- NuGet hives and settings are preserved across installations and re-extractions
- `LayoutDiscovery` finds the bundle by checking the CLI's parent directory for components

### Script Options

**Linux/macOS:**
```bash
# Install specific version
curl -fsSL https://aka.ms/install-aspire.sh | bash -s -- --version 13.2.0

# Install to custom location
curl -fsSL https://aka.ms/install-aspire.sh | bash -s -- --install-dir /opt/aspire

# Skip PATH modification
curl -fsSL https://aka.ms/install-aspire.sh | bash -s -- --no-path
```

**Windows:**
```powershell
# Install specific version
irm https://aka.ms/install-aspire.ps1 | iex -Args '--version', '13.2.0'

# Install to custom location  
irm https://aka.ms/install-aspire.ps1 | iex -Args '--install-dir', 'C:\aspire'
```

### Default Installation Locations

| Component | Linux/macOS | Windows |
|-----------|-------------|---------|
| CLI (bundle or CLI-only) | `~/.aspire/bin/aspire` | `%USERPROFILE%\.aspire\bin\aspire.exe` |
| NuGet Hives | `~/.aspire/hives/` | `%USERPROFILE%\.aspire\hives\` |
| Settings | `~/.aspire/globalsettings.json` | `%USERPROFILE%\.aspire\globalsettings.json` |

### PR Build Installation

For testing PR builds before they are merged:

**CLI from PR (bundle-backed):**
```bash
# Linux/macOS
./eng/scripts/get-aspire-cli-pr.sh 1234

# Windows
.\eng\scripts\get-aspire-cli-pr.ps1 -PRNumber 1234
```

The PR script also downloads NuGet package artifacts (`built-nugets` and `built-nugets-for-{rid}`) and installs them as a NuGet hive at `~/.aspire/hives/pr-{N}/packages/`. This enables `aspire new` and `aspire add` to resolve PR-built package versions when the channel is set to `pr-{N}`.

---

## Configuration

Configuration is primarily done through environment variables. No user-editable configuration files are required.

### Environment Variable Precedence

```text
ASPIRE_* env vars > relative path auto-detect > assembly metadata (NuGet packages)
```

### Integration Cache

Downloaded integration packages are cached in:
- Linux/macOS: `~/.aspire/packages/`
- Windows: `%LOCALAPPDATA%\Aspire\packages\`

---

## Size and Distribution

### Size Estimates (Windows x64)

| Component | On Disk | Zipped |
|-----------|---------|--------|
| DCP (platform-specific) | ~286 MB | ~100 MB |
| Aspire Managed (self-contained: Dashboard + Server + NuGet + .NET Runtime) | ~65 MB | ~25 MB |
| CLI (native AOT, includes certificate management) | ~22 MB | ~10 MB |
| **Total** | **~373 MB** | **~135 MB** |

*Aspire Managed is a single self-contained binary that includes the .NET runtime, eliminating the need for a separate runtime directory.*
*Certificate management is handled natively in the CLI — no separate tool needed.*
*Sizes vary by platform. Linux tends to be smaller than Windows.*

### Distribution Formats

| Platform | Archive | Self-Extracting Binary |
|----------|---------|----------------------|
| Windows x64 | `aspire-{ver}-win-x64.zip` | `aspire.exe` (~134 MB) |
| Linux x64 | `aspire-{ver}-linux-x64.tar.gz` | `aspire` (~134 MB) |
| Linux ARM64 | `aspire-{ver}-linux-arm64.tar.gz` | `aspire` (~134 MB) |
| macOS x64 | `aspire-{ver}-osx-x64.tar.gz` | `aspire` (~134 MB) |
| macOS ARM64 | `aspire-{ver}-osx-arm64.tar.gz` | `aspire` (~134 MB) |

The self-extracting binary is the **recommended distribution format** — one file containing the CLI and full bundle payload. Archive format is still produced for compatibility with existing install scripts.

### Download Locations

- **GitHub Releases**: `https://github.com/microsoft/aspire/releases`
- **aspire.dev**: Direct download links on documentation site

---

## Development Mode

When developing Aspire itself, the bundle mode is **not** used even if `ASPIRE_REPO_ROOT` is set. This ensures developers can:

1. Make changes to `Aspire.Hosting.*` assemblies
2. Use project references instead of pre-built binaries
3. See their changes reflected immediately without rebuilding a bundle

### How It Works

The layout discovery system detects development mode and creates a "dev layout" with `Version = "dev"`. When the CLI detects a dev layout, it falls back to the standard SDK-based flow:

```csharp
if (layout.IsDevLayout)
{
    // Continue using SDK mode with project references
    return false;
}
```

### Environment Variables for Development

| Variable | Description |
|----------|-------------|
| `ASPIRE_REPO_ROOT` | Path to local Aspire repo (triggers dev layout detection) |
| `ASPIRE_USE_GLOBAL_DOTNET=true` | Force SDK mode, skip bundle detection entirely |

### Testing Bundle Infrastructure

To test bundle infrastructure during development without affecting the normal dev workflow:

1. Build the aspire-server standalone: `dotnet build src/Aspire.Hosting.RemoteHost`
2. Create a test bundle layout manually with the built artifacts
3. Set `ASPIRE_LAYOUT_PATH` to point to your test layout
4. The dev layout detection only activates when `ASPIRE_REPO_ROOT` is set

---

## Security Considerations

### Package Signing

| Platform | Mechanism |
|----------|-----------|
| Windows | Authenticode signature on CLI executable |
| macOS | Notarization + code signing |
| Linux | GPG signature file (`.asc`) |

### Checksum Verification

Each release publishes SHA-256 checksum files alongside the bundle binaries:

```text
aspire-13.2.0-linux-x64.tar.gz.sha256
aspire-13.2.0-win-x64.zip.sha256
```

Install scripts and `aspire update --self` should verify the downloaded file's SHA-256 hash against the `.sha256` file before installing. This catches corruption from partial downloads, network errors, or disk issues. The checksum covers the entire file (CLI binary + payload + trailer), providing end-to-end integrity verification.

The bundle trailer itself does **not** contain a payload hash. Integrity verification is the responsibility of the download/install path, not the extraction path. This keeps the trailer format simple and avoids a double-read of the ~150 MB payload during extraction.

### Runtime Isolation

The bundled .NET runtime is isolated from any globally-installed .NET:

- `DOTNET_ROOT` is set to the bundle's runtime directory
- `DOTNET_MULTILEVEL_LOOKUP=0` disables global probing
- No modification to system PATH or environment

### NuGet Security

- Package downloads use HTTPS only
- Package signatures are verified when available
- Authenticated feeds require explicit credential configuration

---

## Backward Compatibility

A core design principle of the bundle feature is **complete backward compatibility**. Users with existing workflows must not experience any breaking changes.

### Compatibility Requirements

1. **Existing SDK-based workflows continue to work unchanged**
   - If the .NET SDK is installed globally, all existing commands work identically
   - No new CLI flags required to use existing functionality
   - `aspire new`, `aspire add`, `aspire run` behave the same as before

2. **Dotnet tool installation remains supported**
   - `dotnet tool install -g Aspire.Cli` continues to work
   - `aspire update --self` shows `dotnet tool update` instructions when running as a tool

3. **Bundle mode is transparent**
   - No user action required to switch between bundle and SDK mode
   - CLI auto-detects which mode to use based on installation location
   - All commands produce the same user-visible output regardless of mode

### Detection Logic

The CLI determines its execution mode using this priority order:

```text
1. ASPIRE_USE_GLOBAL_DOTNET=true → Force SDK mode (for testing/debugging)
2. ASPIRE_REPO_ROOT is set       → Dev mode (use SDK with project refs)
3. Valid bundle layout found     → Bundle mode
4. .NET SDK available globally   → SDK mode
5. Neither available             → Error with installation instructions
```

### API Compatibility

Changes to internal CLI classes maintain backward compatibility through:

1. **New dependencies are optional or have sensible defaults**
   ```csharp
   // IBundleDownloader is nullable - if not registered, bundle update is skipped
   private readonly IBundleDownloader? _bundleDownloader;
   
   // ILayoutDiscovery always returns null if no layout found - SDK mode continues
   var layout = _layoutDiscovery.DiscoverLayout();
   if (layout is null) { /* fall back to SDK mode */ }
   ```

2. **DI registration is additive**
   - New services are registered alongside existing ones
   - Tests using full DI container continue to work
   - Tests mocking specific services are unaffected

3. **Graceful degradation**
   - If bundle components are missing, fall back to SDK
   - If NuGet operations are unavailable, fall back to `dotnet` commands
   - Error messages guide users to resolution

### Test Compatibility

Tests continue to work because:

1. **Integration tests use the full DI container**
   - New services (`ILayoutDiscovery`, `IBundleDownloader`) are registered
   - Tests discover no layout → SDK mode is used → existing behavior

2. **Unit tests mock at service boundaries**
   - Tests mocking `IProjectLocator`, `IPackagingService` etc. are unaffected
   - New services can be mocked independently if needed

3. **Test helpers register all services**
   - `CliTestHelper.CreateServiceCollection()` uses the same registration as production
   - No test-specific configuration needed for backward compatibility

### Environment Variable Summary

| Variable | Purpose | Effect |
|----------|---------|--------|
| `ASPIRE_USE_GLOBAL_DOTNET=true` | Force SDK mode | Bypasses bundle detection entirely |
| `ASPIRE_REPO_ROOT` | Development mode | Uses SDK with project references |
| `ASPIRE_LAYOUT_PATH` | Bundle location | Overrides auto-detection |
| `ASPIRE_DCP_PATH` | DCP override | Works in both modes |
| `ASPIRE_MANAGED_PATH` | Aspire-managed override (CLI only) | Works in both modes |

### Migration Path

Users migrating from SDK-based installation to bundle:

1. **No migration required** - existing projects work with bundle CLI
2. **Package references unchanged** - same NuGet packages, same versions
3. **Configuration preserved** - `~/.aspire/` settings continue to work
4. **Can switch back anytime** - reinstall via `dotnet tool` to return to SDK mode

### Version Compatibility

A key design principle is that the CLI and AppHost can be updated independently:

#### CLI Updated, AppHost Unchanged

When using a newer CLI with an older AppHost (NuGet packages):

1. **Protocol stability** - The JSON-RPC protocol between CLI and AppHost is versioned
2. **Feature detection** - CLI queries AppHost capabilities before using new features
3. **Graceful fallback** - Unknown features are skipped, core functionality preserved
4. **Package resolution** - NuGet packages from older Aspire versions continue to work

```text
CLI v10.0 ────► AppHost (Aspire.Hosting v9.x)
    │
    └── Uses SDK mode to build project with v9.x packages
        Works identically to v9.x CLI
```

#### AppHost Updated, CLI Unchanged

When using an older CLI with newer AppHost packages:

1. **Forward compatibility** - Older CLI can run newer AppHost projects
2. **New features unavailable** - Features requiring CLI support won't work
3. **Clear error messages** - When incompatibility detected, show upgrade guidance
4. **Core functionality works** - `aspire run`, `aspire add` continue to function

```text
CLI v9.x ────► AppHost (Aspire.Hosting v10.x)
    │
    └── Builds and runs project
        New v10 features that need CLI support are unavailable
        User sees: "Upgrade CLI for new features: aspire update --self"
```

#### Bundle Updated, SDK-based AppHost

When using bundle CLI with SDK-installed Aspire packages:

1. **Mode detection** - Bundle CLI detects SDK is available
2. **SDK mode activation** - Uses `dotnet build` for AppHost, not pre-built server
3. **Identical behavior** - Works exactly like dotnet-tool-installed CLI
4. **No conflicts** - Bundle runtime isolated from global .NET

```text
Bundle CLI ────► AppHost (via SDK)
    │
    └── Detects .NET SDK is installed
        Falls back to SDK mode
        Uses dotnet build, not pre-built server
```

#### Version Mismatch Handling

```csharp
// CLI checks AppHost protocol version
var serverVersion = await appHost.GetProtocolVersionAsync();
if (serverVersion < MinSupportedVersion)
{
    // Show upgrade message but continue if possible
    InteractionService.DisplayMessage("warning", 
        $"AppHost uses protocol v{serverVersion}, CLI expects v{MinSupportedVersion}+. " +
        "Some features may not work. Consider updating packages.");
}
```

---

## Future Considerations

### Out of Scope for Initial Release

- **Auto-update mechanism**: Users manually download new versions
- **Minimal bundle variant**: Full bundle only, no on-demand component download
- **Template creation**: `aspire new` for .NET templates still requires SDK

### Potential Enhancements

1. **Modular bundles**: Base + optional integration packs
2. **CDN distribution**: Faster downloads via global CDN
3. **Update command**: `aspire update --self` for bundle updates (implemented)
4. **Bundle compression**: Support for zstd in ZIP format (better ratios)
5. **Single-file runtime bundle**: Consolidate runtime folder into single file (see below)

### Single-File Runtime Bundle (Future Option)

The current bundle layout includes a `runtime/` folder (~106 MB) containing the .NET runtime:

```text
runtime/
├── dotnet.exe                 # Host/muxer
├── host/fxr/{version}/        # hostfxr
└── shared/
    ├── Microsoft.NETCore.App/{version}/
    └── Microsoft.AspNetCore.App/{version}/
```

A future enhancement could consolidate this into a **single-file runtime binary** using the `Microsoft.NET.HostModel.Bundle` API. This would:

1. Create a single `dotnet-aspire` executable containing:
   - The apphost (native executable stub)
   - hostfxr and hostpolicy (statically linked or bundled)
   - Microsoft.NETCore.App framework assemblies
   - Microsoft.AspNetCore.App framework assemblies

2. Use .NET's bundle format which:
   - Memory-maps managed assemblies directly from the bundle (no extraction)
   - Extracts only native libraries to a temp directory when needed
   - Caches extracted files across runs

#### Proposed Implementation

**Step 1: Create a minimal host application**

```csharp
// tools/AspireRuntimeHost/Program.cs
// Minimal app that forwards execution to the target DLL
public class Program
{
    public static int Main(string[] args)
    {
        // The actual DLL to run is passed as first argument
        // This app just provides the runtime context
        return 0;
    }
}
```

**Step 2: Publish as single-file with shared frameworks**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  </PropertyGroup>
</Project>
```

**Step 3: Use the Bundler API programmatically**

```csharp
using Microsoft.NET.HostModel.Bundle;

var bundler = new Bundler(
    hostName: "dotnet-aspire",
    outputDir: outputPath,
    options: new BundleOptions(
        targetOS: targetOS,
        targetArch: targetArch,
        enableCompression: true));

// Add framework assemblies
foreach (var dll in frameworkAssemblies)
{
    bundler.AddToBundle(dll, BundlerFileType.Assembly);
}

// Add native libraries  
foreach (var native in nativeLibs)
{
    bundler.AddToBundle(native, BundlerFileType.NativeBinary);
}

// Generate the bundle
bundler.GenerateBundle();
```

#### Bundle Structure

The resulting `dotnet-aspire` binary would have this internal structure:

```text
dotnet-aspire (single file, ~100-120 MB)
├── [AppHost Header]
├── [hostfxr + hostpolicy code]
├── [Bundle Manifest]
│   ├── File count, offsets, sizes
│   └── Compression metadata
├── [Framework Assemblies - Memory Mapped]
│   ├── System.Runtime.dll
│   ├── System.Collections.dll
│   ├── Microsoft.AspNetCore.*.dll
│   └── ... (~800 assemblies)
└── [Native Libraries - Extracted on demand]
    ├── coreclr.dll/libcoreclr.so
    ├── clrjit.dll/libclrjit.so
    └── System.*.Native.dll/so
```

#### Runtime Behavior

1. **First run**: Native libraries extracted to `~/.aspire/runtime-cache/{bundle-hash}/`
2. **Subsequent runs**: Cache hit, no extraction needed
3. **Managed code**: Memory-mapped directly from bundle, no disk I/O

#### Usage in CLI

```csharp
// LayoutProcessRunner would use the single-file runtime
public async Task<int> RunAsync(string dllPath, string[] args)
{
    var runtimePath = _layout.GetSingleFileRuntime(); // "dotnet-aspire"
    
    var process = new Process();
    process.StartInfo.FileName = runtimePath;
    process.StartInfo.ArgumentList.Add("exec");
    process.StartInfo.ArgumentList.Add(dllPath);
    // ...
}
```

#### Estimated Sizes

| Component | Current | Single-File |
|-----------|---------|-------------|
| runtime/ folder | 106 MB | - |
| dotnet-aspire binary | - | ~100-120 MB |
| **Net change** | - | ~0-15 MB smaller |

The main benefit is **simplicity** (one file vs folder tree) rather than size reduction.

#### Trade-offs

**Pros:**
- Single file instead of ~200 files in runtime/ folder
- Simpler xcopy deployment
- Managed assemblies load faster (memory-mapped)
- No need to manage runtime folder structure

**Cons:**
- Native libraries still extract to disk (required by OS loader)
- More complex build process
- Harder to debug/inspect
- Updates require full binary replacement

#### Implementation Effort

- **Low**: Self-extracting archive (compress runtime/, extract on first use)
- **Medium**: Use existing single-file publish infrastructure
- **High**: Custom Bundler integration with proper framework resolution

#### Dependencies

- `Microsoft.NET.HostModel` NuGet package (contains Bundler API)
- Understanding of deps.json and runtimeconfig.json generation
- Platform-specific native library handling

---

## Implementation Status

This section tracks the implementation progress of the bundle feature.

### Completed

- [x] **Specification document** - This document (`docs/specs/bundle.md`)
- [x] **Layout configuration classes** - `src/Aspire.Cli/Layout/LayoutConfiguration.cs`
- [x] **Layout discovery service** - `src/Aspire.Cli/Layout/LayoutDiscovery.cs`
- [x] **Layout process runner** - `src/Aspire.Cli/Layout/LayoutProcessRunner.cs`
- [x] **Bundle NuGet service** - `src/Aspire.Cli/NuGet/BundleNuGetService.cs`
- [x] **NuGet operations** - embedded in `src/Aspire.Managed/NuGet/`
  - [x] Search command (NuGet v3 HTTP API)
  - [x] Restore command (NuGet RestoreRunner)
  - [x] Layout command (flat DLL + XML doc layout from project.assets.json)
- [x] **Layout services registered in DI** - `src/Aspire.Cli/Program.cs`
- [x] **Pre-built AppHost server class** - `src/Aspire.Cli/Projects/PrebuiltAppHostServer.cs`
- [x] **DCP/Dashboard/Runtime env var support** - `src/Aspire.Hosting/Dcp/DcpOptions.cs`, `src/Aspire.Hosting/Dashboard/DashboardEventHandlers.cs`
  - `ASPIRE_DCP_PATH` environment variable
  - `ASPIRE_DASHBOARD_PATH` environment variable
  - `ASPIRE_RUNTIME_PATH` environment variable (guest apphosts only)
- [x] **Shared discovery logic** - `src/Shared/BundleDiscovery.cs`
  - `TryDiscoverDcpFromEntryAssembly()` / `TryDiscoverDcpFromDirectory()`
  - `TryDiscoverDashboardFromEntryAssembly()` / `TryDiscoverDashboardFromDirectory()`
  - `TryDiscoverRuntimeFromEntryAssembly()` / `TryDiscoverRuntimeFromDirectory()`
  - `GetDotNetExecutablePath()` - env → disk → PATH fallback
- [x] **GuestAppHostProject bundle mode integration** - `src/Aspire.Cli/Projects/GuestAppHostProject.cs`
  - Automatic bundle mode detection via `TryGetBundleLayout()`
  - `PrepareSdkModeAsync()` for traditional SDK-based server build
  - `PrepareBundleModeAsync()` for pre-built server from bundle
- [x] **Standalone aspire-server project** - `src/Aspire.Hosting.RemoteHost/`
  - Pre-built server for bundle distribution
  - Framework-dependent deployment (uses bundled runtime)
- [x] **Certificate management** - `src/Aspire.Cli/Certificates/`
  - `ICertificateToolRunner` abstraction
  - `NativeCertificateToolRunner` - calls `CertificateManager` directly (no subprocess)
  - `CertificateGeneration/` - vendored from aspnetcore, EventSource replaced with ILogger
- [x] **Aspire Managed unified binary** - `src/Aspire.Managed/`
  - Self-contained single binary: `aspire-managed dashboard|server|nuget`
  - Replaces separate runtime, dashboard, aspire-server, and tools directories
- [x] **Bundle build tooling** - `tools/CreateLayout/`
  - Builds aspire-managed as self-contained single-file binary
  - Copies DCP
  - `--embed-in-cli` option creates self-extracting binary
- [x] **Installation scripts** - `eng/scripts/get-aspire-cli-pr.sh`, `eng/scripts/get-aspire-cli-pr.ps1`
  - Downloads bundle archive from PR build artifacts
  - Extracts to `~/.aspire/` with CLI in `bin/` subdirectory
  - Downloads and installs NuGet hive packages for PR channel
- [x] **Self-extracting binary** - `src/Shared/BundleTrailer.cs`, `src/Aspire.Cli/Bundles/`
  - 32-byte trailer format (magic + offset + size + version hash)
  - `IBundleService` / `BundleService` for centralized extraction logic
  - Thread-safe extraction with `SemaphoreSlim`
  - Platform-aware extraction (system `tar` on Unix, .NET `TarReader` on Windows)
  - Version tracking via `.aspire-bundle-version` marker file
- [x] **Setup command** - `src/Aspire.Cli/Commands/SetupCommand.cs`
  - `aspire setup [--install-path]`
  - Delegates to `IBundleService.ExtractAsync()`
- [x] **Self-update simplified** - `src/Aspire.Cli/Commands/UpdateCommand.cs`
  - `aspire update --self` downloads new CLI, swaps binary, extracts via `IBundleService`
  - Removed old `ExecuteBundleSelfUpdateAsync` / `IBundleDownloader` dependency
- [x] **Unit tests** - `tests/Aspire.Cli.Tests/BundleServiceTests.cs`
  - 10 tests: roundtrip, edge cases, version marker, tar.gz extraction with strip-components

### In Progress

- [ ] Integrate NuGet service with AddCommand

### Pending

- [ ] Multi-platform build workflow (GitHub Actions)
- [ ] Simplify install scripts to thin download + `aspire setup` wrappers

### Key Files

| File | Purpose |
|------|---------|
| `src/Aspire.Cli/Layout/LayoutConfiguration.cs` | Configuration classes for layout structure |
| `src/Aspire.Cli/Layout/LayoutDiscovery.cs` | Priority-based layout discovery (env > config > relative) |
| `src/Aspire.Cli/Layout/LayoutProcessRunner.cs` | Run managed DLLs via layout's .NET runtime |
| `src/Aspire.Cli/NuGet/BundleNuGetService.cs` | NuGet operations wrapper for bundle mode |
| `src/Aspire.Managed/NuGet/` | NuGet search/restore/layout commands (embedded in aspire-managed) |
| `src/Aspire.Cli/Projects/PrebuiltAppHostServer.cs` | Bundle-mode server runner |
| `src/Aspire.Cli/Projects/GuestAppHostProject.cs` | Main polyglot handler with bundle/SDK mode switching |
| `src/Aspire.Hosting/Dcp/DcpOptions.cs` | DCP/Dashboard path resolution with env var support |
| `src/Aspire.Cli/Certificates/ICertificateToolRunner.cs` | Certificate tool abstraction |
| `src/Aspire.Cli/Certificates/NativeCertificateToolRunner.cs` | Native certificate management (no subprocess) |
| `src/Aspire.Cli/Certificates/CertificateGeneration/` | Vendored CertificateManager from aspnetcore (ILogger-based) |
| `src/Aspire.Managed/Program.cs` | Unified managed binary entry point (dashboard/server/nuget) |
| `src/Shared/BundleTrailer.cs` | (Deleted) Previously held trailer read/write logic |
| `src/Aspire.Cli/Bundles/IBundleService.cs` | Bundle extraction interface + result enum |
| `src/Aspire.Cli/Bundles/BundleService.cs` | Centralized extraction with .NET TarReader |
| `src/Aspire.Cli/Commands/SetupCommand.cs` | `aspire setup` command for bundle extraction |
| `src/Aspire.Cli/Utils/ArchiveHelper.cs` | Shared .zip/.tar.gz extraction utility |
| `tools/CreateLayout/Program.cs` | Bundle build tool (layout assembly + self-extracting binary) |
| `eng/Bundle.proj` | MSBuild orchestration for bundle creation |
| `tests/Aspire.Cli.Tests/BundleServiceTests.cs` | Unit tests for bundle service and extraction |

---

## Build Process

The bundle is built using the `tools/CreateLayout` tool, which assembles all components into the final bundle layout.

### Aspire Managed Build

The `aspire-managed` binary is published as a self-contained single-file executable, which includes the .NET runtime. This eliminates the need to separately download and bundle the .NET SDK/runtime.

```text
aspire-managed (self-contained, ~65 MB)
├── .NET 10 Runtime (embedded)
├── ASP.NET Core Framework (embedded)
├── Aspire.Dashboard (embedded)
├── Aspire.Hosting.RemoteHost / aspire-server (embedded)
├── NuGet Commands (embedded)
└── All managed dependencies
```

**Advantages over the previous SDK-download approach:**
1. **Simpler build**: No SDK download or extraction step
2. **Smaller total size**: Single binary with tree-shaking vs full runtime directory
3. **Single file**: One binary instead of hundreds of files across multiple directories
4. **Version consistency**: All components compiled together

### Build Steps

1. **Build aspire-managed** as a self-contained single-file binary (includes .NET runtime, Dashboard, AppHost Server, NuGet operations)
2. **Download and copy DCP** binaries
3. **Create archive** (tar.gz for Unix, ZIP for Windows) with `COPYFILE_DISABLE=1` to suppress macOS xattr headers
4. **Create self-extracting binary** — appends tar.gz payload + 32-byte trailer to native AOT CLI

### Self-Extracting Binary Build

`Bundle.proj` passes `--embed-in-cli` to `CreateLayout`, which:

1. Takes the native AOT CLI binary and the tar.gz archive
2. Copies the CLI binary to `{output}.bundle`
3. Appends the tar.gz payload
4. Writes the 32-byte trailer (magic + offset + size + version hash)
5. Replaces the original CLI binary with the bundle

```bash
# Build command
dotnet msbuild eng/Bundle.proj /p:TargetRid=osx-arm64 /p:Configuration=Release /p:BundleRuntimeVersion=10.0.102

# Output: artifacts/bundle/osx-arm64/aspire (self-extracting, ~134 MB)
```

The resulting binary is a valid native executable that also contains the full bundle. Running `aspire --version` works immediately; `aspire setup` extracts the payload.
