# Aspire Release Process

This document describes the release process for microsoft/aspire, including both the automated workflows and manual steps required by the release manager.

## Overview

The Aspire release process uses these main automation components:

1. **Azure DevOps pipeline** ([`release-publish-nuget`](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary), source: `eng/pipelines/release-publish-nuget.yml`)
   - Downloads signed artifacts from a selected official source build.
   - Re-publishes NuGet, npm, and WinGet release inputs so 1ES can generate release SBOMs.
   - Submits NuGet packages to NuGet.org through ESRP/MicroBuild and waits for release completion.
   - Publishes Aspire CLI npm packages through ESRP/MicroBuild.
   - Promotes the build to the GA channel via darc.
   - Submits WinGet manifest PRs.
   - Optionally publishes the signed VS Code extension to the Visual Studio Marketplace.
   - Dispatches the GitHub Actions workflow below as the `aspire-repo-bot` GitHub App and waits for it to complete.
   - Uploads `aspire-cli-*` archives from the source build's `BlobArtifacts` onto the **draft** GitHub Release as the `aspire-repo-bot`.
   - Dispatches the Nix flake update workflow, passing the `aspire-cli-*.tar.gz.sha512` checksums read from the source build's `BlobArtifacts` (so the manifest is built from the build, not the draft release).
2. **GitHub Actions workflow** (`.github/workflows/release-github-tasks.yml`)
   - Creates Git tags.
   - Creates **draft** GitHub Releases (see [Draft-then-publish release flow](#draft-then-publish-release-flow)).
   - Creates merge-back PRs.
   - Creates baseline version update branches and, when not deferred, PRs.
   - Normally dispatched automatically by the AzDO pipeline above; it can also be run manually as a fallback for partial-failure re-runs.
3. **GitHub Actions workflow** (`.github/workflows/release-notes-generate.md`) — agentic
   - Triggered when the draft release is created.
   - Replaces the placeholder release notes body with auto-generated notes against the live draft (via the gh-aw `update-release` safe output).
4. **GitHub Actions workflow** (`.github/workflows/update-nix-cli-flake.yml`)
   - Receives the CLI `.sha512` checksums from the AzDO pipeline (sourced from the source build's `BlobArtifacts`), so it can run while the release is still a draft. Manual runs against a published release omit the inputs and read the release's `.sha512` assets instead.
   - Updates `eng/nix/versions.json`.
   - Commits the Nix manifest update to the `update-baseline-<version>` branch created by `release-github-tasks.yml`, then creates or updates the PR.
   - Normally dispatched automatically by the AzDO pipeline; it can also be run manually as a fallback after the GitHub release assets are live.
5. **GitHub Actions workflow** (`.github/workflows/homebrew-validate-release.yml`)
   - Triggered when the release manager publishes the draft (i.e. `release: [published]`).
   - Generates the Homebrew cask file from the just-published `aspire-cli-osx-*` assets and runs `brew audit --cask --online` + explicit binary notarization verification + a real `brew install`/`brew uninstall` cycle to catch problems before Homebrew/homebrew-cask's autobump PR is opened.
6. **GitHub Actions workflow** (`.github/workflows/extension-release.yml`)
   - Prepares a VS Code extension release PR.
   - Bumps `extension/package.json`.
   - Generates or updates `extension/CHANGELOG.md`.
   - Defaults its comparison baseline to the latest stable Marketplace VSIX's embedded `extension/.version` commit SHA.
   - Can run with `dry_run: true` in forks to validate changelog generation without bot secrets.

## Draft-then-publish release flow

GitHub's [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases) lock release assets and the underlying tag at publish time. Once a release is published, the assets attached to it (and the tag itself) cannot be changed. The body and title remain editable, but everything else is frozen.

Aspire's release flow has historically depended on mutating the released artifact: the agentic release-notes-generate workflow rewrites the body, and the AzDO pipeline's `PublishReleaseAssetsJob` uploads `aspire-cli-*` archives onto the release *after* it was created. With immutable releases enabled, all of those mutations must happen **before** the release is published.

To support this, the release flow now creates the GitHub release as a **draft**:

1. `release-github-tasks.yml` runs `gh release create --draft`, which writes the tag and creates a draft release with a placeholder body.
2. `release-github-tasks.yml` then explicitly dispatches `release-notes-generate` via `workflow_dispatch` (passing the tag name), which rewrites the body via the gh-aw `update-release` safe output. It uses an explicit dispatch because GitHub Actions does [not fire `release` events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release) (including `created`) for draft releases. (Draft releases support body edits via the Releases REST API.)
3. The AzDO pipeline's `PublishReleaseAssetsJob` uploads `aspire-cli-*` archives onto the draft via `gh release upload`. (Draft releases support asset upload via the Releases REST API.)
4. **The release manager publishes the draft manually as a final step** — see [Step 5: Publish the draft release](#step-5-publish-the-draft-release).
5. Publishing the draft fires `release: [published]`, which kicks off downstream workflows that need the live release: `release-update-support-mdx` (opens a PR on `microsoft/aspire.dev`) and `homebrew-validate-release` (validates the cask against the live download URLs). Both subscribe to `published` rather than `released` because GitHub does not reliably fire `released` when a release is published from a draft.

The release-existence check uses the same GitHub App token as release creation.
GitHub requires push access to see draft releases; a read-only automatic
`GITHUB_TOKEN` reports an existing draft as missing. Keeping this lookup on the
App token preserves retries without granting write permissions to the automatic
token.

## Installer channels

Aspire ships through several channels. The release pipeline either submits the bump itself or validates a bump submitted upstream; the per-channel docs describe manifest shape, validation modes, and dogfooding workflows in detail.

| Channel | Who submits the version bump | Per-channel docs |
|---------|------------------------------|------------------|
| **NuGet** (libraries, AppHost SDK, `Aspire.Cli` tool packages) | `release-publish-nuget` submits to ESRP/MicroBuild for NuGet.org publication | This document |
| **WinGet** (`winget install Microsoft.Aspire`) | `release-publish-nuget` submits manifest PRs to `microsoft/winget-pkgs` via `wingetcreate` | [`eng/winget/README.md`](../eng/winget/README.md) |
| **Homebrew cask** (`brew install --cask aspire`) | Upstream Homebrew/homebrew-cask's [autobump workflow](https://github.com/Homebrew/homebrew-cask/blob/master/.github/workflows/autobump.yml) opens the bump PR on a 3-hour schedule, detecting the new version via the cask's `livecheck` block. `homebrew-validate-release.yml` validates the cask against the live GitHub release after the release manager publishes it. | [`eng/homebrew/README.md`](../eng/homebrew/README.md) |
| **`dotnet tool install -g Aspire.Cli`** | `release-publish-nuget` submits the per-RID `Aspire.Cli.*.nupkg` packages to ESRP alongside the libraries | [`docs/specs/install-routes.md`](specs/install-routes.md) |
| **Install script** (`get-aspire-cli.sh` / `.ps1`) | No separate publication — the script downloads directly from the GitHub release assets attached in Step 1 | [`docs/specs/install-routes.md`](specs/install-routes.md), `eng/scripts/get-aspire-cli.*` |
| **Nix flake** (`nix run github:microsoft/aspire#aspire-cli`) | `release-publish-nuget` dispatches `.github/workflows/update-nix-cli-flake.yml`, passing the CLI `.sha512` checksums from the source build; the workflow adds the manifest update to the baseline version branch and creates or updates the PR | [`eng/nix/README.md`](../eng/nix/README.md), [`docs/ci/native-cli-packaging.md`](ci/native-cli-packaging.md) |

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
2. **Release branch and version**: Ensure the release branch exists, for example `release/9.2`, and that `eng/Versions.props` identifies the next unshipped release.
   - Before the first stable `X.Y.0` release, the branch remains on `X.Y.0`.
   - Immediately after publishing stable `X.Y.Z`, merge a dedicated PR that increments `PatchVersion` to `Z+1` before accepting more backports.
   - Do not leave the branch on a published version. A PR build such as `X.Y.Z-pr.*` has lower SemVer precedence than stable `X.Y.Z`, which can make release-branch CI and CLI update scenarios incorrectly treat the published release as an available update.
3. **Permissions and approvals**:
   - Access to run Azure DevOps pipelines with the publishing pool.
   - Confirmed ESRP NuGet onboarding for the `aspire` NuGet organization and authorization for the shared MicroBuild publisher identity (see [NuGet ESRP cutover checklist](#nuget-esrp-cutover-checklist)).
   - Permission to use the `AspireSecurePublishPipelineMarketplaceConnectionWithManagedIdentity` service connection if publishing the VS Code extension.
   - Permission for the release definition to use `EsrpThirdPartyPackageManager`, the ESRP service connection used by the MicroBuild publishing wrapper for NuGet and npm.
   - Confirmed ESRP owner and approver aliases for each enabled registry. NuGet defaults are proposed release contacts, not evidence that those contacts are onboarded.
   - GitHub write access for creating tags, releases, and PRs if you need to run the GitHub workflow manually.
4. **AzDO secrets** (already configured for chained dispatch):
   - `aspire-bot-app-id` — `aspire-repo-bot` GitHub App ID.
   - `aspire-bot-private-key` — `aspire-repo-bot` GitHub App PEM private key.

   Both live in the **`Aspire-Release-Secrets`** variable group (AzDO → Pipelines → Library) and are marked as secret. To rotate the private key, generate a new one from the App settings page (github.com/organizations/dotnet/settings/apps/aspire-repo-bot → "Private keys" → "Generate a private key"), paste the full PEM (including the `-----BEGIN/END-----` lines) into the `aspire-bot-private-key` variable, save, then revoke the old key from the same App settings page. The App ID does not change on rotation.

## Step-by-step release process

### Step 1: Run the AzDO release pipeline

1. Navigate to the Azure DevOps pipeline: [release-publish-nuget](https://dev.azure.com/dnceng/internal/_build?definitionId=1600&_a=summary) (definition `1600` in `dnceng/internal`).
2. Click **Run pipeline** and select the **Branch/tag** containing the intended release pipeline YAML. The definition defaults to `main`; selecting a `release/17.0` source build does not select `release/17.0` YAML. A release-branch YAML change requires that branch to exist in the internal mirror and be selected here, or an equivalent change on the YAML branch actually used.
3. Fill in the parameters. Most should stay at their defaults; the ones flagged `[Advanced]` in the run-pipeline form are for re-running after a partial failure, opting in to currently-disabled release legs, or testing pipeline changes on a topic branch.

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
   | `SkipNuGetPublish` | Set `true` only after verifying the complete selected package set is published; not a partial-publication recovery switch. | `false` |
   | `NuGetPublishOwners` | Single NuGet ESRP owner alias or Microsoft email. Confirm this contact during NuGet onboarding. | `joperezr` |
   | `NuGetPublishApprovers` | Single NuGet ESRP approver alias or Microsoft email, distinct from the owner. Confirm approval eligibility during onboarding. | `adamratzman` |
   | `SkipNpmRidPublish` | Set `true` if npm RID packages published but the pointer package did not. | `false` |
   | `SkipNpmPointerPublish` | Set `true` if the pointer package published but a later validation or promotion step failed. Registry validation still runs. | `false` |
   | `SkipChannelPromotion` | Set `true` if re-running after darc success. | `false` |
   | `SkipWinGetPublish` | Set `true` if re-running a stable release after WinGet success. Prerelease releases skip WinGet automatically. | `false` |
   | `SkipGitHubTasks` | Set `true` to skip dispatching the GH workflow. | `false` |
   | `SkipReleaseAssets` | Set `true` to skip uploading `aspire-cli-*` assets to the GitHub release. | `false` |
   | `SkipNixPackageUpdate` | Set `true` if the Nix flake update was already added to the baseline version PR. Stable releases dispatch the updater with checksums from the source build; prereleases skip it. | `false` |
   | `SkipVSCodeExtensionPublish` | Set `false` to publish the signed `aspire-vscode-extension` artifact to the Visual Studio Marketplace. | `true` |
   | `NpmPublishOwners` | Single ESRP owner alias or email. Overrides must be one of the required owner aliases in `eng/pipelines/release-publish-nuget.yml` (currently `joperezr` or `ankj`). | `joperezr` |
   | `NpmPublishApprovers` | Single ESRP approver alias or email. The approver must be a Microsoft address and must not be the same alias as the owner. | `adamratzman` |
   | `NpmRegistryPropagationDelayMinutes` | Delay between npm RID package and pointer package submissions. | `10` |
   | `GitHubTasksWorkflowRef` | Ref to load release GitHub workflows from when dispatching. Only affects the workflow source; the release branch and commit are passed via inputs. Override only when testing pipeline changes on a topic branch. | `main` |

4. Select the **Resources** button in the bottom right, then select the source build from the `aspire-build` dropdown.
   - The picker shows all recent builds from the `microsoft-aspire` pipeline regardless of branch. Pick the build that corresponds to the release branch and version you intend to ship.
   - Each build's tags are shown alongside its number. Verify the `release-version - X.Y.Z` tag matches the version you intend to ship before clicking **Run**. If the tag is missing, either re-run the source build after the tag-emitting change in `azure-pipelines.yml` is on that release branch or pass an explicit `ReleaseVersion` override.
5. Click **Run** and monitor the pipeline. The final stage (`GitHubTasks`) dispatches `release-github-tasks.yml`, waits for it to complete, uploads the `aspire-cli-*` archives from the source build's `BlobArtifacts` onto the newly-created **draft** GitHub release, and then dispatches `update-nix-cli-flake.yml` for stable releases so `eng/nix/versions.json` is bumped from the source build's `.sha512` checksums on the `update-baseline-<version>` branch before the baseline PR is created or updated. The AzDO pipeline only succeeds if the enabled GitHub tasks, asset upload, and Nix update dispatch succeed.
6. Verify packages appear on NuGet.org and npm, verify that the `aspire-cli-*` archives are attached to the draft GitHub release, and review the generated baseline version PR including the Nix flake manifest update. Then proceed to [Step 5: Publish the draft release](#step-5-publish-the-draft-release).

To publish only the VS Code extension after merging an extension release PR, run the same `release-publish-nuget` pipeline, select the signed source build from that merge, and set:

| Parameter | Value |
|-----------|-------|
| `ReleaseVersion` | `auto` |
| `IsPrerelease` | `false` for stable, `true` for pre-release |
| `DryRun` | `false` |
| `SkipNuGetPublish` | `true` |
| `SkipNpmRidPublish` | `true` |
| `SkipNpmPointerPublish` | `true` |
| `SkipChannelPromotion` | `true` |
| `SkipWinGetPublish` | `true` |
| `SkipGitHubTasks` | `true` |
| `SkipReleaseAssets` | `true` |
| `SkipNixPackageUpdate` | `true` |
| `SkipVSCodeExtensionPublish` | `false` |

> **Stable vs. pre-release source build:** For a stable release (`IsPrerelease=false`), use the `microsoft-aspire` build that ran automatically on merge. For a pre-release (`IsPrerelease=true`), that automatic build is stable-only and cannot be used — manually queue the `microsoft-aspire` pipeline on the merge commit with `Package VS Code Extension as Pre-Release=true`, wait for it to finish, and select that build instead. The publish job fails if the VSIX's embedded pre-release flag does not match `IsPrerelease`.

For a full Aspire release that should also publish the extension, keep the normal NuGet/channel/GitHub task settings and set `SkipVSCodeExtensionPublish` to `false`. `IsPrerelease` also controls whether extension publishing passes `--pre-release` to `vsce`; for a pre-release extension, the selected source build must also have been queued with `Package VS Code Extension as Pre-Release=true`.

The npm release path validates Windows, Linux, and macOS install summaries, publishes the seven RID packages first, waits for ESRP completion, waits for the configured propagation delay, and then publishes the top-level `@microsoft/aspire-cli` pointer package. After the pointer package publishes, the pipeline installs it from the live npm registry and runs `aspire --version` before channel promotion. This avoids installing a pointer package whose optional RID dependencies are not visible yet and catches registry propagation issues before the release is promoted. For prereleases, set `SkipNpmRidPublish=true` and `SkipNpmPointerPublish=true` unless the npm publishing path has gained explicit non-`latest` dist-tag support.

NuGet publishing remains available for prereleases when npm publishing is skipped. The pipeline stages only the flattened artifact's root-level `.nupkg` files in a clean `nuget-esrp-submission` directory, verifies their signatures, and submits that directory without rebuilding or repacking.

SBOMs, manifests, and `.snupkg` files are not submitted. Source `.nupkg` files are gathered recursively and flattened into the prepared artifact; duplicate filenames fail validation. Legacy `.symbols.nupkg` files, nested packages in the prepared artifact, and empty selections also fail validation.

NuGet submission waits for ESRP completion before npm and channel promotion. The release job has a shared **360-minute timeout**, including NuGet and both npm approval/completion waits; arrange approver availability before starting.

ESRP completion is not an automated NuGet.org visibility check. Use this two-run plan for the first cutover; `SkipChannelPromotion=true` alone does **not** suppress the other publish legs or downstream actions.

1. **First explicitly authorized live run — NuGet only:** select the intended signed source build and set:

   ```yaml
   DryRun: false
   SkipNuGetPublish: false
   SkipNpmRidPublish: true
   SkipNpmPointerPublish: true
   SkipChannelPromotion: true
   SkipWinGetPublish: true
   SkipGitHubTasks: true
   SkipReleaseAssets: true
   SkipNixPackageUpdate: true
   SkipVSCodeExtensionPublish: true
   ```

   Set `IsPrerelease=true` for a preview or `false` for stable. NuGet supports previews; the live prerelease block applies separately to npm.
2. **Second run — only after ESRP is complete and every expected NuGet package ID/version is visible on NuGet.org:** select the **same signed source build**, keep `DryRun=false`, and set `SkipNuGetPublish=true`. Start with the other skip settings above, changing a flag to `false` only for an action still intended and not already completed: npm RID/pointer publishing, channel promotion, WinGet, GitHub Tasks, release assets, Nix, or optional VS Code Marketplace publishing. Do not enable every action by default or rerun npm/other legs already completed. Keep both npm skip flags `true` for prereleases; WinGet and Nix are stable-only.
3. **Partial or uncertain NuGet publication:** do not run phase two or blindly retry publishing. Retain the package inventory and ESRP status, and coordinate recovery with the ESRP service owners; see [NuGet publish failures](#nuget-publish-failures).

`commit_sha` and `release_branch` for the GitHub workflow are derived automatically from the source build resource, so there is no need to copy them by hand.

> **Tip**: Use `DryRun: true` to exercise preparation, package-only staging, signature verification, and contact validation without publishing, promoting, tagging, creating PRs, or uploading release assets. The dry-run state is propagated to the GitHub workflow as `dry_run: true`. `DryRun` defaults to `false`; always set it explicitly for validation.

A dry run excludes ESRP submission. It does **not** validate NuGet onboarding, service-connection authorization, approver eligibility, upload/completion, duplicate handling, or registry visibility.

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

1. Re-run the AzDO pipeline with completed AzDO-side work skipped, such as `SkipNuGetPublish`, `SkipNpmRidPublish`, `SkipNpmPointerPublish`, `SkipChannelPromotion`, `SkipWinGetPublish`, `SkipNixPackageUpdate`, and `SkipReleaseAssets` set as appropriate, keeping `SkipGitHubTasks: false`. The `GitHubTasks` stage will dispatch the workflow again with the right inputs, and the workflow's own `skip_*` idempotency makes the completed steps no-ops.
2. Or, navigate to Actions → **Release GitHub Tasks**, click **Run workflow**, and fill in the parameters manually:

   | Parameter | Description | Example |
   |-----------|-------------|---------|
   | `release_version` | The version being released. | `13.0.0` |
   | `commit_sha` | Full 40-character commit SHA from the build. | `abc123...` |
   | `release_branch` | Release branch name. | `release/9.2` |
   | `is_prerelease` | `true` for preview releases. | `false` |
   | `dry_run` | `true` to validate without making changes. | `false` |
   | `skip_tagging` | Skip if tag already created. | `false` |
   | `skip_github_release` | Skip if draft release already exists. | `false` |
   | `skip_merge_pr` | Skip if PR already created. | `false` |
   | `skip_baseline_pr` | Skip if PR already created. | `false` |

Manual runs go through the normal `authorize` check (admin/maintain permission required).

### Step 4: Verify and prepare to publish

After automation completes:

1. **Review and merge the merge-back PR**:
   - Merge-back PR: `$RELEASE_BRANCH` → `main`.
   - **Do not merge the baseline version PR yet.** It updates `PackageValidationBaselineVersion` and, for stable releases, `eng/nix/versions.json`, which points at the release's public `releases/download/v<version>/...` asset URLs. Those URLs return 404 until the draft is published, so merging this PR before Step 5 would break `nix run github:microsoft/aspire#aspire-cli` on `main`. You merge it after publishing — see [Step 5](#step-5-publish-the-draft-release).
2. **Verify draft release contents**:
   - Open the draft on the [GitHub Releases page](https://github.com/microsoft/aspire/releases) (drafts appear above published releases).
   - Confirm the `aspire-cli-*` archives are attached.
   - Confirm the release-notes-generate workflow has rewritten the body. If the body still contains the placeholder text after ~30 minutes, check the [`release-notes-generate` runs](https://github.com/microsoft/aspire/actions/workflows/release-notes-generate.lock.yml) for failures and rerun via `workflow_dispatch` if needed.
3. **Verify upstream channels**:
   - Verify packages on [NuGet.org](https://www.nuget.org/packages?q=Aspire).
   - Verify npm packages on the Microsoft npm profile.

### Step 5: Publish the draft release

This is a **manual** step performed by the release manager. The release is created as a draft so that all mutations (release notes rewrite, CLI asset upload) can complete before publish — once published, GitHub's [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases) policy locks the assets and tag. Publishing must therefore happen *after* Step 4 verification, not before.

1. Navigate to the draft release on the [GitHub Releases page](https://github.com/microsoft/aspire/releases).
2. Click **Edit** to do a final review — confirm the title is `Aspire <version>`, the body looks correct, the tag is `v<version>` pointing at the release commit, and the `aspire-cli-*` archives are attached.
3. Uncheck **Set as a pre-release** if it is checked but this is a stable release (or check it for a preview release).
4. Click **Publish release**.
5. **Now merge the baseline version PR.** With the release published, its `eng/nix/versions.json` asset URLs resolve, so merging it updates `PackageValidationBaselineVersion` and the Nix manifest on `main` without breaking the flake.
6. For a stable release, open and merge a dedicated PR against the release branch that increments `PatchVersion` in `eng/Versions.props`. Complete this before accepting more backports so subsequent PR builds use the next unshipped servicing version.

Publishing the draft fires the `release: [published]` event, which triggers:

- [`release-update-support-mdx`](https://github.com/microsoft/aspire/actions/workflows/release-update-support-mdx.lock.yml): opens a draft PR on `microsoft/aspire.dev` to update the support mdx with the new release info.
- [`homebrew-validate-release`](https://github.com/microsoft/aspire/actions/workflows/homebrew-validate-release.yml): runs `brew audit --cask --online` + explicit binary notarization verification + a real `brew install`/`brew uninstall` cycle against the cask generated from the just-published `aspire-cli-osx-*` assets.

If either downstream workflow fails, the release itself is fine — the release is published and immutable. Investigate the failure on the workflow run, fix the underlying issue, and rerun via `workflow_dispatch` against the published tag.

### Step 6: Post-Release Communication

1. **Test installation**: `dotnet new install Aspire.ProjectTemplates::VERSION` and `aspire update --self`.
2. **Communicate**:
   - Update any tracking issues.
   - Notify stakeholders.

## Handling failures

Use the per-step recovery instructions before rerunning. NuGet ESRP submissions must not be blindly repeated after partial publication.

### Azure DevOps pipeline failures

| Step failed | Resolution |
|-------------|------------|
| Validate Parameters | Fix the input parameters and re-run. |
| Derive ReleaseVersion | Check that the build has a `release-version - X.Y.Z` tag, or pass `ReleaseVersion` explicitly. |
| Extract BAR Build ID | Check that the build has a `BAR ID - NNNNNN` tag. |
| Prepare/Stage/Verify NuGet Packages | Check that the selected source build produced nonempty `PackageArtifacts`, with unique package filenames and no symbol packages. |
| Prepare/List npm Packages | Check that the selected source build produced all eight `microsoft-aspire-cli*.tgz` tarballs and matching `.tgz.sig` sidecars in `BlobArtifacts`. |
| MicroBuild NuGet Publish | Inspect the ESRP release ID/status and every expected package ID/version on NuGet.org. Follow [NuGet publish failures](#nuget-publish-failures) before any resubmission. |
| MicroBuild npm Publish | Check the ESRP release result. If RID packages published but the pointer package did not, re-run with `SkipNuGetPublish: true`, `SkipNpmRidPublish: true`, `SkipNpmPointerPublish: false`, and `SkipChannelPromotion: true`; do not set `SkipNpmPointerPublish` until the pointer package is published. |
| Validate Published npm Package from Registry | Confirm the pointer package is visible on npm and that `npm install -g @microsoft/aspire-cli@<version>` works. If registry propagation is slow, re-run with completed publish steps skipped after the package is visible. |
| Promote Build to Channel | Re-run with completed publish steps skipped. |
| WinGet publishing | Re-run with the corresponding skip flag for completed work. |
| Publish VS Code Extension to Marketplace | Check that `aspire-vscode-extension` contains one `.vsix`, `.manifest`, and `.signature.p7s`; verify the `AspireSecurePublishPipelineMarketplaceConnectionWithManagedIdentity` service connection identity is a Contributor on the Visual Studio Marketplace `microsoft-aspire` publisher; re-run with the already-completed `Skip*` flags set to `true`. |
| GitHubTasks dispatch | Re-run with completed AzDO-side work skipped and `SkipGitHubTasks: false`; set `SkipReleaseAssets` according to whether release asset upload already completed. |
| Release asset upload | Re-run with `SkipGitHubTasks: true` and `SkipReleaseAssets: false` after the draft GitHub release exists. |
| Nix flake update dispatch | Re-run with `SkipGitHubTasks: true`, `SkipReleaseAssets: true`, and `SkipNixPackageUpdate: false` after the `update-baseline-<version>` branch exists. The job sources checksums from the source build's `BlobArtifacts`, so it does not require the GitHub release assets to be uploaded first. If the Nix manifest was already committed to the baseline PR, set `SkipNixPackageUpdate: true` instead. For prereleases, leave it skipped because the Nix manifest only tracks stable `x.y.z` releases. |

### GitHub Actions failures

The GitHub workflow splits release tasks into `release`, `merge-pr`, and `baseline-pr` jobs. If a step fails, drill into the run UI to see exactly which job and step (tag, release, merge PR, baseline branch/PR) hit the issue.

Re-run with the corresponding `skip_*` input set to `true` to skip steps that have already succeeded. The skip inputs are still passed step-by-step so partial-failure re-runs behave the same way they did before consolidation.

| Step failed | Resolution |
|-------------|------------|
| Authorize | Caller lacks admin/maintain permission, or the AzDO bot identity check failed. |
| Validate Version Format / Commit SHA | Fix the input parameters and re-run. |
| Create Tag | If the tag exists with the wrong SHA, resolve it manually. |
| Create GitHub Release (draft) | Re-run with `skip_tagging: true`. |
| Create Merge PR | Re-run with `skip_tagging: true` and `skip_github_release: true`. |
| Create Baseline PR | Re-run with all prior skips set to `true`. |

## Configuration

### 1ES and MicroBuild compliance

The AzDO pipeline extends the MicroBuild publish-enabled 1ES template (`azure-pipelines/MicroBuild.1ES.Official.Publish.yml@MicroBuildTemplate`). `MicroBuild.Publish.yml@MicroBuildTemplate` submits NuGet and npm through `EsrpThirdPartyPackageManager`. The source build creates, signs where platform signing applies, verifies, and stages the package artifacts; the release pipeline consumes those pre-built artifacts and does not rebuild or re-pack the CLI.

The network isolation policy remains `Permissive,CFSClean2`. Public registry checks still need network access; replacing the direct NuGet push does not establish that a stricter policy supports the remaining release steps.

### Variable groups

The pipeline uses:

| Variable group | Purpose |
|----------------|---------|
| `Aspire-Release-Secrets` | Release pipeline secrets, including the `aspire-repo-bot` GitHub App credentials. NuGet and VS Code Marketplace publishing use service connections rather than variable-group API keys. |
| `Aspire-Secrets` | WinGet bot token. |

The legacy `VscePublishToken` PAT must not be retained as a fallback after the Marketplace service-connection migration. If the variable is still present in `Aspire-Release-Secrets`, revoke the PAT and delete the variable.

### Service connections

| Connection name | Purpose |
|-----------------|---------|
| `EsrpThirdPartyPackageManager` | Shared publishing service connection used by the MicroBuild wrapper for NuGet and npm ESRP submissions. NuGet organization authorization must be confirmed separately. |
| `AspireSecurePublishPipelineMarketplaceConnectionWithManagedIdentity` | Azure Resource Manager workload-identity service connection used by `vsce --azure-credential` to publish the signed VS Code extension to the Visual Studio Marketplace. Its managed identity must be a Contributor on the `microsoft-aspire` Marketplace publisher. |
| `Darc: Maestro Production` | Used for darc channel promotion. |

The release definition must be approved for the 1ES and MicroBuild publishing templates and must have permission to use `EsrpThirdPartyPackageManager`.

#### NuGet ESRP cutover checklist

**Live cutover is blocked until onboarding and authorization are confirmed.** The YAML configures `organizationName: aspire`; neither existing npm publishing nor package ownership proves NuGet ESRP Trusted Publishing readiness.

- Confirm ESRP NuGet onboarding for `aspire` and authorization of the shared publisher behind `EsrpThirdPartyPackageManager` for definition `1600`. Do not substitute another repository's publisher identity.
- Inventory every package ID/version from the selected signed build. Confirm organization ownership and publication rights, including all new IDs with no existing NuGet.org package page.
- Confirm the NuGet owner, approver, and requester/approver eligibility rules with the service owners. Local validation accepts exactly one Microsoft alias/email per role and rejects overlap; it cannot establish service-side eligibility.
- Confirm which internal mirror branch supplies the YAML. In particular, ensure `release/17.0` exists there before selecting it; the source-build resource and `GitHubTasksWorkflowRef` do not control the release pipeline's YAML.
- Preview the expanded YAML for dry-run, skip, and live configurations without queuing a live run. With authorization, run `DryRun=true` against the intended source build and inspect the staging/signature logs and excluded publish steps. These checks do not prove the live route.
- Follow the NuGet-only first run and conditional second run above, not just `SkipChannelPromotion=true`. Retain the source build/revision, package inventory, ESRP release ID/status, approval result, and visibility evidence for every expected NuGet.org package ID/version before enabling any intended remaining actions. This is the end-to-end evidence still required for the migration.
- After successful cutover, check for other consumers, revoke the old direct-push credential, and retire `NuGet.org - dotnet/aspire`. Do not keep an automatic direct-push fallback.

#### Marketplace publishing identity

`vsce --azure-credential` authenticates as the user-assigned managed identity behind the
`AspireSecurePublishPipelineMarketplaceConnectionWithManagedIdentity` connection, following
[Secure Automated Publishing as Microsoft](https://eng.ms/docs/cloud-ai-platform/devdiv/vs-services-dougam/vs-marketplace-skofman/visual-studio-marketplace/extension-publisher-guides/secure-automated-publishing-as-microsoft).

The concrete Azure resource identifiers are maintained in the internal
[Azure DevOps service connections](https://dev.azure.com/dnceng/internal/_settings/adminservices),
not this public repository. The AzDO user ID is the value to add as a member of the
`microsoft-aspire` Marketplace publisher; the Marketplace shows it as
`<tenant id>\<principal id>`. It is not the client or principal ID, and it is only obtainable by calling
`https://app.vssps.visualstudio.com/_apis/profile/profiles/me` *while authenticated as the
identity itself*, so it must be read from a pipeline run using the service connection rather
than from the Azure portal.

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
3. Set `SkipNpmRidPublish: true` and keep `SkipNpmPointerPublish: false` so only the pointer package is submitted.
4. Set `SkipNpmPointerPublish: true` only after the pointer package is visible.

If the pointer package published but the live npm registry validation failed afterward, re-run with `SkipNpmRidPublish: true` and `SkipNpmPointerPublish: true` so the pipeline retries the install smoke without resubmitting already-published packages.

### Tag already exists but points to different commit

This indicates a mismatch between the expected release commit and an existing tag. Resolution:

1. Verify you're using the correct commit SHA.
2. If the existing tag is wrong, it must be manually deleted by someone with the required permission.
3. If the SHA is wrong, correct it and re-run.

### NuGet publish failures

ESRP duplicate-package behavior is not assumed to be idempotent. There is no automatic already-published-package filtering or direct-push fallback.

1. Retain the failed run's source build/revision, complete package inventory, ESRP release ID/status, and task diagnostics. Confirm whether ESRP is still processing before taking further action.
2. Check **every** expected package ID/version on NuGet.org, allowing for indexing delay. A successful ESRP submission is not proof of registry visibility; do not promote while packages are missing.
3. If all packages are available, rerun with `SkipNuGetPublish=true` and skip other completed work. Use the same signed source build.
4. If publication is partial or uncertain, stop automatic retries and coordinate recovery of the existing ESRP release with the service owners. The pipeline resubmits the entire selection, not just missing packages; do not rerun it with NuGet publishing enabled or set `SkipNuGetPublish=true` to bypass missing packages.
5. If ESRP confirms that no packages were published and the submission is no longer active, resolve the reported onboarding, authorization, approval, or upload failure before an explicitly authorized retry.

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
  -> BlobArtifacts: microsoft-aspire-cli*.tgz and aspire-cli-* archives + .sha512 sidecars
  -> eng/nix/versions.json is updated from the build's aspire-cli-*.tar.gz.sha512 checksums

Azure DevOps release-publish-nuget.yml
  -> PrepareArtifacts
     -> republish NuGet artifacts with SBOM
     -> split npm RID and pointer artifacts with SBOM
     -> republish WinGet artifacts with SBOM when selected
  -> ReleaseJob
     -> stage only flattened .nupkg files in clean nuget-esrp-submission
     -> verify NuGet signatures
     -> submit NuGet through MicroBuild.Publish (aspire organization)
     -> wait for NuGet ESRP approval/completion
     -> publish npm RID packages through MicroBuild.Publish
     -> wait for npm propagation
     -> publish npm pointer package through MicroBuild.Publish
     -> install the pointer package from npm and run aspire --version
     -> promote BAR build to GA channel
  -> WinGetJob
  -> GitHubTasks
     -> dispatch release-github-tasks.yml as aspire-repo-bot
     -> upload aspire-cli-* assets to the draft GitHub release
     -> dispatch update-nix-cli-flake.yml as aspire-repo-bot (stable releases),
        passing the build's aspire-cli-*.tar.gz.sha512 checksums as inputs

GitHub release-github-tasks.yml
  -> create tag
  -> create DRAFT GitHub release (with placeholder body)
  -> create merge-back PR
  -> create baseline version branch
  -> create baseline version PR immediately only when Nix update is skipped or the workflow is run manually without deferral

GitHub update-nix-cli-flake.yml (dispatched by UpdateNixPackageJob, stable releases)
  -> receive the build's aspire-cli-*.tar.gz.sha512 checksums as inputs
  -> update eng/nix/versions.json (no draft-release read needed)
  -> commit the Nix manifest update to update-baseline-<version>
  -> create or update the baseline version PR

GitHub release-notes-generate.lock.yml (dispatched by release-github-tasks.yml
                                        via workflow_dispatch — draft releases
                                        do not fire `release: created`)
  -> agent rewrites the draft release body with real notes
     via the gh-aw `update-release` safe output

[Release manager]
  -> reviews the draft release on github.com
  -> clicks "Publish release"

GitHub release-update-support-mdx.lock.yml (triggered on `release: published`)
  -> opens draft PR on microsoft/aspire.dev

GitHub homebrew-validate-release.yml (triggered on `release: published`)
  -> generates aspire.rb from the just-published aspire-cli-osx-* assets
   -> runs `brew audit --cask --online` + binary notarization verification + brew install/uninstall
```

## Related documentation

- [Contributing Guide](contributing.md)
- [Quarantined Tests](quarantined-tests.md)
- [Install routes & sidecars](specs/install-routes.md) — how the CLI identifies its install channel
- [WinGet README](../eng/winget/README.md) — manifest layout, prepare/publish, dogfooding
- [Homebrew README](../eng/homebrew/README.md) — cask layout, livecheck/autobump, validation modes
