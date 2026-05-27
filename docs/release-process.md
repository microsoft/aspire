# Aspire Release Process

This document describes the release process for microsoft/aspire, including both the automated workflows and manual steps required by the release manager.

## Overview

The Aspire release process involves two main automation components:

1. **Azure DevOps Pipeline** ([`release-publish-nuget`](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary), source: `eng/pipelines/release-publish-nuget.yml`)
   - Publishes NuGet packages to NuGet.org
   - Promotes the build to the GA channel via darc
   - Submits WinGet manifest PRs
   - Validates the Homebrew cask against the live GitHub release
     (cask version bumps themselves are submitted by upstream autobump —
     see [Installer channels](#installer-channels))
   - Dispatches the GitHub Actions workflow below as the `aspire-repo-bot`
     GitHub App and waits for it to complete

2. **GitHub Actions Workflow** (`.github/workflows/release-github-tasks.yml`)
   - Creates Git tags
   - Creates GitHub Releases
   - Creates merge-back PRs
   - Creates baseline version update PRs
   - Normally dispatched automatically by the AzDO pipeline above; can also
     be run manually as a fallback for partial-failure re-runs

## Installer channels

Aspire ships through several channels. The release pipeline either submits the bump itself or validates a bump submitted upstream; the per-channel docs describe manifest shape, validation modes, and dogfooding workflows in detail.

| Channel | Who submits the version bump | Per-channel docs |
|---------|------------------------------|------------------|
| **NuGet** (libraries, AppHost SDK, `Aspire.Cli` tool packages) | `release-publish-nuget` pushes to NuGet.org | This document |
| **WinGet** (`winget install Microsoft.Aspire`) | `release-publish-nuget` submits manifest PRs to `microsoft/winget-pkgs` via `wingetcreate` | [`eng/winget/README.md`](../eng/winget/README.md) |
| **Homebrew cask** (`brew install --cask aspire`) | Upstream Homebrew/homebrew-cask's [autobump workflow](https://github.com/Homebrew/homebrew-cask/blob/master/.github/workflows/autobump.yml) opens the bump PR on a 3-hour schedule, detecting the new version via the cask's `livecheck` block. `release-publish-nuget` only validates the cask against the live GitHub release after asset upload. | [`eng/homebrew/README.md`](../eng/homebrew/README.md) |
| **`dotnet tool install -g Aspire.Cli`** | `release-publish-nuget` pushes the per-RID `Aspire.Cli.*.nupkg` packages to NuGet.org alongside the libraries | [`docs/specs/install-routes.md`](specs/install-routes.md) |
| **Install script** (`get-aspire-cli.sh` / `.ps1`) | No separate publication — the script downloads directly from the GitHub release assets attached in Step 1 | [`docs/specs/install-routes.md`](specs/install-routes.md), `eng/scripts/get-aspire-cli.*` |

The CLI identifies which channel installed it via a per-install sidecar so that self-update can route back through the same channel. See [`docs/specs/install-routes.md`](specs/install-routes.md).

## Prerequisites

Before starting a release:

1. **Signed Build**: Have a successful signed build from the official [`microsoft-aspire`](https://dev.azure.com/dnceng/internal/_build?definitionId=1602) pipeline
   - The build will be selected from a dropdown when running the release pipeline
   - The build should have a `BAR ID - NNNNNN` tag (auto-extracted by the pipeline)

2. **Release Branch**: Ensure the release branch exists (e.g., `release/9.2`)

3. **Permissions**:
   - Access to run Azure DevOps pipelines with the publishing pool
   - GitHub write access for creating tags/releases/PRs (only required for manual GH workflow runs)

4. **AzDO secrets** (already configured for chained dispatch):
   - `aspire-bot-app-id` — `aspire-repo-bot` GitHub App ID
   - `aspire-bot-private-key` — `aspire-repo-bot` GitHub App PEM private key

   Both live in the **`Aspire-Release-Secrets`** variable group (AzDO →
   Pipelines → Library) and are marked as secret. To rotate the private key:
   generate a new one from the App settings page
   (github.com/organizations/dotnet/settings/apps/aspire-repo-bot →
   "Private keys" → "Generate a private key"), paste the full PEM (including
   the `-----BEGIN/END-----` lines) into the `aspire-bot-private-key`
   variable, save, then revoke the old key from the same App settings page.
   The App ID does not change on rotation.

## Step-by-Step Release Process

### Step 1: Run the AzDO release pipeline (one click for everything)

1. Navigate to the Azure DevOps pipeline:
   [release-publish-nuget](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary)
   (definition `1600` in `dnceng/internal`)
2. Click "Run pipeline"
3. Fill in the parameters. **Most should stay at their defaults** — the
   ones flagged `[Advanced]` in the run-pipeline form are only for
   re-running after a partial failure or for testing pipeline changes on a
   topic branch.

   **Common (you may set these every release):**

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `ReleaseVersion` | Override for the version label (used as `v<version>` tag). **Leave as `auto` to derive from the source build's `release-version - *` tag** — the normal case. Only set this when re-shipping under a corrected tag. | `auto` |
   | `IsPrerelease` | `true` for preview releases | `false` |
   | `DryRun` | Set `true` to test without publishing or tagging | `false` |
   | `GaChannelName` | Target GA channel | `Aspire 9.x GA` |

   **Advanced (leave defaults unless you know what you're doing):**

   | Parameter | Description | Default |
   |-----------|-------------|---------|
   | `SkipNuGetPublish` | Set `true` if re-running after NuGet success | `false` |
   | `SkipChannelPromotion` | Set `true` if re-running after darc success | `false` |
   | `SkipWinGetPublish` | Set `true` if re-running after WinGet success | `false` |
   | `SkipGitHubTasks` | Set `true` to skip dispatching the GH workflow | `false` |
   | `SkipReleaseAssets` | Set `true` to skip uploading aspire-cli-* assets to the GitHub release | `false` |
   | `SkipHomebrewValidation` | Set `true` if re-running after a successful Homebrew cask validation (validates against the live GH release) | `false` |
   | `GitHubTasksWorkflowRef` | Ref to load `release-github-tasks.yml` from when dispatching. Only affects the workflow source — the release branch/commit are passed via inputs. Override only when testing pipeline changes on a topic branch. | `main` |
   
4. Select the **Resources** button in the bottom right, then select the source build from the `aspire-build` dropdown
   - The picker shows all recent builds from the `microsoft-aspire`
     pipeline regardless of branch. Pick the build that corresponds to the
     release branch and version you intend to ship.
   - Each build's tags are shown alongside its number — verify the
     `release-version - X.Y.Z` tag matches the version you intend to ship
     **before** clicking Run. If the tag is missing, either re-run the
     source build (after the tag-emitting change in `azure-pipelines.yml`
     is on that release branch) or pass an explicit `ReleaseVersion`
     override below.
5. Click "Run" and monitor the pipeline. The final stage (`GitHubTasks`)
   dispatches `release-github-tasks.yml`, waits for it to complete, and
   then uploads the `aspire-cli-*` archives from the source build's
   `BlobArtifacts` onto the newly-created GitHub release — the AzDO
   pipeline only succeeds if both pieces succeed.
6. Verify packages appear on NuGet.org and that the `aspire-cli-*`
   archives are attached to the GitHub release.

`commit_sha` and `release_branch` for the GitHub workflow are derived
automatically from the source build resource — no need to copy them by hand.

> **Tip**: Use `DryRun: true` to test end-to-end without publishing,
> promoting, tagging, or creating PRs. The dry-run state is propagated to
> the GitHub workflow as `dry_run: true`.

### Step 2 (fallback): Manually re-run the GitHub workflow

The GitHub workflow is normally dispatched by the AzDO pipeline as the
`aspire-repo-bot` GitHub App, with its `authorize` job bypassed for the
bot. If a GitHub-side step fails partway through and you need to re-run
only the GitHub work, you can:

1. Re-run the AzDO pipeline with `SkipNuGetPublish`, `SkipChannelPromotion`,
   `SkipWinGetPublish` all set to `true` (and the appropriate other skips),
   keeping `SkipGitHubTasks: false`. The
   `GitHubTasks` stage will dispatch the workflow again with the right
   inputs, and the workflow's own `skip_*` idempotency makes the
   completed steps no-ops.
2. Or, navigate to Actions → "Release GitHub Tasks", click "Run workflow",
   and fill in the parameters manually:

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `release_version` | The version being released | `13.0.0` |
   | `commit_sha` | Full 40-char commit SHA from the build | `abc123...` |
   | `release_branch` | Release branch name | `release/9.2` |
   | `is_prerelease` | `true` for preview releases | `false` |
   | `dry_run` | `true` to validate without making changes | `false` |
   | `skip_tagging` | Skip if tag already created | `false` |
   | `skip_github_release` | Skip if release already exists | `false` |
   | `skip_merge_pr` | Skip if PR already created | `false` |
   | `skip_baseline_pr` | Skip if PR already created | `false` |

   Manual runs go through the normal `authorize` check (admin/maintain
   permission required).

### Step 3: Post-Release Tasks (Manual)

After automation completes:

1. **Review and merge automatically created PRs**:
   - Merge-back PR: `$RELEASE_BRANCH` → `main`
   - Baseline version PR: Updates `PackageValidationBaselineVersion`

2. **Verify the release**:
   - Check the [GitHub Releases page](https://github.com/microsoft/aspire/releases)
   - Verify packages on [NuGet.org](https://www.nuget.org/packages?q=owner%3Aaspire)
   - Test installation: `dotnet new install Aspire.ProjectTemplates::VERSION` and `aspire update --self`

3. **Communicate**:
   - Update any tracking issues
   - Notify stakeholders

## Handling Failures

Both automations are designed to be **idempotent** and safe to re-run.

### Azure DevOps Pipeline Failures

The pipeline runs as a single stage with all steps in sequence. If a step fails:

| Step Failed | Resolution |
|-------------|------------|
| Validate Parameters | Fix the input parameters and re-run |
| Extract BAR Build ID | Check that the build has a `BAR ID - NNNNNN` tag |
| List/Verify Packages | Check that the build artifacts are available |
| Push Packages to NuGet.org | Check NuGet.org for partial success; the `1ES.PublishNuget@1` task handles duplicates via `allowPackageConflicts: true` |
| Promote Build to Channel | Re-run with `SkipNuGetPublish: true` |

### GitHub Actions Failures

The GitHub workflow runs as a single `release` job with all tasks as
sequential steps. The previous design split these across six jobs and paid
the runner-provisioning tax on each; consolidation cuts that to one. If a
step fails, drill into the run UI to see exactly which step (tag, release,
merge PR, baseline PR) hit the issue.

Re-run with the corresponding `skip_*` input set to `true` to skip steps
that have already succeeded. The skip inputs are still passed step-by-step
so partial-failure re-runs behave the same way they did before
consolidation.

| Step Failed | Resolution |
|-------------|------------|
| Authorize | Caller lacks admin/maintain permission (or AzDO bot identity check failed) |
| Validate Version Format / Commit SHA | Fix the input parameters and re-run |
| Create Tag | If tag exists with wrong SHA, requires manual resolution |
| Create GitHub Release | Re-run with `skip_tagging: true` |
| Create Merge PR | Re-run with `skip_tagging: true`, `skip_github_release: true` |
| Create Baseline PR | Re-run with all prior skips set to `true` |

## Configuration

### 1ES Pipeline Compliance

The AzDO pipeline extends the 1ES Official Pipeline Templates (`v1/1ES.Official.PipelineTemplate.yml@1ESPipelineTemplates`) to be compliant with Microsoft organization requirements. This provides:
- SDL (Security Development Lifecycle) compliance scanning
- Proper pool configuration for internal pipelines
- Component governance integration
- **Secure NuGet publishing** via the `1ES.PublishNuget@1` task with managed service connections

> **Note**: This pipeline does not use the MicroBuild template since we're not signing packages - packages are already signed during the main build pipeline. We only download and publish pre-signed artifacts.

### Variable Groups (Azure DevOps)

The pipeline uses the `Aspire-Release-Secrets` variable group. Note that NuGet publishing credentials are managed via a service connection, not a variable group secret.

### Service Connections (Azure DevOps)

| Connection Name | Purpose |
|-----------------|---------|
| `NuGet.org - microsoft/aspire` | NuGet service connection for publishing packages to NuGet.org |
| `Darc: Maestro Production` | Used for darc channel promotion |

> **Note**: The `NuGet.org - microsoft/aspire` service connection must be configured in Azure DevOps Project Settings → Service connections with:
> - **Type**: NuGet
> - **Authentication**: ApiKey
> - **Feed URL**: `https://api.nuget.org/v3/index.json`
> - **ApiKey**: A scoped NuGet.org API key with push permissions for Aspire packages

### Approved GitHub Actions

The workflow uses only pre-approved actions:

- `actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683` (v4)
- `./.github/actions/create-pull-request` (local composite action)

## Troubleshooting

### "Could not find BAR ID tag"

The pipeline expects the build to have a tag in format `BAR ID - NNNNNN`. This is normally added automatically by the Maestro publishing process. If missing:

1. Check if the build completed its post-build steps
2. Manually look up the BAR ID in Maestro and add the tag
3. Contact the engineering team if the issue persists

### "Tag already exists but points to different commit"

This indicates a mismatch between the expected release commit and an existing tag. Resolution:

1. Verify you're using the correct commit SHA
2. If the existing tag is wrong, it must be manually deleted (requires admin)
3. If the SHA is wrong, correct it and re-run

### NuGet publish failures

The `1ES.PublishNuget@1` task is configured with `allowPackageConflicts: true`, which means it will skip packages that already exist on NuGet.org. If publishing fails:

1. Check the pipeline logs for specific error messages
2. Verify the service connection `NuGet.org - microsoft/aspire` is properly configured
3. Ensure the API key in the service connection has push permissions for the package IDs
4. Re-run the pipeline (it will skip already-published packages)

### PR creation fails

The workflow checks for existing PRs before creating. If a PR exists with a different title:

1. Close or merge the existing PR
2. Re-run the workflow

## Architecture Diagram

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                         RELEASE PROCESS FLOW                            │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │                    Azure DevOps Pipeline                         │   │
│  │                release-publish-nuget.yml                         │   │
│  │          (1ES.Official.PipelineTemplate.yml)                     │   │
│  │                                                                  │   │
│  │  Resource: aspire-build (select from dropdown)                   │   │
│  │  Input: GaChannelName                                            │   │
│  │                                                                  │   │
│  │  ┌─────────────┐   ┌──────────────┐   ┌──────────────────────┐   │   │
│  │  │  Validate   │──▶│ Extract BAR  │──▶│  Download & Verify   │   │   │
│  │  │   Inputs    │   │   Build ID   │   │     Packages         │   │   │
│  │  └─────────────┘   └──────────────┘   └──────────────────────┘   │   │
│  │                                                    │             │   │
│  │                                                    ▼             │   │
│  │                                       ┌──────────────────────┐   │   │
│  │                                       │  1ES.PublishNuget@1  │   │   │
│  │                                       │  (via svc connection)│   │   │
│  │                                       └──────────────────────┘   │   │
│  │                                                    │             │   │
│  │                                                    ▼             │   │
│  │                                       ┌──────────────────────┐   │   │
│  │                                       │  Promote to Channel  │   │   │
│  │                                       │     (via darc)       │   │   │
│  │                                       └──────────────────────┘   │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                    │                                    │
│                                    ▼                                    │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │                    GitHub Actions Workflow                       │   │
│  │              .github/workflows/release-github-tasks.yml          │   │
│  │                                                                  │   │
│  │  Input: release_version, commit_sha, release_branch              │   │
│  │                                                                  │   │
│  │  ┌─────────────┐   ┌──────────────┐   ┌──────────────────────┐   │   │
│  │  │  Validate   │──▶│ Create Tag   │──▶│  Create GitHub       │   │   │
│  │  │   Inputs    │   │  v{version}  │   │    Release           │   │   │
│  │  └─────────────┘   └──────────────┘   └──────────────────────┘   │   │
│  │                                                    │             │   │
│  │                          ┌─────────────────────────┼─────────┐   │   │
│  │                          ▼                         ▼         │   │   │
│  │              ┌───────────────────────┐  ┌────────────────────┐   │   │
│  │              │   Create Merge PR     │  │ Create Baseline PR │   │   │
│  │              │ release/X.Y → main    │  │ Update version     │   │   │
│  │              └───────────────────────┘  └────────────────────┘   │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## Related Documentation

- [Contributing Guide](contributing.md)
- [Quarantined Tests](quarantined-tests.md)
- [Install routes & sidecars](specs/install-routes.md) — how the CLI identifies its install channel
- [WinGet README](../eng/winget/README.md) — manifest layout, prepare/publish, dogfooding
- [Homebrew README](../eng/homebrew/README.md) — cask layout, livecheck/autobump, validation modes
