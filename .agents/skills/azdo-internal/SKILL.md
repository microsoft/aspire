---
name: azdo-internal
description: Use when asked to trigger or inspect Aspire internal Azure DevOps builds, source-index runs, or release validation on dnceng/internal; push to the internal mirror; download build logs or artifacts; or validate eng/ pipeline changes in microsoft/aspire.
---

# Aspire AzDO Internal Pipelines

Select the pipeline that actually consumes the change, then use that definition consistently for preflight, queueing, and monitoring.

## Overview

The Aspire repo (`microsoft/aspire` on GitHub) has an internal mirror at `dnceng/internal/_git/microsoft-aspire` on Azure DevOps. The official build produces packages, native CLI binaries, and installers. Source indexing runs in a separate pipeline; it is not a source-build distribution pipeline.

Loading this skill is not permission to push, queue, cancel, or publish. Perform those actions only within the user's requested scope. Public / Helix tests are out of scope; see `docs/ci/azdo-public-pipeline.md`.

For the validation process (baseline, iteration, contributor-branch limits, and regression evidence), follow [Track B of the CI infrastructure testing guide](../pr-testing/ci-infra-testing.md#track-b--azure-devops-pipelines). The mechanics below support that process.

> **Shell note (Windows).** Examples use bash; use Git Bash on Windows or translate the shell glue to `pwsh` (`VAR=value` becomes `$VAR = 'value'`; `VAR=$(command)` becomes `$VAR = command`). `az`, `git`, and `gh` are cross-platform. Disable git pagers with `git --no-pager`.

## Key Details

| Item | Value |
|------|-------|
| **AzDO Org/Project** | `dnceng` / `internal` |
| **Internal Git Repo** | `https://dev.azure.com/dnceng/internal/_git/microsoft-aspire` |
| **Git Remote Name** | User-chosen; discover by mirror URL, never assume a name. Examples use `INTERNAL_REMOTE` as a placeholder. |

### Select the pipeline

| Pipeline | Definition ID | YAML | Purpose |
|----------|--------------|------|---------|
| **microsoft-aspire** (main) | 1602 | `eng/pipelines/azure-pipelines.yml` | Official internal build (PR + CI) |
| **microsoft-aspire-source-index** | 1693 | `eng/pipelines/azure-pipelines-source-index.yml` | Daily source indexing on `main`; manual queueing supported |
| **microsoft-aspire unofficial** | *(discover below)* | `eng/pipelines/azure-pipelines-unofficial.yml` | Unofficial/dev builds |
| **microsoft-aspire-Release-To-NuGet** | *(discover below)* | `eng/pipelines/release-publish-nuget.yml` | Release publishing; consumes official-build artifacts |

Use 1602 for official-build changes and 1693 for indexing changes; shared inputs can require both. Do not substitute a green 1602 build for indexing validation: `eng/pipelines/azure-pipelines.yml` sets `enableSourceIndex: false`. Set the selected definition below, complete the prerequisites, then confirm its live name, repository, and YAML before queueing.

```bash
PIPELINE_ID=1602 # Set to 1693 for source indexing, or a discovered definition.
```

After preflight, confirm the definition:

```bash
az pipelines list --organization https://dev.azure.com/dnceng --project internal \
  --query "[?contains(name,'aspire')].{name:name,id:id}" -o table
az pipelines show --id "$PIPELINE_ID" --organization https://dev.azure.com/dnceng --project internal \
  --query '{id:id,name:name,repository:repository.name,yaml:process.yamlFilename}' -o json
```

## Prerequisites

Before triggering or querying builds, confirm the environment is set up. **Fail early** if any of these are missing rather than emitting commands that will error:

```bash
# 1. Azure CLI present
az version

# 2. The azure-devops extension (provides `az pipelines` / `az devops`)
az extension show --name azure-devops

# 3. Access to the internal project (interactive login or AZURE_DEVOPS_EXT_PAT)
az devops project show --project internal --organization https://dev.azure.com/dnceng
```

Stop at a failed prerequisite. Install a missing `azure-devops` extension with `az extension add --name azure-devops` only when needed and permitted. A 401/403, `TF400813`, or an authentication prompt means stop before pushing or queueing; request an authorized maintainer run or report offline validation as incomplete. Never print credentials or loop on auth errors.

## How to Push to the Internal Repo

The internal AzDO repo mirrors GitHub. Discover the remote with `git --no-pager remote -v` and match its URL to `dnceng/internal/_git/microsoft-aspire`; check read access with `git --no-pager ls-remote --heads INTERNAL_REMOTE` before pushing. Read access does not establish push permission. To push a branch for a manual build:

```bash
# Push your local branch to the internal remote (see "Git Remote Name" above to find yours)
git --no-pager push INTERNAL_REMOTE <local-branch>:<remote-branch-name>

# Example (use your own alias as the branch prefix):
git --no-pager push INTERNAL_REMOTE fix-azdo-pr-build:<your-alias>/fix-azdo-pr-build
```

If no remote points at the internal repo yet, add one (pick any name you like):

```bash
git --no-pager remote add INTERNAL_REMOTE https://dnceng@dev.azure.com/dnceng/internal/_git/microsoft-aspire
```

> **Branch rules (important).** Push validation changes only to **personal** branches, e.g. `<your-alias>/<branch>`. The internal mirror enforces branch policies:
>
> - `main` and `release/*` are policy-gated — direct/force pushes are rejected (they require a PR), so don't use them as your scratch validation branch.
> - Inspect branch-control checks when a run is blocked. A personal branch does not bypass service-connection checks or job conditions. In particular, indexing defaults to `main` only (see below).

## Triggering the Pipeline

### Reconcile existing runs before queueing

Set `BRANCH` to the remote branch and `COMMIT` to the pushed commit. The example assumes you pushed the current HEAD; otherwise use the actual pushed revision. List runs for the **selected definition and branch**, including their revisions:

```bash
BRANCH='<your-alias>/<branch>'
COMMIT=$(git --no-pager rev-parse HEAD)
az pipelines build list --definition-ids "$PIPELINE_ID" \
  --organization https://dev.azure.com/dnceng --project internal --branch "refs/heads/$BRANCH" \
  --top 10 \
  --query '[].{id:id,status:status,result:result,commit:sourceVersion,reason:reason}' -o json
```

Inspect the selected YAML's `trigger:` and `pr:` filters. Pushing may auto-queue the official build for a matching branch; personal branches are not automatically included. Source indexing has `trigger: none` and `pr: none`, so pushing alone does not queue 1693.

Reuse a queued/running run only when its definition, branch, commit, and relevant parameters match the intended validation. Do not cancel unrelated work on the same branch. Cancel only confirmed superseded runs belonging to this task, after checking `az pipelines build cancel --help` for the installed CLI:

```bash
az pipelines build cancel --build-id <SUPERSEDED_BUILD_ID> \
  --organization https://dev.azure.com/dnceng --project internal
```

If that command is unavailable, use the build UI.

### Via Azure CLI (when no matching run exists)

```bash
# Trigger a build on a specific branch
az pipelines run \
  --id "$PIPELINE_ID" \
  --organization https://dev.azure.com/dnceng \
  --project internal \
  --branch "$BRANCH" \
  --commit-id "$COMMIT"
```

The command returns JSON including `id` (build ID) and `url`. Record the definition, branch, commit, parameters, and build ID. `--commit-id` pins the intended revision if the remote branch advances; omit it only when the request is explicitly to build the latest remote tip. Read runtime parameters from the selected YAML and pass any required overrides with `--parameters name=value`.

**If queueing times out, do not retry immediately.** The server may already have accepted the request. Repeat the definition/branch/revision lookup above and inspect candidate runs. Retry only after confirming no matching run was queued; if the lookup itself fails, stop rather than risk a duplicate.

## Pipeline structure

Don't rely on a snapshot here — the stages, job conditions, and variables change. Read the current definition from the repo:

- `eng/pipelines/azure-pipelines.yml` — stages, jobs, gating conditions
- `eng/pipelines/azure-pipelines-source-index.yml` — indexing schedule and job parameters
- `eng/pipelines/templates/` — per-job step templates
- `eng/pipelines/scripts/` — the scripts those steps run

To see why a stage/job ran or was skipped, read the build **timeline** and logs, not just the headline status. Follow template parameters into `eng/common/core-templates/` when a condition is inherited.

## Monitoring a Build

### Build Results URL

```
https://dev.azure.com/dnceng/internal/_build/results?buildId=<BUILD_ID>
```

### Via Azure CLI

```bash
# Check build details, including definition, sourceBranch, sourceVersion, status, and result
az pipelines build show \
  --id <BUILD_ID> \
  --organization https://dev.azure.com/dnceng \
  --project internal

# List recent builds for the selected definition (for example, to find a baseline)
az pipelines build list \
  --definition-ids "$PIPELINE_ID" \
  --organization https://dev.azure.com/dnceng --project internal \
  --top 5

# List recent builds for the selected definition and branch
az pipelines build list \
  --definition-ids "$PIPELINE_ID" \
  --organization https://dev.azure.com/dnceng --project internal \
  --branch "refs/heads/$BRANCH" \
  --top 5
```

### Read the timeline, task logs, and artifacts

```bash
az devops invoke --area build --resource Timeline \
  --route-parameters project=internal buildId=<BUILD_ID> \
  --org https://dev.azure.com/dnceng --api-version 7.1 \
  --query 'records[].{name:name,type:type,state:state,result:result,log:log.id,issues:issues}' -o json
```

`partiallySucceeded` can reflect SDL warnings, but no failed records is **not** proof of validation. Inspect warning-bearing, canceled, and skipped records; verify the intended job actually ran and its observable output matches the change. Report security findings and unreachable paths explicitly instead of dismissing all SDL warnings.

Retrieve the task's `log.id` from the timeline. Use a new output path under the session artifacts directory; `--out-file` must not already exist:

```bash
az devops invoke --area build --resource logs \
  --route-parameters project=internal buildId=<BUILD_ID> logId=<LOG_ID> \
  --org https://dev.azure.com/dnceng --api-version 7.1 --accept-media-type text/plain \
  --out-file <NEW_LOG_PATH>

az pipelines runs artifact list --run-id <BUILD_ID> \
  --org https://dev.azure.com/dnceng --project internal \
  --query '[].{name:name,type:resource.type}' -o json

az pipelines runs artifact download --run-id <BUILD_ID> \
  --org https://dev.azure.com/dnceng --project internal \
  --artifact-name <ARTIFACT_NAME_FROM_LIST> --path <DOWNLOAD_DIRECTORY>
```

The download command targets pipeline artifacts. For other artifact types, or the full log archive, use the build UI's download action. Inspect the actual output against the selected pipeline's baseline; do not assume every pipeline publishes `PackageArtifacts` or `BlobArtifacts`.

## Common Tasks

### Monitoring a long-running build

There is **no** `az pipelines watch` command. For a long build, use one detached watcher per build ID that polls `az pipelines build show`, writes `result.json` and `watch.log` under the session artifacts directory, and optionally notifies on completion. Check for an existing watcher first; do not leave a duplicate foreground polling loop running. Surface authentication/network failures instead of treating them as completion.

### Validate source indexing (definition 1693)

Read `eng/pipelines/azure-pipelines-source-index.yml` and the inherited templates:

- `eng/common/core-templates/job/source-index-stage1.yml` defines `SourceIndexStage1` with default condition `eq(variables['Build.SourceBranch'], 'refs/heads/main')`.
- `eng/common/core-templates/steps/source-index-stage1-publish.yml` processes the build binlog into an indexable solution, then uploads stage1 indexing data through a service connection.

The standalone pipeline does not override that job condition. Queueing 1693 on a personal branch is supported, but **the indexing job normally skips**; a green run on that branch does not validate indexing.

For full validation, an authorized run on `main` must show `SourceIndexStage1` executing, with successful `Build Repository`, `Source Index: Process Binlog into indexable sln`, and `Source Index: Upload Source Index stage1 artifacts to Azure` tasks in that job. Check their logs for the expected build command, binlog consumption, and upload outcome. SDL indexing tasks in another job are not a substitute. Stage1 upload success does not establish completion of any downstream indexing service.

For pre-merge iteration, test the changed build command locally or use an explicitly approved scratch pipeline that isolates build/binlog processing and omits the upload. Do not bypass the main-only condition and inadvertently publish indexing data from a personal branch. Do not rely on `runAsPublic: true` to suppress the upload: the stage1 job does not forward it to the publish-step template, so a manual internal run still includes the upload task. Before lifting the condition in a scratch copy, remove the upload step and confirm from the expanded job/timeline that it is absent. Record local/scratch evidence as partial validation; report the production upload and schedule as unvalidated until exercised. A manual run does not validate the daily schedule.

### Limits: what you can't fully validate on a personal branch

Some stages only run on `main`/`release/*` and will be skipped or fail on a `<your-alias>/...` branch, so they can't be exercised this way:

- **Publish / release stages** (NuGet push, WinGet/Homebrew PR submission) run in the release pipeline, not on feature-branch CI.
- Steps that read the `publish-build-assets` variable group fail on non-`main`/non-`release/*` branches — by Arcade convention that group is only pulled for non-PR official branches. A feature-branch build legitimately can't access it.

When validating pipeline changes, confirm up front whether the path you're testing is even reachable from a personal branch; if not, validate the *mechanism* safely (next section) rather than running it for real on a release branch.

### Validating publish/release-only changes safely

When a change only runs on `main`/`release/*` (publish, NuGet push, WinGet/Homebrew PR submission, release notifications), validate the mechanism **without real side effects**. Never let a publishing or PR-submitting step run live during validation. In order of preference:

**1. Run the release pipeline with `DryRun=true`.** The release/publish pipeline (`eng/pipelines/release-publish-nuget.yml`) exposes a `DryRun` runtime parameter that **defaults to `false` (live)** — so you must pass it explicitly:

```bash
az pipelines run --id <RELEASE_PIPELINE_ID> \
  --organization https://dev.azure.com/dnceng --project internal \
  --branch <your-alias>/<branch> \
  --parameters DryRun=true
```

ESRP sign/publish, the `gh release` upload (`publish-release-cli-assets.ps1`), and the WinGet/npm publish steps are all gated on this flag, so the path runs end-to-end without pushing anything.

**Always verify dry-run actually engaged** — don't assume it. The scripts print it; grep the job log for:

```
DryRun: True        # publish-release-cli-assets.ps1
Dry Run: true       # release-publish-nuget.yml
```

If you don't see it, treat the step as having run live. (A malformed `-DryRun` argument has silently bound positionally before and run live against the wrong target — confirm from the log, don't trust the intent.)

**2. Extract the step's script and run it locally** with test inputs and `-DryRun`. Best when you're changing script logic (version compute, manifest/cask generation, notifications) rather than YAML wiring — no pipeline, no side effects, fastest loop.

**3. Test gating, not effect.** If the change only affects *when* a stage runs, trigger builds on representative branches and inspect which stages were scheduled vs skipped in the build timeline. The publish never needs to fire.

Do **not** validate by repointing publish targets at a personal fork / test feed / test repo — a misconfiguration hits the real target, which is the side effect you're trying to avoid.

### Reduce the pipeline to one job (scratch worktree)

To iterate fast on a single non-publishing job's mechanics, temporarily strip the selected pipeline down to that job (remove other stages and drop `dependsOn`). Remove side-effecting steps before changing any branch condition. Do this on a **throwaway branch in a separate worktree** so the reduced YAML never reaches your real PR:

```bash
git --no-pager worktree add ../azdo-scratch -b <your-alias>/azdo-scratch
# Reduce the selected definition's YAML to the safe job, commit, then:
git --no-pager push INTERNAL_REMOTE <your-alias>/azdo-scratch:<your-alias>/azdo-scratch
az pipelines run --id "$PIPELINE_ID" --organization https://dev.azure.com/dnceng --project internal --branch <your-alias>/azdo-scratch
```

Caveats:

- A reduced job validates the job's **own logic, not its integration.** Stripping upstream stages removes the variables and artifacts it would normally receive, so passing here doesn't guarantee it passes in a full run — re-validate the wiring end-to-end before merging.
- Keep the job's own setup steps (restore, etc.) when slimming.
- Never force publishing or source-index upload gates to `true`; omit side-effecting steps from scratch validation.
- Clean up afterward: remove the worktree and delete the scratch branch on the internal remote.
- The reduced YAML isn't in your PR, so note in the PR description what you validated and link the build.
