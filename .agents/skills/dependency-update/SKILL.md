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

A single-file C# app is bundled alongside this skill at `.agents/skills/dependency-update/MigratePackage.cs`. It uses the Azure DevOps .NET SDK (`PipelinesHttpClient`) with `Azure.Identity` for authentication. Use it to trigger and monitor pipeline runs — it handles prerequisite checks, pipeline triggering, and polling.

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
dotnet .agents/skills/dependency-update/MigratePackage.cs -- --check-prereqs
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
dotnet .agents/skills/dependency-update/MigratePackage.cs -- "<PackageName>" "<PackageVersion>"
```

The script will:
1. Authenticate via Azure.Identity (AzureCliCredential)
2. Trigger the `dotnet-migrate-package` pipeline using `PipelinesHttpClient.RunPipelineAsync`
3. Poll every 30 seconds via `PipelinesHttpClient.GetRunAsync` until completion (default 15-minute timeout)
4. Report success or failure

**Example:**
```bash
dotnet .agents/skills/dependency-update/MigratePackage.cs -- "Hex1b" "0.49.0"
dotnet .agents/skills/dependency-update/MigratePackage.cs -- "Hex1b.McpServer" "0.49.0"
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

Package major versions do not have to match the target framework: a compatible dependency can lift to 11.x even for `net10.0`. Prefer framework-provided assemblies over explicit package references, including `System.Text.Json` and the `Microsoft.Extensions.*` abstractions now provided by .NET 11's `Microsoft.NETCore.App`. Keep references where an older supported target still needs them. EF providers can impose narrower constraints; Pomelo currently remains on EF Core 9 with project-local health-check pins. Inspect the provider's dependency ranges rather than assuming its major version follows the project's TFM.

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

- **`Npgsql.EntityFrameworkCore.PostgreSQL`** — A new .NET/EF Core major may reach GA before the matching stable provider. Follow [Npgsql EF major-version transitions](#npgsql-ef-major-version-transitions); do not hold all EF integrations on prerelease for Npgsql.
- **`Pomelo.EntityFrameworkCore.MySql`** — The net10 integration currently retains the EF Core 9 provider and matching project-local health-check pins. Revisit those pins together when upgrading the provider.
- **`Microsoft.AI.Foundry.Local`** — Has historically had broken transitive dependency metadata (NU1603). Check if the issue is resolved before updating.
- **`Spectre.Console`** — Currently on pre-release. Always update to the latest pre-release, not the latest stable. Verify hyperlink rendering behavior hasn't changed (test: `ConsoleActivityLoggerTests`).
- **`Milvus.Client`** — No stable release exists. Always stays on pre-release.
- **`Azure.Monitor.OpenTelemetry.Exporter`** — Version 1.6.0 introduced AOT warnings. Hold at 1.5.0 until resolved. Version is in `eng/Versions.props`.
- **`Microsoft.FluentUI.AspNetCore.Components`** and **`Microsoft.FluentUI.AspNetCore.Components.Icons`** — Must not be automatically updated. Updates to these packages often have breaking changes and require careful manual testing of the dashboard. Always flag these for human review and manual update.

### Npgsql EF major-version transitions

1. **Check package metadata first.** Query available stable/prerelease provider versions, then inspect the selected provider's nuspec at `https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/{version}/npgsql.entityframeworkcore.postgresql.nuspec`. Check each TFM's dependency ranges, including `Microsoft.EntityFrameworkCore` and `.Relational`, and the corresponding HealthChecks EF nuspec. Matching major numbers do not establish compatibility; `[version]` means an exact dependency, not a minimum.
2. **Keep the stable line global.** When a compatible stable provider is unavailable, select the latest compatible provider prerelease in the affected TFM's `PackageVersion Update="Npgsql.EntityFrameworkCore.PostgreSQL"` block in `Directory.Packages.props`. Keep repo-wide EF Core and `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` properties in `eng/Versions.props` on GA, with matching stable version/repository/SHA metadata in `eng/Version.Details.xml` through the managed dependency-flow process. Do not downgrade other EF providers or flow records to accommodate Npgsql.
3. **Isolate compatibility pins to Npgsql's affected TFM.** In both component projects under `src/Components/`, override `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` to the compatible prerelease. Use `PackageReference Update` in `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` because it already has a direct reference. Use `PackageReference Include` in `Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL` because its reference otherwise arrives transitively through the non-Azure project; `Update` cannot add a missing item. In both matching projects under `tests/`, use a conditional `PackageVersion Update` for HealthChecks EF so central transitive pinning does not lift it back to GA. Preserve older supported TFMs' provider and EF dependency lines.
4. **Keep both integration packages prerelease.** Set `<SuppressFinalPackageVersion>true</SuppressFinalPackageVersion>` in both component projects while their provider requires prerelease dependencies. This applies to each multi-targeted package as a whole, not just its newest TFM. Leave main's global `StabilizePackageVersion` setting unchanged; the local suppression also protects these packages when a release build stabilizes the rest of the repository.

For example, [#22070](https://github.com/microsoft/aspire/pull/22070/commits/b7bc012095fdef8b7e03176aadd8dd500b5b4793) carried the pattern from [#22146](https://github.com/microsoft/aspire/pull/22146). These are historical examples, not permanent update targets or an assumption that either PR has merged:

- The [Npgsql EF `11.0.0-rc.3` nuspec](https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/11.0.0-rc.3/npgsql.entityframeworkcore.postgresql.nuspec) requires exactly EF Core and EF Core Relational `[11.0.0-rc.3.26522.148]`.
- [HealthChecks EF `11.0.0`](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.diagnostics.healthchecks.entityframeworkcore/11.0.0/microsoft.extensions.diagnostics.healthchecks.entityframeworkcore.nuspec) requires EF Core Relational `>= 11.0.0`, which conflicts with that provider. [HealthChecks EF `11.0.0-rc.3.26522.148`](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.diagnostics.healthchecks.entityframeworkcore/11.0.0-rc.3.26522.148/microsoft.extensions.diagnostics.healthchecks.entityframeworkcore.nuspec) permits the matching EF prerelease. This does not require globally downgrading the other HealthChecks packages.

The project-local overrides for that example were:

```xml
<!-- Non-Azure component: override its existing direct reference. -->
<ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">
  <PackageReference Update="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" VersionOverride="11.0.0-rc.3.26522.148" />
</ItemGroup>

<!-- Azure component: add a direct reference to constrain the transitive dependency. -->
<ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">
  <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" VersionOverride="11.0.0-rc.3.26522.148" />
</ItemGroup>

<!-- Both test projects: override the centrally pinned transitive version. -->
<ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">
  <PackageVersion Update="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="11.0.0-rc.3.26522.148" />
</ItemGroup>
```

**Verify a dependency update:**

- Run the repository restore bootstrap (`.\restore.cmd` on Windows or `./restore.sh` on Linux/macOS), then restore both component projects and both matching test projects using approved feeds. Inspect each TFM's resolved `project.assets.json` graph (use `dotnet list <project> package --include-transitive --framework <tfm>` for a readable view). Verify the provider, EF Core, EF Core Relational, and HealthChecks EF versions, not just evaluated MSBuild properties. In the example, `net11.0` must resolve the RC3 graph while `net10.0` retains Npgsql EF/EF Core 10.x. Resolve downgrade/range warnings rather than suppressing them, and confirm unaffected EF integrations still resolve GA.
- Run both Azure and non-Azure test projects across their supported TFMs. Use the repository's MTP filters; for example, `dotnet test --project tests\Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests\Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"`, then repeat for `tests\Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL.Tests\Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL.Tests.csproj`. Container-backed tests require Docker; report skipped or blocked coverage explicitly.
- Pack both components with normal settings and with `/p:StabilizePackageVersion=true` as a command-line release-build simulation, without editing the global setting. Inspect the resulting package versions and nuspec dependency groups: both integration packages must retain prerelease suffixes even under stabilization, and older TFM groups must remain on their supported dependency line.

**Retire the workaround together.** When a compatible stable provider is published, recheck its nuspec ranges, update the affected TFM's provider pin to stable, remove both components' temporary HealthChecks EF `VersionOverride` entries (remove the Azure-only direct reference if no longer needed), remove both tests' temporary `PackageVersion Update` entries, and remove `SuppressFinalPackageVersion` from both components. Remove obsolete workaround comments and repeat graph, test, and pack verification. Under stabilization, both packages should then be eligible for final versions. Do not carry RC pins or suppression forward merely because they worked for the previous release.

## Important Constraints

- **One package per pipeline run** — The script processes one dependency at a time
- **Wait for completion** — Don't start the next pipeline run until the current one finishes (the pipeline queue is aggressive)
- **Always check nuget.org** — The mirroring pipeline pulls from nuget.org
- **Verify versions exist** — Before triggering the pipeline, confirm the version exists on nuget.org
- **Don't modify NuGet.config** — Package sources are managed separately; this skill only handles version updates. Temporary nuget.org additions for verification must be reverted before committing.
- **Use Darc for managed dependency flow** — `eng/Version.Details.xml` tracks the .NET 11 package line, using canonical properties in `eng/Versions.props` (package ID without punctuation plus `Version`). The `Net10Version` properties are manual .NET 10 servicing pins: update them together without changing the .NET 11 flow records. Framework-provided .NET 11 abstractions have no managed package records; retain only their .NET 10 pins. During an explicitly requested initial migration, align each managed record's version, repository URI, and SHA with the actual package's nuspec and its canonical property; do not invent source metadata or change cloud subscriptions. Keep compatibility aliases derived from canonical properties.
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
