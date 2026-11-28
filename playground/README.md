# Integrations

This folder contains Aspire applications used within this repository for testing.

These are not intended as samples! In order to develop in this repository, we need some non-standard approaches that are not recommended or might not work elsewhere.

To find samples, see https://github.com/microsoft/aspire-samples

## Refresh playground manifests

Run `pwsh -File eng/refreshManifests.ps1` from the repository root. The script builds the
repository and runs the `generate-manifest` profile for each `*AppHost.csproj` that defines one.
Publishing directly from the build outputs does not start the orchestrator or require installing
a released Aspire CLI.

If the build or a manifest command fails, the script stops before the workflow can publish a
partial update.
