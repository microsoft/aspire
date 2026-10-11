# Azure DevOps Pipelines

This directory contains Azure DevOps pipeline definitions for the microsoft/aspire repository.

## Pipeline Files

### Main Pipelines

- **`azure-pipelines.yml`** - Main internal pipeline for official builds
- **`azure-pipelines-public.yml`** - Weekly scheduled public builds
- **`azdo-tests.yml`** - Manual trigger pipeline for testing (use `/azp run azdo-tests`)
- **`azure-pipelines-unofficial.yml`** - Unofficial/experimental builds
- **`azure-pipelines-codeql.yml`** - CodeQL security analysis

### Template Files

- **`templates/public-pipeline-template.yml`** - Shared template for public pipelines
- **`templates/BuildAndTest.yml`** - Build and test execution template
- **`templates/build_sign_native.yml`** - Native build and signing template
- **`templates/polyglot-codeql.yml`** - Go/Java starter and generated SDK CodeQL extraction
- **`templates/send-to-helix.yml`** - Helix test execution template

### Configuration

- **`common-variables.yml`** - Shared variables across pipelines

## Manual Pipeline Usage

The `azdo-tests.yml` pipeline can be triggered manually using Azure DevOps comment commands:

```yml
/azp run azdo-tests
```

This pipeline:
- Only runs on manual triggers (no automatic builds)
- Always executes both pipeline tests and Helix tests
- Uses the same build and test logic as the public pipeline
- Useful for testing changes before they go through the normal CI process

## Template Structure

The public pipelines (`azure-pipelines-public.yml` and `azdo-tests.yml`) use a shared template (`templates/public-pipeline-template.yml`) to avoid code duplication while maintaining the same functionality.

## Go and Java CodeQL snapshots

The official pipeline's `polyglot_codeql` stage runs only on non-PR internal builds
of `main`, after `build` and `build_sign_native` succeed. Its Go and Java jobs run
in parallel with each other and with Assemble; neither needs published packages.
They download `managed_packages_shipping` and the Windows CLI from
`native_archives_win_x64` from the current run.

Each job uses a local package channel pinned to the same-build AppHost package
version, scaffolds its starter, and compiles both the AppHost/generated SDK and
the API without starting any application or container. Sources remain under
`artifacts/codeql/<language>/starter` for CodeQL finalization. CLI state and
dependency caches are isolated per job.
Official `main` builds enable the 1ES internal Go module proxy so Go dependencies
are restored through the supported credential provider rather than public hosts.
Java's Gradle init script routes plugin and Maven dependencies through the
existing `dotnet-public-maven` feed, using the job token for upstream access
without weakening the pipeline's network isolation policy or editing the starter.

1ES injects CodeQL Initialize and Finalize, with each job restricted to its own
language. The default scan cadence still applies. Check Finalize for a database
upload and the linked CAP Central job for successful analysis; a green pipeline
or TSA upload alone is not evidence of a successful language snapshot. Continuous
SDL requires successful analysis within 30 days per detected language, and S360
can take 48 hours to reflect completed analysis. See the
[1ES CodeQL guidance](https://aka.ms/codeql3000-1espt) and
[snapshot KPI documentation](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/codeql/troubleshooting/s360/onboarding-kpis).

Personal-branch validation skips this stage. Its upload path must be validated
explicitly before treating the first official `main` run as compliant.
