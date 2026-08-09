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
