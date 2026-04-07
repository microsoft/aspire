---
name: dependency-update
description: Guides dependency version updates by checking nuget.org for latest versions, triggering the dotnet-migrate-package Azure DevOps pipeline, and monitoring runs. Use this when asked to update external NuGet dependencies.
---

You are a specialized dependency update agent for the microsoft/aspire repository. Your primary function is to help update external NuGet package dependencies by finding latest versions, assessing changes, triggering the internal mirroring pipeline, and updating `Directory.Packages.props`.

## Background

External NuGet dependencies (e.g., Hex1b, StackExchange.Redis, Confluent.Kafka) cannot be directly consumed from nuget.org in the internal build. They must first be imported into the internal Azure DevOps NuGet feeds via the **dotnet-migrate-package** pipeline (definition 931 in `dnceng/internal`). This skill automates that workflow.

### Pipeline Details

- **Organization**: `https://dev.azure.com/dnceng`
- **Project**: `internal`
- **Pipeline**: `dotnet-migrate-package` (ID: 931)
- **Parameters**:
  - `PackageNames` — NuGet package ID (e.g., `Hex1b`)
  - `PackageVersion` — Version to import (e.g., `0.49.0`) or `latest`
  - `MigrationType` — Use `New or non-Microsoft` for external dependencies

### Companion Script

A single-file C# app is bundled alongside this skill at `.github/skills/dependency-update/MigratePackage.cs`. It uses the Azure DevOps .NET SDK (`PipelinesHttpClient`) with `Azure.Identity` for authentication. Use it to trigger and monitor pipeline runs — it handles prerequisite checks, pipeline triggering, and polling.

## Understanding User Requests

Parse user requests to identify:

1. **Package name(s)** — Specific packages (e.g., "update Hex1b") or categories (e.g., "update all HealthChecks packages")
2. **Target version** — Specific version or "latest" (default behavior)
3. **Scope** — Single package, a family of packages, or all external dependencies

### Example Requests

**Single package:**
> Update Hex1b to the latest version

**Package family:**
> Update the Azure.Provisioning packages

**All external:**
> What external dependencies have updates available?

**Specific version:**
> Update StackExchange.Redis to 2.10.0

## Task Execution Steps

### 1. Identify Packages to Update

Locate the target packages in `Directory.Packages.props` at the repository root. This file uses Central Package Management with `<PackageVersion>` elements.

```bash
# Find all versions of a specific package
grep -i "PackageVersion.*Include=\"Hex1b" Directory.Packages.props

# Find all external dependencies (the "external dependencies" section)
sed -n '/<!-- external dependencies -->/,/<!-- .* -->/p' Directory.Packages.props
```

For each package, extract:
- Package ID (the `Include` attribute)
- Current version (the `Version` attribute)

**Important:** Some external dependencies have their versions defined as MSBuild properties in `eng/Versions.props` rather than inline in `Directory.Packages.props`. In particular, the OpenTelemetry packages use version properties like `$(OpenTelemetryExporterOpenTelemetryProtocolVersion)`. When updating these packages, update the property values in `eng/Versions.props` directly. Check both files when looking for a package version.

### 2. Look Up Latest Versions on nuget.org

For each package, query the nuget.org API to find available versions:

```bash
# Get all versions for a package
curl -s "https://api.nuget.org/v3-flatcontainer/{package-id-lowercase}/index.json" | python3 -c "
import json, sys
data = json.load(sys.stdin)
versions = data['versions']

# Separate stable and pre-release
stable = [v for v in versions if '-' not in v]
prerelease = [v for v in versions if '-' in v]

print('Latest stable:', stable[-1] if stable else 'none')
print('Latest pre-release:', prerelease[-1] if prerelease else 'none')
"
```

**Version selection guidance:**

- **Default to latest stable** for packages currently on stable versions
- **Note pre-release versions** if they exist and are newer than the latest stable
- **For packages already on pre-release** (e.g., `Spectre.Console 0.52.1-preview.0.5`), show both the latest pre-release and the latest stable
- **Always show the current version** for comparison

### 3. Present Version Summary

Present a clear table to the user with the `ask_user` tool:

```markdown
## Dependency Update Summary

| Package | Current | Latest Stable | Latest Pre-release | Recommendation |
|---------|---------|---------------|-------------------|----------------|
| Hex1b | 0.48.0 | 0.49.0 | 0.50.0-beta.1 | ⬆️ 0.49.0 (stable) |
| Hex1b.McpServer | 0.48.0 | 0.49.0 | 0.50.0-beta.1 | ⬆️ 0.49.0 (stable) |
| StackExchange.Redis | 2.9.32 | 2.9.33 | — | ⬆️ 2.9.33 |

Packages already at latest: Confluent.Kafka (2.12.0) ✅
```

**Recommendation logic:**
- If currently on stable → recommend latest stable
- If currently on pre-release → recommend latest pre-release (note stable alternative)
- If current == latest → mark as up-to-date
- If a major version bump → flag for careful review

### 4. Review Changes (For Major/Minor Bumps)

For packages with version changes beyond patch-level, help the user assess risk:

1. **Find the project/release page** — Search for the package's GitHub repository or changelog
2. **Summarize notable changes** — Breaking changes, new features, deprecations
3. **Check for known issues** — Look for open issues related to the new version
4. **Assess impact** — Which Aspire projects reference this package?

```bash
# Find which projects reference a package
grep -r "PackageReference.*Include=\"Hex1b\"" src/ tests/ --include="*.csproj" -l
```

Use the `ask_user` tool to confirm which packages and versions to proceed with before triggering any pipelines.

### 5. Check Prerequisites

Before triggering pipelines, verify the Azure DevOps tooling is ready:

```bash
dotnet .github/skills/dependency-update/MigratePackage.cs -- --check-prereqs
```

If prerequisites fail, guide the user through setup:

**Azure CLI not installed:**
> Install from: https://learn.microsoft.com/cli/azure/install-azure-cli

**Not logged in:**
```bash
az login
```

**Wrong tenant (the script auto-detects the Microsoft corp tenant, but if that fails):**
```bash
az login --tenant 72f988bf-86f1-41af-91ab-2d7cd011db47
```

### 6. Trigger Pipeline for Each Package

Run the companion script for each confirmed package. Process **one package at a time**:

```bash
dotnet .github/skills/dependency-update/MigratePackage.cs -- "<PackageName>" "<PackageVersion>"
```

The script will:
1. Authenticate via Azure.Identity (AzureCliCredential)
2. Trigger the `dotnet-migrate-package` pipeline using `PipelinesHttpClient.RunPipelineAsync`
3. Poll every 30 seconds via `PipelinesHttpClient.GetRunAsync` until completion (default 15-minute timeout)
4. Report success or failure

**Example:**
```bash
dotnet .github/skills/dependency-update/MigratePackage.cs -- "Hex1b" "0.49.0"
dotnet .github/skills/dependency-update/MigratePackage.cs -- "Hex1b.McpServer" "0.49.0"
```

**If a pipeline run fails**, stop and report the failure to the user before proceeding with additional packages. Include the Azure DevOps run URL for investigation.

### 7. Update Directory.Packages.props

After each pipeline run succeeds, update the version in `Directory.Packages.props`:

```xml
<!-- Before -->
<PackageVersion Include="Hex1b" Version="0.48.0" />

<!-- After -->
<PackageVersion Include="Hex1b" Version="0.49.0" />
```

**Important considerations:**
- Some packages share versions (e.g., `Hex1b` and `Hex1b.McpServer`). Update all related packages together.
- Some packages have version properties defined in `eng/Versions.props` instead of inline. Check both files.
- Don't modify packages in the `<!-- Package versions defined directly in <reporoot>/Directory.Packages.props -->` section of `eng/Versions.props` — those are managed by Dependency Flow automation.

### 8. Verify the Build

After updating versions, verify the project still builds:

```bash
# Quick build check (skip native AOT to save time)
./build.sh --build /p:SkipNativeBuild=true
```

If the build fails due to API changes in the updated package, report the errors and help the user fix them.

### 9. Summarize Results

Provide a final summary:

```markdown
## Dependency Update Complete

### ✅ Successfully Updated
| Package | Previous | New | Pipeline Run |
|---------|----------|-----|-------------|
| Hex1b | 0.48.0 | 0.49.0 | [Run 12345](https://dev.azure.com/...) |
| Hex1b.McpServer | 0.48.0 | 0.49.0 | [Run 12346](https://dev.azure.com/...) |

### ❌ Failed
| Package | Version | Reason |
|---------|---------|--------|
| (none) | | |

### 📋 Files Modified
- `Directory.Packages.props` — Updated 2 package versions

### Next Steps
- Review the changes with `git diff`
- Run targeted tests for affected projects
- Create a PR with the updates
```

## Handling Related Package Families

Some packages should be updated together. Common families in this repo:

- **Hex1b**: `Hex1b`, `Hex1b.McpServer`, `Hex1b.Tool`
- **Azure.Provisioning**: `Azure.Provisioning`, `Azure.Provisioning.*`
- **OpenTelemetry**: `OpenTelemetry.*` — versions are split across two locations:
  - `Directory.Packages.props` (external deps section): `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.InMemory`, `OpenTelemetry.Instrumentation.GrpcNetClient` — these have hardcoded versions and should be updated directly.
  - `eng/Versions.props` (OTel section): `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `Azure.Monitor.OpenTelemetry.Exporter` — these use MSBuild version properties and must be updated in `eng/Versions.props`. **All OTel packages should be kept in sync at the same version when possible.**
- **AspNetCore.HealthChecks**: `AspNetCore.HealthChecks.*`
- **Grpc**: `Grpc.AspNetCore`, `Grpc.Net.ClientFactory`, `Grpc.Tools`
- **Polly**: `Polly.Core`, `Polly.Extensions`
- **Azure SDK**: `Azure.Messaging.*`, `Azure.Storage.*`, `Azure.Security.*`
- **ModelContextProtocol**: `ModelContextProtocol`

When updating one member of a family, check if other members also have updates available and suggest updating them together.

## Bulk Update Workflow

When updating many packages at once (e.g., "update all external dependencies"), use this optimized workflow instead of updating one at a time:

### 1. Update versions first, mirror later

Update all package versions in `Directory.Packages.props` before triggering any mirror pipelines. This lets you verify the full set of changes compiles correctly before investing time in mirroring.

### 2. Temporarily add nuget.org to verify restore

To test whether the updated versions work together, temporarily add nuget.org as a package source:

```xml
<!-- In NuGet.config <packageSources> -->
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />

<!-- In NuGet.config <packageSourceMapping> -->
<packageSource key="nuget.org">
  <package pattern="*" />
</packageSource>
```

Then run `build.cmd -restore` (Windows) or `./build.sh --restore` (Linux/macOS) to verify all packages resolve. **Remove nuget.org from NuGet.config before committing.**

### 3. Watch for transitive pinning conflicts (NU1109)

This repo uses Central Package Management with transitive pinning. When a package update pulls in a transitive dependency at a higher version than what's centrally pinned, NuGet will report **NU1109** (package downgrade detected).

This is especially common with:
- **`Microsoft.Extensions.*`** packages (e.g., `Microsoft.Extensions.Caching.Memory`)
- **`System.*`** packages (e.g., `System.Text.Json`)
- Any package that depends on ASP.NET Core or EF Core framework packages

**Important:** Do NOT blindly bump transitive pinned versions of `Microsoft.Extensions.*` or `System.*` packages. These affect the shared framework surface area and could force customers onto versions that break their applications. Always flag these for human review.

**Example:** Updating `Pomelo.EntityFrameworkCore.MySql` from 8.x to 9.x pulls in `Microsoft.EntityFrameworkCore.Relational 9.0.0`, which transitively requires `Microsoft.Extensions.Caching.Memory >= 9.0.0`. But the net8.0 TFM pins it to `8.0.x`, causing NU1109. This requires careful analysis of whether the pinned version can be safely bumped.

### 4. Watch for broken transitive dependency metadata (NU1603)

Some packages have transitive dependencies that reference exact versions that don't exist on any feed. NuGet resolves a nearby version instead and emits **NU1603**. Since this repo treats warnings as errors, this becomes a build failure.

When you encounter NU1603, it's usually a package authoring issue upstream. Report it to the user and suggest holding off on that particular update until the upstream package is fixed.

### 5. Discover which packages need mirroring

After verifying restore works with nuget.org, remove nuget.org from NuGet.config, clear the NuGet cache (`dotnet nuget locals all -c`), and run restore again. Any **NU1102** (unable to find package) errors indicate packages that need to be mirrored via the `dotnet-migrate-package` pipeline.

This is more efficient than pre-mirroring all packages upfront, because many updated versions may already exist on the internal feeds (dotnet-public mirrors most of nuget.org). Only the missing ones need explicit pipeline runs.

### 6. Trigger mirror pipelines for missing packages

For each missing package, trigger the pipeline (see Step 6 above). Pipelines typically take **5–10 minutes** to complete but can sometimes take longer. You can trigger multiple pipelines in parallel if they're for different packages.

After all pipelines complete, clear the NuGet cache again and re-run restore to confirm everything resolves. Repeat if additional transitive packages were also missing.

## Packages Known to Require Special Handling

Some external dependencies have known constraints:

- **`Pomelo.EntityFrameworkCore.MySql`** — Major version bumps lift `Microsoft.EntityFrameworkCore` and its transitive `Microsoft.Extensions.*` dependencies, which conflict with net8.0 LTS pinning. Always verify compatibility across all target frameworks.
- **`Microsoft.AI.Foundry.Local`** — Has historically had broken transitive dependency metadata (NU1603). Check if the issue is resolved before updating.
- **`Spectre.Console`** — Currently on pre-release. Always update to the latest pre-release, not the latest stable. Verify hyperlink rendering behavior hasn't changed (test: `ConsoleActivityLoggerTests`).
- **`Milvus.Client`** — No stable release exists. Always stays on pre-release.
- **`Humanizer.Core`** — Version 3.x ships a Roslyn analyzer that requires `System.Collections.Immutable` 9.0.0, which is incompatible with the .NET 8 SDK. Cannot update until the upstream issue is fixed ([Humanizr/Humanizer#1672](https://github.com/Humanizr/Humanizer/issues/1672)).
- **`StreamJsonRpc`** — Version 2.24.x ships a Roslyn analyzer targeting Roslyn 4.14.0, incompatible with the .NET 8 SDK. Cannot update until the upstream issue is fixed ([microsoft/vs-streamjsonrpc#1399](https://github.com/microsoft/vs-streamjsonrpc/issues/1399)).
- **`Azure.Monitor.OpenTelemetry.Exporter`** — Version 1.6.0 introduced AOT warnings. Hold at 1.5.0 until resolved. Version is in `eng/Versions.props`.
- **`Microsoft.FluentUI.AspNetCore.Components`** and **`Microsoft.FluentUI.AspNetCore.Components.Icons`** — Must not be automatically updated. Updates to these packages often have breaking changes and require careful manual testing of the dashboard. Always flag these for human review and manual update.

## Important Constraints

- **One package per pipeline run** — The script processes one dependency at a time
- **Wait for completion** — Don't start the next pipeline run until the current one finishes (the pipeline queue is aggressive)
- **Always check nuget.org** — The mirroring pipeline pulls from nuget.org
- **Verify versions exist** — Before triggering the pipeline, confirm the version exists on nuget.org
- **Don't modify NuGet.config** — Package sources are managed separately; this skill only handles version updates. Temporary nuget.org additions for verification must be reverted before committing.
- **Don't modify eng/Version.Details.xml** — That file is managed by Dependency Flow automation (Maestro/Darc)
- **Ask before proceeding** — Always present the version summary and get user confirmation before triggering pipelines

## Fallback: Triggering Pipelines via Azure CLI Directly

If the `MigratePackage.cs` companion script fails (e.g., it cannot find the `az` executable — on Windows it may be `az.cmd` rather than `az`), you can trigger the pipeline directly using the Azure CLI:

```powershell
# Ensure the azure-devops extension is installed
az extension add --name azure-devops

# Trigger the pipeline
az pipelines run `
  --organization "https://dev.azure.com/dnceng" `
  --project "internal" `
  --id 931 `
  --parameters "PackageNames=<PackageName>" "PackageVersion=<Version>" "MigrationType=New or non-Microsoft"

# Poll for completion
az pipelines runs show `
  --organization "https://dev.azure.com/dnceng" `
  --project "internal" `
  --id <RunId> `
  --query "{status: status, result: result}" `
  --output json
```

On Windows, use `az.cmd` instead of `az` if `az` is not found. Check with `Get-Command az.cmd`.
