# Aspire Bundle - Self-Contained Distribution

> **Status:** Draft Specification  
> **Last Updated:** September 2026

This document describes the bundle implementation in this repository. It distinguishes the build payload, the installed layout, and compatibility with older AppHosts; these are not interchangeable layouts or launch contracts.

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Bundle Layout](#bundle-layout)
4. [Self-Extracting Binary](#self-extracting-binary)
5. [Extraction and Lifetime](#extraction-and-lifetime)
6. [Component Discovery](#component-discovery)
7. [AppHost Compatibility](#apphost-compatibility)
8. [NuGet and AppHost Server](#nuget-and-apphost-server)
9. [Certificate Management](#certificate-management)
10. [Installation and Configuration](#installation-and-configuration)
11. [Build Process](#build-process)
12. [Security Considerations](#security-considerations)
13. [Validation](#validation)

## Overview

The Aspire Bundle distributes the CLI with its runtime components:

| Component | Deployment | Purpose |
|-----------|------------|---------|
| `aspire[.exe]` | Native AOT | CLI, including native development-certificate management |
| `managed/aspire-managed[.exe]` | Self-contained single-file executable | AppHost Server, NuGet operations, terminal hosting, and a Dashboard compatibility forwarder |
| `dashboard/Aspire.Dashboard[.exe]` | Native AOT | Dashboard web application |
| `dashboard/wwwroot/` and native dependencies | Publish assets | Dashboard scripts, styles, fonts, images, and SQLite native library |
| `dcp/` | Platform-specific native binaries | Developer Control Plane |

The bundle removes the need to acquire DCP and Dashboard separately when using the bundled components. Its pre-built AppHost Server and NuGet helper do not require a globally installed .NET SDK.

This does **not** mean every application can run without other prerequisites. .NET application development still requires the appropriate SDK, guest languages require their own toolchains, and container resources require a container runtime. Integration packages and application dependencies must be available locally or restored from their configured sources. Offline operation requires those dependencies to have been acquired already.

The managed helper contains its own runtime. The CLI and Dashboard are Native AOT executables; there is no separate `runtime/` directory or `ASPIRE_RUNTIME_PATH` contract for the current bundle.

## Architecture

```text
aspire (Native AOT CLI)
  |
  +-- managed/aspire-managed
  |     +-- server: AppHost Server and integration loading
  |     +-- nuget: package search, restore, and probe manifests
  |     +-- terminalhost: terminal hosting
  |     +-- dashboard: compatibility forwarder to native Dashboard
  |
  +-- dashboard/Aspire.Dashboard (Native AOT)
  |     +-- wwwroot/ and native dependencies
  |
  +-- dcp/ (Developer Control Plane)

Guest AppHost <---- JSON-RPC ----> AppHost Server
                                      |
                                      +-- DCP orchestration
                                           +-- application resources
                                           +-- Dashboard
```

For a guest-language AppHost, the CLI prepares integration dependencies, starts the AppHost Server, and starts the guest process that communicates with it. For a .NET AppHost, the CLI uses the .NET project workflow and supplies bundle component paths when available.

Standalone `aspire dashboard run` and CLI profile capture launch Dashboard directly. The Dashboard entry point sets `ContentRootPath = AppContext.BaseDirectory`, so its static assets are resolved beside the executable regardless of the caller's working directory.

`aspire-managed dashboard` does **not** host Blazor in the managed helper. It forwards arguments to the sibling native Dashboard and propagates its exit status. This preserves a launch contract used by older AppHosts without bringing the Dashboard back into the managed helper's dependency graph.

## Bundle Layout

### Build Payload

`CreateLayout` assembles a payload containing these directories:

```text
{payload}/
├── managed/
│   └── aspire-managed[.exe]
├── dashboard/
│   ├── Aspire.Dashboard[.exe]
│   ├── e_sqlite3.dll             # Windows
│   │                             # Linux: libe_sqlite3.so
│   │                             # macOS: libe_sqlite3.dylib
│   ├── wwwroot/
│   └── ...                      # Other publish assets, excluding debug symbols
└── dcp/
    ├── dcp[.exe]
    └── ...                      # DCP extensions and supporting files
```

The CLI is published separately with the payload embedded. `CreateLayout` does not copy the CLI into the payload or modify a CLI binary.

Dashboard layout creation requires a RID-specific publish for the requested configuration. It fails if the executable, `wwwroot`, or a nonempty platform-specific SQLite library is missing. It copies the publish assets and native dependencies, excluding debug symbols. DCP must also be available for the requested target platform; it is acquired from build-time NuGet packages and copied into the payload.

### Installed Layout

For a script installation using the default prefix, the installed structure is:

```text
~/.aspire/
├── bin/
│   ├── aspire[.exe]
│   └── .aspire-install.json      # Install-route sidecar
├── .aspire-bundle-lock
├── .aspire-bundle-version       # Current binary fingerprint
├── bundle/ -> versions/{id}/    # Symlink or Windows junction
├── versions/
│   ├── {id}/
│   │   ├── managed/
│   │   ├── dashboard/
│   │   ├── dcp/
│   │   └── .leases/            # Live bundle users
│   └── ...                     # Older versions retained while in use
├── hives/
└── globalsettings.json
```

This is an example of the script route, not a universal install location. Package-manager installations can keep their payload beside the resolved CLI binary. Sidecar-less installations use the Aspire home directory. See [installation routes](install-routes.md) for route-specific ownership and locations.

The stable `bundle/` path selects the active version. Processes started through a leased layout use paths rooted in the selected `versions/{id}/` directory, so a later update cannot redirect them to another version mid-operation.

Flat layouts with `managed/`, `dashboard/`, and `dcp/` directly under a root remain useful for build output and explicit layout discovery. They are distinct from the versioned extraction layout above.

## Self-Extracting Binary

The CLI embeds the compressed payload as the manifest resource `bundle.tar.gz`. `BundlePayloadPath` supplies that archive when publishing the CLI. The resource is part of the compiled executable and therefore within the executable's signing boundary; it is **not** appended after signing or described by a trailing bundle header.

```text
Native AOT CLI executable
  +-- CLI code and resources
  +-- embedded manifest resource: bundle.tar.gz
        +-- managed/
        +-- dashboard/
        +-- dcp/
```

`BundleService.IsBundle` detects the resource, and extraction opens its manifest resource stream. A build with no embedded payload returns `NoPayload` for explicit extraction; lazy extraction is a no-op. Discovery can still locate a separately supplied layout.

Payload archives produced by `CreateLayout` use `.tar.gz` on **all** platforms, including Windows. They are build inputs for the self-extracting CLI, not an independently complete CLI installation. Acquisition archives containing the CLI executable are separate artifacts and may use a different format.

There is no fixed bundle-size guarantee. Measure the executable, compressed payload, and extracted layout for the target RID and configuration. A retained older version also contributes to installed disk usage while a process holds a lease.

## Extraction and Lifetime

All payload extraction is owned by [BundleService](../../src/Aspire.Cli/Bundles/BundleService.cs), through [IBundleService](../../src/Aspire.Cli/Bundles/IBundleService.cs):

| Member | Purpose |
|--------|---------|
| `IsBundle` | Detect an embedded payload |
| `EnsureExtractedAsync()` | Ensure the current CLI's payload is available |
| `ExtractAsync(destinationPath, force, cancellationToken)` | Explicit extraction with a result describing success, reuse, absence, or validation failure |
| `EnsureExtractedAndAcquireLayoutAsync(holderKind, commandName, cancellationToken)` | Resolve a version-rooted layout and acquire a lease under the extraction lock |
| `GetDefaultExtractDir(processPath)` | Select the extraction root from the install route and resolved binary path |

### Extraction Sequence

1. Determine the extraction root and acquire `.aspire-bundle-lock` for cross-process synchronization.
2. Compare the stored binary fingerprint and validate the active version. Reuse an up-to-date layout when possible.
3. Derive the version directory ID from the fingerprint. If that version is already valid, reuse it; otherwise extract to a temporary sibling under `versions/`.
4. Extract with .NET `TarReader`, validating archive paths and links and preserving Unix permissions. Validate required components before promoting the temporary directory.
5. Promote the validated directory and switch the `bundle/` link. Validate discovery through the new link and attempt to restore the prior target if validation fails.
6. Write the current fingerprint to `.aspire-bundle-version`.
7. Perform best-effort cleanup of stale versions, temporary/failed directories, and obsolete layout paths, preserving versions that are still leased.

The marker is an implementation-generated binary fingerprint, not just an assembly version string or a cryptographic authenticity check. `ComputeVersionId` produces a filesystem-safe version-directory name using XxHash3. `force` bypasses the initial up-to-date shortcut; a valid existing version directory can still be reused.

### Leases and Child Processes

Extraction, active-version selection, and lease acquisition share the bundle lock. Callers that start bundle-owned processes retain the lease until the child exits or has acquired its own lease. `ASPIRE_BUNDLE_VERSION_DIR` passes the selected version directory to children; `aspire-managed` and the native Dashboard acquire their own leases.

This protects the Dashboard's executable **and static assets** from cleanup during a CLI update. Parent-process watchdogs and Windows job handling provide separate process-lifetime protection; a bundle lease is not a substitute for child-process cleanup.

### Entry Points

- `aspire setup` is a hidden installer command that explicitly extracts the current CLI payload. `--install-path` selects the extraction destination and `--force` bypasses the up-to-date shortcut. Without an explicit destination, setup uses the parent of the CLI binary's directory, independently of the install-route sidecar. Its help states that non-default paths require `ASPIRE_LAYOUT_PATH` for automatic discovery.
- Commands that require bundled components ensure extraction before using them. This includes Dashboard and AppHost workflows, not just guest-language projects.
- For self-updatable installs, `aspire update --self` replaces and verifies the CLI executable. The new CLI extracts its payload lazily when a subsequent command needs bundled components. The update does not proactively extract the payload or overwrite a running leased version in place.

## Component Discovery

### CLI Layout Discovery

[LayoutDiscovery](../../src/Aspire.Cli/Layout/LayoutDiscovery.cs) searches:

1. An explicit `ASPIRE_LAYOUT_PATH`.
2. The directory containing the CLI, then its parent, resolving the CLI symlink first and falling back to its original path.
3. The Aspire home directory, which is also the extraction fallback for sidecar-less installs.

At a candidate root, discovery recognizes `bundle/{managed,dashboard,dcp}` or the flat `{managed,dashboard,dcp}` structure. The current CLI requires all three directories and their executables. A legacy layout accepted by an SDK compatibility resolver is not necessarily a valid current CLI layout.

`ASPIRE_USE_GLOBAL_DOTNET=true` (or `1`) disables bundle-mode availability. Individual component and launch overrides are handled by their consumers; they are not a single global precedence rule for every command.

### SDK Build-Time Resolution

[ResolveAspireCliBundle](../../src/Aspire.Hosting.Tasks/ResolveAspireCliBundle.cs) resolves DCP, Dashboard, and terminal-host paths for AppHost build metadata. Its top-level order is:

1. Explicit `AspireCliBundlePath` MSBuild property.
2. Explicit `AspireCliPath` MSBuild property.
3. CLI discovery on `PATH`, when enabled.
4. Aspire home fallback.

The resolver handles install roots, component roots, versioned layouts, and compatible older bundle shapes. An explicitly supplied invalid path is reported when warnings are enabled rather than silently replaced by another installation.

### Hosting Runtime Resolution

[DcpOptions](../../src/Aspire.Hosting/Dcp/DcpOptions.cs) resolves DCP and Dashboard using:

1. Explicit `DcpPublisher` configuration (`CliPath` or `DashboardPath`).
2. `ASPIRE_DCP_PATH` or `ASPIRE_DASHBOARD_PATH` through configuration.
3. AppHost assembly metadata populated during the build.

Terminal-host resolution checks its environment/configuration keys and build metadata separately. The path and invocation arguments form a pair: the bundled `aspire-managed` requires the `terminalhost` argument.

Hosting does not discover a separate Dashboard runtime beside the application. Native Dashboard executables run directly. A framework-dependent Dashboard supplied for development uses `dotnet exec` with the Dashboard's own runtime configuration, not one rewritten to use the AppHost's framework versions.

## AppHost Compatibility

The CLI and AppHost can use different versions, but compatibility is determined by the specific launch contract, not by a promise that every historical CLI/package combination is supported.

- The CLI's `DashboardLaunchHelper` checks the AppHost Hosting version before choosing the native Dashboard. Older or unknown versions use `aspire-managed` as the compatibility entry point. Explicit user-supplied Dashboard paths remain overrides.
- The current managed helper's `dashboard` subcommand starts the sibling native Dashboard and forwards its arguments. It fails clearly if that executable is missing.
- The SDK resolver also understands transitional layouts that put `Aspire.Dashboard` under `managed/`, and older layouts that use the managed dispatcher. An existing but incomplete `dashboard/` directory is rejected instead of being mistaken for a legacy layout.
- Direct `dotnet run` uses the paths resolved into AppHost metadata or explicit configuration. It is not guaranteed to fall back to downloading Dashboard NuGet packages when the bundle is missing.
- .NET AppHost projects still use the .NET SDK to build and run. A pre-built guest AppHost Server does not replace the .NET project toolchain.

The native-version gate and executable-selection behavior are defined in [LayoutConfiguration.cs](../../src/Aspire.Cli/Layout/LayoutConfiguration.cs). The compatibility forwarder is in [Aspire.Managed/Program.cs](../../src/Aspire.Managed/Program.cs).

## NuGet and AppHost Server

`aspire-managed nuget` provides package search, restore, and package probe-manifest generation without requiring a globally installed SDK. Its command definitions live in [Aspire.Managed/NuGet](../../src/Aspire.Managed/NuGet); use the subcommands' `--help` output for their current arguments.

The pre-built AppHost Server loads the integrations required by the project. Package restore artifacts and probe manifests are cached beneath `<workspace>/.aspire/integrations/`:

```text
<workspace>/.aspire/integrations/
├── package-restore/
│   └── {restore-hash}/
│       ├── obj/project.assets.json
│       └── integration-package-probe-manifest.json
└── apphosts/
    └── {app-path-hash}/
        ├── appsettings.json
        ├── integration-package-probe-manifest.json
        └── project-layouts/items/{fingerprint}/libs/
```

NuGet package-backed assemblies and native libraries are loaded from the package cache through `ASPIRE_INTEGRATION_PROBE_MANIFEST_PATH`. Project-reference outputs are copied into immutable fingerprinted directories and supplied through `ASPIRE_INTEGRATION_LIBS_PATH`.

The integration load context keeps integration loading separate from the helper's default context, while sharing the type-system/code-generation contracts. This dynamic managed loading belongs to the self-contained AppHost Server, not to the Native AOT Dashboard.

## Certificate Management

The CLI's `NativeCertificateToolRunner` uses the vendored ASP.NET Core certificate-management implementation. It does not need to launch a bundled `dotnet dev-certs` tool. Platform-specific trust operations can still invoke operating-system utilities and require user interaction or permissions.

These development-certificate operations are separate from the Dashboard's configured client-certificate authentication. For Dashboard client-certificate formats and configuration, see the [Dashboard README](../../src/Aspire.Dashboard/README.md).

## Installation and Configuration

The [installation-route specification](install-routes.md) documents script, PR, localhive, package-manager, and sidecar-less installations. `IBundleService.GetDefaultExtractDir` implements the root selection for automatic extraction:

| Install route | Extraction root |
|---------------|-----------------|
| Script, PR, or localhive sidecar | Parent of the CLI binary's directory, normally the install prefix above `bin/` |
| Package-manager sidecar (`winget`, `brew`, `dotnet-tool`) | Directory containing the resolved CLI binary |
| Nix sidecar | `ASPIRE_HOME`, or `~/.aspire` by default, outside the read-only Nix store |
| No recognized install-route sidecar | `ASPIRE_HOME`, or `~/.aspire` by default |

Installers can explicitly prepare the payload using `aspire setup`; otherwise, the CLI extracts it lazily when needed. Setup's route-independent default is distinct from the automatic extraction roots above. Self-update behavior depends on the installation route; package-manager ownership must be respected. See `aspire update --help` and the installation-route specification rather than assuming every installation is replaced in place by the CLI.

PR acquisition scripts also populate channel-specific NuGet hives. Hives, integration caches, and user settings are distinct from the versioned runtime payload and are not replaced when the active `bundle/` link changes.

### Relevant Configuration

| Name | Purpose |
|------|---------|
| `ASPIRE_LAYOUT_PATH` | Explicit layout root for CLI discovery |
| `ASPIRE_HOME` | Default Aspire state root and fallback extraction location; portable install routes can use their own prefix for state |
| `ASPIRE_DCP_PATH` | DCP directory override |
| `ASPIRE_DASHBOARD_PATH` | Dashboard executable override |
| `ASPIRE_MANAGED_PATH` | Managed component override for CLI consumers; accepted path shape depends on the consumer |
| `ASPIRE_TERMINAL_HOST_PATH` | Terminal-host executable |
| `ASPIRE_TERMINAL_HOST_INVOCATION_ARGS` | Arguments needed by the terminal-host entry point, normally `terminalhost` |
| `ASPIRE_BUNDLE_VERSION_DIR` | Selected version directory passed to bundle-owned child processes for leases |
| `ASPIRE_USE_GLOBAL_DOTNET` | Force the SDK-based server path instead of bundle mode |
| `ASPIRE_REPO_ROOT` | Repository-development override used by CLI project and artifact resolution |
| `ASPIRE_INTEGRATION_PROBE_MANIFEST_PATH` | Manifest identifying package-cache integration assets |
| `ASPIRE_INTEGRATION_LIBS_PATH` | Copied project-reference integration outputs |

The repository-development path uses project references and local artifacts where supported. It is distinct from a deployed bundle. To test an assembled layout, use explicit layout configuration and the repository's bundle tools.

## Build Process

[eng/Bundle.proj](../../eng/Bundle.proj) orchestrates local bundle creation. Restore the repository toolchain first and install the target platform's Native AOT prerequisites.

```powershell
# Windows example, from the repository root
.\restore.cmd
dotnet msbuild eng/Bundle.proj /t:Build /p:TargetRid=win-x64 /p:Configuration=Release
```

```bash
# macOS example, from the repository root
./restore.sh
dotnet msbuild eng/Bundle.proj /t:Build /p:TargetRid=osx-arm64 /p:Configuration=Release
```

The build sequence is:

1. Publish `Aspire.Managed` as a self-contained single-file executable.
2. Publish `Aspire.Dashboard` with Native AOT for the same RID and configuration.
3. Restore the matching DCP package, using the target OS/architecture rather than the build machine's defaults.
4. Run `CreateLayout` to assemble the payload and create its `.tar.gz` archive.
5. Publish the Native AOT CLI with `BundlePayloadPath` pointing to that archive.

The assembled directories are under `artifacts/bundle/{rid}/`; the payload archive is `artifacts/bundle/aspire-{version}-{rid}.tar.gz`. The self-extracting CLI is in the CLI project's publish output, not in the payload directory.

`Configuration` defaults to Debug. `SkipManagedBuild=true` reuses existing Managed **and Dashboard** publishes; `SkipNativeBuild=true` skips the final CLI publish. They do not make missing payload components optional.

### CreateLayout

For already-published components:

```bash
dotnet run --project tools/CreateLayout -- \
  --output artifacts/bundle/linux-x64 \
  --artifacts artifacts \
  --rid linux-x64 \
  --configuration Release \
  --bundle-version local-test \
  --archive
```

`--output`, `--artifacts`, `--rid`, and `--configuration` are required. `--bundle-version`, `--archive`, and `--verbose` are optional. The output directory is cleaned before assembly. There is no `--embed-in-cli`, `--runtime`, or runtime-download option; embedding happens in the subsequent CLI publish.

For the complete tool contract, see [CreateLayout](../../tools/CreateLayout/README.md). CI packaging additionally uses [dashboardpack](../../eng/dashboardpack/Common.projitems), [clipack](../../eng/clipack/Common.projitems), signing targets, and the native-archive workflows.

## Security Considerations

- The embedded resource is part of the executable being signed. There is no post-signing appended payload or trailer whose integrity must be handled separately.
- Download verification belongs to each acquisition/update route. A bundle-version fingerprint is for reuse and directory identity, not proof of publisher authenticity. Do not infer signature or checksum guarantees from successful extraction alone.
- Extraction validates archive paths and links to prevent traversal outside the extraction root. Validate the staged layout before switching the active link.
- Version-rooted paths and leases protect running consumers from cleanup and mixed-version component selection during updates.
- Native Dashboard startup does not require a globally selected .NET runtime. The managed helper carries its runtime; SDK-based development remains a separate workflow.
- Package source configuration, authentication, and package trust remain the responsibility of the NuGet acquisition path. Bundling the NuGet helper does not remove those requirements.

## Validation

Tests cover different parts of the distribution contract:

| Area | Coverage |
|------|----------|
| Payload extraction and lifetime | `BundleServiceTests`, `BundleServiceIntegrationTests` |
| CLI layout discovery and leases | `LayoutConfigurationTests`, `LayoutDiscoveryReparsePointTests`, `LayoutProcessRunnerTests` |
| Layout assembly and required assets | `tests/Infrastructure.Tests/CreateLayout/` |
| AppHost build and launch compatibility | SDK resolution, .NET project, pre-built server, and Hosting Dashboard tests |
| Native startup and static assets | `eng/scripts/test-native-dashboard.ps1` |
| Native browser interactivity | `NativeAotDashboardTests`, including a working directory outside the publish directory and lease cleanup |
| CI selection and archives | Infrastructure workflow-selection and archive-download tests |

The dedicated native-archive workflow explicitly opts into the native browser test's outerloop category. Native executables must be exercised on a compatible runner; a successful cross-publish is not evidence that the target binary ran. See [build-cli-native-archives.yml](../../.github/workflows/build-cli-native-archives.yml) for the current matrix and execution exclusions.