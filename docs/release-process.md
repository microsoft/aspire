# Aspire Release Process

This document describes the release process for microsoft/aspire, including both the automated workflows and manual steps required by the release manager.

## Overview

The Aspire release process uses two main automation components:

1. **Azure DevOps pipeline** ([`release-publish-nuget`](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary), source: `eng/pipelines/release-publish-nuget.yml`)
   - Downloads signed artifacts from a selected official source build.
   - Re-publishes NuGet, npm, and WinGet release inputs so 1ES can generate release SBOMs.
   - Publishes NuGet packages to NuGet.org.
   - Publishes Aspire CLI npm packages through ESRP/MicroBuild.
   - Promotes the build to the GA channel via darc.
   - Submits WinGet manifest PRs.
   - Validates the Homebrew cask against the live GitHub release (cask version bumps themselves are submitted by upstream autobump; see [Installer channels](#installer-channels)).
   - Optionally publishes the signed VS Code extension to the Visual Studio Marketplace.
   - Dispatches the GitHub Actions workflow below as the `aspire-repo-bot` GitHub App and waits for it to complete.
   - Uploads `aspire-cli-*` archives from the source build's `BlobArtifacts` onto the GitHub Release as the `aspire-repo-bot`.
2. **GitHub Actions workflow** (`.github/workflows/release-github-tasks.yml`)
   - Creates Git tags.
   - Creates GitHub Releases.
   - Creates merge-back PRs.
   - Creates baseline version update PRs.
   - Normally dispatched automatically by the AzDO pipeline above; it can also be run manually as a fallback for partial-failure re-runs.
3. **GitHub Actions workflow** (`.github/workflows/extension-release.yml`)
   - Prepares a VS Code extension release PR.
   - Bumps `extension/package.json`.
   - Generates or updates `extension/CHANGELOG.md`.
   - Defaults its comparison baseline to the latest stable Marketplace VSIX's embedded `extension/.version` commit SHA.
   - Can run with `dry_run: true` in forks to validate changelog generation without bot secrets.

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

1. **Signed build**: Have a successful signed build from the official [`microsoft-aspire`](https://dev.azure.com/dnceng/internal/_build?definitionId=1602) pipeline.
   - Select this build from the `aspire-build` resource dropdown when running the release pipeline.
   - The build should have a `BAR ID - NNNNNN` tag, which the pipeline extracts automatically.
   - The build should also have a `release-version - X.Y.Z` tag, which the pipeline uses when `ReleaseVersion` is left as `auto`.
   - The build must include native CLI NuGet packages, `microsoft-aspire-cli*.tgz` npm tarballs from the native archive jobs, matching `.tgz.sig` detached signature sidecars, and the Windows, Linux, and macOS npm install validation summaries.
   - If publishing the VS Code extension, the build must include the `aspire-vscode-extension` artifact with exactly one `.vsix`, matching `.manifest`, and matching `.signature.p7s`.
   - If publishing the VS Code extension as a Marketplace pre-release, the build that runs automatically on merge will not work because it packages a stable VSIX; manually queue the `microsoft-aspire` source build on the merge commit with `Package VS Code Extension as Pre-Release=true` so the produced VSIX is marked as pre-release before signing.
2. **Release branch**: Ensure the release branch exists, for example `release/9.2`.
3. **Permissions and approvals**:
   - Access to run Azure DevOps pipelines with the publishing pool.
   - Permission to use the NuGet.org service connection.
   - Approval to use the DevDiv ESRP service connection for MicroBuild npm publishing.
   - Valid ESRP owner and approver aliases for npm publishing.
   - GitHub write access for creating tags, releases, and PRs if you need to run the GitHub workflow manually.
4. **AzDO secrets** (already configured for chained dispatch):
   - `aspire-bot-app-id` — `aspire-repo-bot` GitHub App ID.
   - `aspire-bot-private-key` — `aspire-repo-bot` GitHub App PEM private key.

   Both live in the **`Aspire-Release-Secrets`** variable group (AzDO → Pipelines → Library) and are marked as secret. To rotate the private key, generate a new one from the App settings page (github.com/organizations/dotnet/settings/apps/aspire-repo-bot → "Private keys" → "Generate a private key"), paste the full PEM (including the `-----BEGIN/END-----` lines) into the `aspire-bot-private-key` variable, save, then revoke the old key from the same App settings page. The App ID does not change on rotation.

## Step-by-step release process

### Step 1: Run the AzDO release pipeline

1. Navigate to the Azure DevOps pipeline: [release-publish-nuget](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary) (definition `1600` in `dnceng/internal`).
2. Click **Run pipeline**.
3. Fill in the parameters. Most should stay at their defaults; the ones flagged `[Advanced]` in the run-pipeline form are only for re-running after a partial failure or for testing pipeline changes on a topic branch.

   **Common (you may set these every release):**

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `ReleaseVersion` | Override for the version label (used as `v<version>` tag). Leave as `auto` to derive from the source build's `release-version - *` tag, which is the normal case. Only set this when re-shipping under a corrected tag. | `auto` |
   | `IsPrerelease` | `true` for preview releases. Non-dry-run npm publishing is blocked for prereleases until the MicroBuild npm publish path supports non-`latest` dist-tags. | `false` |
   | `DryRun` | Set `true` to test without publishing, promoting, tagging, or creating PRs. | `false` |
   | `GaChannelName` | Target GA channel. | `Aspire 9.x GA` |

   **Advanced (leave defaults unless you know what you're doing):**

   | Parameter | Description | Default |
   |-----------|-------------|---------|
   | `SkipNuGetPublish` | Set `true` if re-running after NuGet success. | `false` |
   | `SkipNpmPublish` | Set `true` if re-running after all npm packages are published. | `false` |
   | `SkipNpmRidPublish` | Set `true` if npm RID packages published but the pointer package did not. | `false` |
   | `SkipNpmPointerPublish` | Set `true` if the pointer package published but a later validation or promotion step failed. Registry validation still runs. | `false` |
   | `SkipChannelPromotion` | Set `true` if re-running after darc success. | `false` |
   | `SkipWinGetPublish` | Set `true` if re-running after WinGet success. | `true` |
   | `SkipGitHubTasks` | Set `true` to skip dispatching the GH workflow. | `false` |
   | `SkipReleaseAssets` | Set `true` to skip uploading `aspire-cli-*` assets to the GitHub release. | `false` |
   | `SkipHomebrewValidation` | Set `true` if re-running after a successful Homebrew cask validation against the live GitHub release. | `false` |
   | `SkipVSCodeExtensionPublish` | Set `false` to publish the signed `aspire-vscode-extension` artifact to the Visual Studio Marketplace. | `true` |
   | `NpmPublishOwners` | Optional comma-separated ESRP owner aliases or emails. Leave empty for the repo default; overrides must still include the required owner aliases from `eng/pipelines/common-variables.yml`. | empty |
   | `NpmPublishApprovers` | Optional comma-separated ESRP approver aliases or emails. Leave empty for the repo default; overrides must still include the required approver aliases from `eng/pipelines/common-variables.yml` and must not overlap owners. | empty |
   | `NpmRegistryPropagationDelayMinutes` | Delay between npm RID package and pointer package submissions. | `10` |
   | `AllowNpmLatestDistTagMove` | Emergency override for intentionally moving npm `latest` to an older stable version. Older servicing releases should normally use `SkipNpmPublish=true`. | `false` |
   | `GitHubTasksWorkflowRef` | Ref to load `release-github-tasks.yml` from when dispatching. Only affects the workflow source; the release branch and commit are passed via inputs. Override only when testing pipeline changes on a topic branch. | `main` |

4. Select the **Resources** button in the bottom right, then select the source build from the `aspire-build` dropdown.
   - The picker shows all recent builds from the `microsoft-aspire` pipeline regardless of branch. Pick the build that corresponds to the release branch and version you intend to ship.
   - Each build's tags are shown alongside its number. Verify the `release-version - X.Y.Z` tag matches the version you intend to ship before clicking **Run**. If the tag is missing, either re-run the source build after the tag-emitting change in `azure-pipelines.yml` is on that release branch or pass an explicit `ReleaseVersion` override.
5. Click **Run** and monitor the pipeline. The final stage (`GitHubTasks`) dispatches `release-github-tasks.yml`, waits for it to complete, uploads the `aspire-cli-*` archives from the source build's `BlobArtifacts` onto the newly-created GitHub release, and validates the Homebrew cask against that live release. The AzDO pipeline only succeeds if the enabled GitHub tasks, asset upload, and Homebrew validation succeed.
6. Verify packages appear on NuGet.org and npm, and verify that the `aspire-cli-*` archives are attached to the GitHub release.

To publish only the VS Code extension after merging an extension release PR, run the same `release-publish-nuget` pipeline, select the signed source build from that merge, and set:

| Parameter | Value |
|-----------|-------|
| `ReleaseVersion` | `auto` |
| `IsPrerelease` | `false` for stable, `true` for pre-release |
| `DryRun` | `false` |
| `SkipNuGetPublish` | `true` |
| `SkipNpmPublish` | `true` |
| `SkipNpmRidPublish` | `true` |
| `SkipNpmPointerPublish` | `true` |
| `SkipChannelPromotion` | `true` |
| `SkipWinGetPublish` | `true` |
| `SkipHomebrewValidation` | `true` |
| `SkipGitHubTasks` | `true` |
| `SkipReleaseAssets` | `true` |
| `SkipVSCodeExtensionPublish` | `false` |

> **Stable vs. pre-release source build:** For a stable release (`IsPrerelease=false`), use the `microsoft-aspire` build that ran automatically on merge. For a pre-release (`IsPrerelease=true`), that automatic build is stable-only and cannot be used — manually queue the `microsoft-aspire` pipeline on the merge commit with `Package VS Code Extension as Pre-Release=true`, wait for it to finish, and select that build instead. The publish job fails if the VSIX's embedded pre-release flag does not match `IsPrerelease`.

For a full Aspire release that should also publish the extension, keep the normal NuGet/channel/GitHub task settings and set `SkipVSCodeExtensionPublish` to `false`. `IsPrerelease` also controls whether extension publishing passes `--pre-release` to `vsce`; for a pre-release extension, the selected source build must also have been queued with `Package VS Code Extension as Pre-Release=true`.

The npm release path validates Windows, Linux, and macOS install summaries, publishes the seven RID packages first, waits for ESRP completion, waits for the configured propagation delay, and then publishes the top-level `@microsoft/aspire-cli` pointer package. After the pointer package publishes, the pipeline installs it from the live npm registry and runs `aspire --version` before channel promotion. This avoids installing a pointer package whose optional RID dependencies are not visible yet and catches registry propagation issues before the release is promoted. For prereleases, set `SkipNpmPublish=true` unless the npm publishing path has gained explicit non-`latest` dist-tag support.

`commit_sha` and `release_branch` for the GitHub workflow are derived automatically from the source build resource, so there is no need to copy them by hand.

> **Tip**: Use `DryRun: true` to test end-to-end without publishing, promoting, tagging, creating PRs, or uploading release assets. The dry-run state is propagated to the GitHub workflow as `dry_run: true`.

### Step 2: Prepare a VS Code extension release PR

Run this step only when releasing the VS Code extension independently of the normal Aspire release train, or when the extension changelog/version bump needs to be prepared before setting `SkipVSCodeExtensionPublish=false` in the AzDO release pipeline.

1. Navigate to Actions → "Extension Release".
2. Click "Run workflow".
3. Fill in the parameters:

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `release_version` | New VS Code extension version | `1.0.10` |
   | `from_sha` | Optional start commit SHA. Leave empty to use the latest stable Marketplace VSIX's bundled `extension/.version` SHA. Pass an explicit SHA for non-stable or historical baselines. | empty |
   | `to_sha` | Optional end commit SHA. Leave empty to use the latest `main` commit. | empty |
   | `dry_run` | `true` to validate and upload the proposed changelog/PR body artifact without creating a branch or PR. This can be used from forks because it does not require bot secrets. | `true` |

4. Review the generated draft PR. Its description contains the exact `release-publish-nuget` parameter set for publishing the extension after the PR is merged.

### Step 3 (fallback): Manually re-run the GitHub workflow

The GitHub workflow is normally dispatched by the AzDO pipeline as the `aspire-repo-bot` GitHub App, with its `authorize` job bypassed for the bot. If a GitHub-side step fails partway through and you need to re-run only the GitHub work, you can:

1. Re-run the AzDO pipeline with completed AzDO-side work skipped, such as `SkipNuGetPublish`, `SkipNpmPublish`, `SkipNpmRidPublish`, `SkipChannelPromotion`, `SkipWinGetPublish`, `SkipHomebrewValidation`, and `SkipReleaseAssets` set as appropriate, keeping `SkipGitHubTasks: false`. The `GitHubTasks` stage will dispatch the workflow again with the right inputs, and the workflow's own `skip_*` idempotency makes the completed steps no-ops.
2. Or, navigate to Actions → **Release GitHub Tasks**, click **Run workflow**, and fill in the parameters manually:

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `release_version` | The version being released. | `13.0.0` |
   | `commit_sha` | Full 40-character commit SHA from the build. | `abc123...` |
   | `release_branch` | Release branch name. | `release/9.2` |
   | `is_prerelease` | `true` for preview releases. | `false` |
   | `dry_run` | `true` to validate without making changes. | `false` |
   | `skip_tagging` | Skip if tag already created. | `false` |
   | `skip_github_release` | Skip if release already exists. | `false` |
   | `skip_merge_pr` | Skip if PR already created. | `false` |
   | `skip_baseline_pr` | Skip if PR already created. | `false` |

Manual runs go through the normal `authorize` check (admin/maintain permission required).

### Step 4: Post-Release Tasks (Manual)

After automation completes:

1. **Review and merge automatically created PRs**:
   - Merge-back PR: `$RELEASE_BRANCH` → `main`.
   - Baseline version PR: updates `PackageValidationBaselineVersion`.
2. **Verify the release**:
   - Check the [GitHub Releases page](https://github.com/microsoft/aspire/releases).
   - Verify packages on [NuGet.org](https://www.nuget.org/packages?q=Aspire).
   - Verify npm packages on the Microsoft npm profile.
   - Test installation: `dotnet new install Aspire.ProjectTemplates::VERSION` and `aspire update --self`.
3. **Communicate**:
   - Update any tracking issues.
   - Notify stakeholders.

## Handling failures

Both automations are designed to be idempotent and safe to re-run.

### Azure DevOps pipeline failures

| Step failed | Resolution |
|-------------|------------|
| Validate Parameters | Fix the input parameters and re-run. |
| Derive ReleaseVersion | Check that the build has a `release-version - X.Y.Z` tag, or pass `ReleaseVersion` explicitly. |
| Extract BAR Build ID | Check that the build has a `BAR ID - NNNNNN` tag. |
| Prepare/List/Verify NuGet Packages | Check that the selected source build produced `PackageArtifacts`. |
| Prepare/List npm Packages | Check that the selected source build produced all eight `microsoft-aspire-cli*.tgz` tarballs and matching `.tgz.sig` sidecars in `BlobArtifacts`. |
| Push Packages to NuGet.org | Check NuGet.org for partial success, then re-run with already-completed steps skipped as needed. |
| MicroBuild npm Publish | Check the ESRP release result. If RID packages published but the pointer package did not, re-run with `SkipNuGetPublish: true`, `SkipNpmRidPublish: true`, and `SkipChannelPromotion: true`; do not set `SkipNpmPublish` until the pointer package is published. |
| Validate Published npm Package from Registry | Confirm the pointer package is visible on npm and that `npm install -g @microsoft/aspire-cli@<version>` works. If registry propagation is slow, re-run with completed publish steps skipped after the package is visible. |
| Promote Build to Channel | Re-run with completed publish steps skipped. |
| WinGet publishing / Homebrew validation | Re-run with the corresponding skip flags for completed work. |
| Publish VS Code Extension to Marketplace | Check that `aspire-vscode-extension` contains one `.vsix`, `.manifest`, and `.signature.p7s`; verify `VscePublishToken` in `Aspire-Release-Secrets` has Marketplace: Manage scope; re-run with the already-completed `Skip*` flags set to `true`. |
| GitHubTasks dispatch | Re-run with completed AzDO-side work skipped and `SkipGitHubTasks: false`; set `SkipReleaseAssets` according to whether release asset upload already completed. |
| Release asset upload | Re-run with `SkipGitHubTasks: true` and `SkipReleaseAssets: false` after the GitHub release exists. |

### GitHub Actions failures

The GitHub workflow runs as a single `release` job with all tasks as sequential steps. If a step fails, drill into the run UI to see exactly which step (tag, release, merge PR, baseline PR) hit the issue.

Re-run with the corresponding `skip_*` input set to `true` to skip steps that have already succeeded. The skip inputs are still passed step-by-step so partial-failure re-runs behave the same way they did before consolidation.

| Step failed | Resolution |
|-------------|------------|
| Authorize | Caller lacks admin/maintain permission, or the AzDO bot identity check failed. |
| Validate Version Format / Commit SHA | Fix the input parameters and re-run. |
| Create Tag | If the tag exists with the wrong SHA, resolve it manually. |
| Create GitHub Release | Re-run with `skip_tagging: true`. |
| Create Merge PR | Re-run with `skip_tagging: true` and `skip_github_release: true`. |
| Create Baseline PR | Re-run with all prior skips set to `true`. |

## Configuration

### 1ES and MicroBuild compliance

The AzDO pipeline extends the MicroBuild publish-enabled 1ES template (`azure-pipelines/1ES.Official.Publish.yml@MicroBuildTemplate`) to be compliant with Microsoft organization requirements and to grant `MicroBuild.Publish.yml@MicroBuildTemplate` access to the DevDiv ESRP service connection for npm submissions. The source build creates, signs where platform signing applies, verifies, and stages the package artifacts; the release pipeline consumes those pre-built artifacts and does not rebuild or re-pack the CLI.

### Variable groups

The pipeline uses:

| Variable group | Purpose |
|----------------|---------|
| `Aspire-Release-Secrets` | Release pipeline secrets, including the `aspire-repo-bot` GitHub App credentials. NuGet publishing uses a service connection rather than a variable-group API key. `VscePublishToken` in this group is used only by the VS Code extension Marketplace publish job; rotate it from the Visual Studio Marketplace publisher management UI when `vsce verify-pat microsoft-aspire` fails or before the PAT expires. |
| `Aspire-Secrets` | WinGet bot token. |

### Service connections

| Connection name | Purpose |
|-----------------|---------|
| `NuGet.org - dotnet/aspire` | NuGet service connection for publishing packages to NuGet.org. |
| `DevDivEsrpAzDoSrvConn` | ESRP service connection used by the MicroBuild publish template for npm publishing. |
| `Darc: Maestro Production` | Used for darc channel promotion. |

The release definition must be approved for the 1ES and MicroBuild publishing templates and must have permission to use `DevDivEsrpAzDoSrvConn`.

### Approved GitHub Actions

The workflow uses only pre-approved actions:

- `actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683` (v4).
- `./.github/actions/create-pull-request` (local composite action).

## Troubleshooting

### Could not find BAR ID tag

The pipeline expects the build to have a tag in format `BAR ID - NNNNNN`. This is normally added automatically by the Maestro publishing process. If missing:

1. Check if the build completed its post-build steps.
2. Manually look up the BAR ID in Maestro and add the tag.
3. Contact the engineering team if the issue persists.

### npm tarballs are missing from release artifacts

The official source build should fail staging if npm tarballs are missing. If the release pipeline cannot find them:

1. Confirm the selected source build is from a branch that includes npm packaging.
2. Check the native archive jobs for `verify-cli-npm-package.ps1` failures.
3. Check `BlobArtifacts` for the eight `microsoft-aspire-cli*.tgz` files.

### npm publish fails after RID packages published

If ESRP published the RID packages but failed before publishing `@microsoft/aspire-cli`:

1. Verify the RID packages are visible on npm.
2. Re-run the release pipeline with completed non-npm steps skipped.
3. Set `SkipNpmRidPublish: true` and keep `SkipNpmPublish: false` so only the pointer package is submitted.
4. Set `SkipNpmPublish: true` only after the pointer package is visible.

If the pointer package published but the live npm registry validation failed afterward, re-run with `SkipNpmRidPublish: true`, `SkipNpmPointerPublish: true`, and `SkipNpmPublish: false` so the pipeline retries the install smoke without resubmitting already-published packages.

### Tag already exists but points to different commit

This indicates a mismatch between the expected release commit and an existing tag. Resolution:

1. Verify you're using the correct commit SHA.
2. If the existing tag is wrong, it must be manually deleted by someone with the required permission.
3. If the SHA is wrong, correct it and re-run.

### NuGet publish failures

The `1ES.PublishNuget@1` task is configured with `allowPackageConflicts: true`, which means it skips packages that already exist on NuGet.org. If publishing fails:

1. Check the pipeline logs for specific error messages.
2. Verify the service connection `NuGet.org - dotnet/aspire` is properly configured.
3. Ensure the service connection has push permissions for the package IDs.
4. Re-run the pipeline with skip flags for work that already completed.

### PR creation fails

The workflow checks for existing PRs before creating. If a PR exists with a different title:

1. Close or merge the existing PR.
2. Re-run the workflow.

## Architecture diagram

```text
Official source build
  -> Native archive jobs
     -> signed native archives / native CLI packages
     -> npm tarballs verified against the native archive
  -> PackageArtifacts: NuGet packages
  -> BlobArtifacts: microsoft-aspire-cli*.tgz and aspire-cli-* release assets

Azure DevOps release-publish-nuget.yml
  -> PrepareArtifacts
     -> republish NuGet artifacts with SBOM
     -> split npm RID and pointer artifacts with SBOM
     -> republish WinGet artifacts with SBOM when selected
  -> ReleaseJob
     -> verify NuGet signatures
     -> publish NuGet through 1ES.PublishNuget@1
     -> publish npm RID packages through MicroBuild.Publish
     -> wait for npm propagation
     -> publish npm pointer package through MicroBuild.Publish
     -> install the pointer package from npm and run aspire --version
     -> promote BAR build to GA channel
  -> WinGetJob
  -> GitHubTasks
     -> dispatch release-github-tasks.yml as aspire-repo-bot
     -> upload aspire-cli-* assets to the GitHub release
     -> validate Homebrew cask against the live release

GitHub release-github-tasks.yml
  -> create tag
  -> create GitHub release
  -> create merge-back PR
  -> create baseline version PR
```

## Related documentation

- [Contributing Guide](contributing.md)
- [Quarantined Tests](quarantined-tests.md)
- [Install routes & sidecars](specs/install-routes.md) — how the CLI identifies its install channel
- [WinGet README](../eng/winget/README.md) — manifest layout, prepare/publish, dogfooding
- [Homebrew README](../eng/homebrew/README.md) — cask layout, livecheck/autobump, validation modes
