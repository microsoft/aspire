---
name: azdo-internal
description: Expert knowledge for triggering, monitoring, and validating changes to the Aspire internal Azure DevOps pipeline (microsoft-aspire, definition 1602) on dnceng/internal. Use when asked to trigger an internal/AzDO build, check pipeline/build status, push to the internal mirror, or validate eng/ pipeline (eng/pipelines/azure-pipelines.yml / release pipeline) changes for the microsoft/aspire repository.
---

# Aspire AzDO Internal Pipeline

Expert knowledge for triggering, monitoring, and validating changes to the Aspire internal Azure DevOps pipeline (`microsoft-aspire`, definition 1602).

## Overview

The Aspire repo (`microsoft/aspire` on GitHub) has an internal mirror at `dnceng/internal/_git/microsoft-aspire` on Azure DevOps. The internal pipeline (`eng/pipelines/azure-pipelines.yml`) runs builds including native CLI compilation and installer preparation (WinGet + Homebrew).

> **Shell note (Windows).** Command examples below use bash. On Windows, run them in `pwsh` or Git Bash. `az`, `git`, and `gh` are cross-platform; only the shell glue differs. PowerShell equivalents for the constructs used here: `cmd >/dev/null 2>&1` → `cmd *> $null`; `cmd || echo "msg"` → `cmd; if (-not $?) { Write-Host "msg" }`; `git remote -v | grep internal` → `git remote -v | Select-String internal`; "grep the job log" → pipe the log to `Select-String`.

## Key Details

| Item | Value |
|------|-------|
| **AzDO Org/Project** | `dnceng` / `internal` |
| **Pipeline Definition ID** | `1602` |
| **Pipeline Name** | `microsoft-aspire` |
| **Internal Git Repo** | `https://dev.azure.com/dnceng/internal/_git/microsoft-aspire` |
| **Git Remote Name** | *Whatever you've configured for the internal repo — the name is arbitrary. Discover it with `git remote -v | grep internal/_git/microsoft-aspire`. Examples below use `INTERNAL_REMOTE` as a placeholder.* |
| **Pipeline URL** | https://dev.azure.com/dnceng/internal/_build?definitionId=1602 |
| **Pipeline YAML** | `eng/pipelines/azure-pipelines.yml` |

### Related Pipelines

| Pipeline | Definition ID | YAML | Purpose |
|----------|--------------|------|---------|
| **microsoft-aspire** (main) | 1602 | `eng/pipelines/azure-pipelines.yml` | Official internal build (PR + CI) |
| **microsoft-aspire-unofficial** | 1601 | `eng/pipelines/azure-pipelines-unofficial.yml` | Unofficial/dev builds |
| **microsoft-aspire-Release-To-NuGet** | 1600 | `eng/pipelines/release-publish-nuget.yml` | Release: NuGet/npm publish, channel promotion, installer (WinGet/Homebrew), GitHub release tasks. Consumes a def-1602 build via `resources.pipelines: aspire-build`. **Side-effecting — run only with `DryRun=true`** (see *Validating publish/release-only changes safely*). |

> Definition IDs are stable — a pipeline keeps its ID for life. The only way one changes is if the pipeline is deleted and recreated (which assigns a new ID). If a build won't start or targets the wrong pipeline, re-verify with:
>
> ```bash
> az pipelines list --organization https://dev.azure.com/dnceng --project internal \
>   --query "[?contains(name,'aspire')].{name:name,id:id}" -o table
> ```

## Prerequisites

Before triggering or querying builds, confirm the environment is set up. **Fail early** if any of these are missing rather than emitting commands that will error:

```bash
# 1. Azure CLI present
az version >/dev/null 2>&1 || echo "az CLI not installed"

# 2. The azure-devops extension (provides `az pipelines` / `az devops`)
az extension show --name azure-devops >/dev/null 2>&1 || az extension add --name azure-devops

# 3. Authenticated to the dnceng org (interactive login or AZURE_DEVOPS_EXT_PAT)
az devops project show --project internal --organization https://dev.azure.com/dnceng >/dev/null 2>&1 \
  || echo "Not authenticated to dnceng/internal — run 'az login' or set AZURE_DEVOPS_EXT_PAT"
```

If the user lacks access to `dnceng/internal` (it's a Microsoft-internal org), stop and tell them — the build cannot be triggered from outside. Don't loop on auth errors.

## How to Push to the Internal Repo

The internal AzDO repo mirrors GitHub. To push a branch for a manual build:

```bash
# Push your local branch to the internal remote (see "Git Remote Name" above to find yours)
git push INTERNAL_REMOTE <local-branch>:<remote-branch-name>

# Example (use your own alias as the branch prefix):
git push INTERNAL_REMOTE fix-azdo-pr-build:<your-alias>/fix-azdo-pr-build
```

If no remote points at the internal repo yet, add one (pick any name you like):

```bash
git remote add INTERNAL_REMOTE https://dnceng@dev.azure.com/dnceng/internal/_git/microsoft-aspire
```

> **Branch rules (important).** Only push/build **personal** branches, e.g. `<your-alias>/<branch>`. The internal mirror enforces branch policies:
>
> - `main` and `release/*` are policy-gated — direct/force pushes are rejected (they require a PR), so don't use them as your scratch validation branch.
> - A build won't start on a branch that isn't permitted by the pipeline's branch controls; if a triggered build never starts or the push is rejected, the branch (not your YAML) is usually the cause. Switch to a `<your-alias>/...` branch and retry.

## Triggering the Pipeline

### Via Azure CLI (preferred for manual triggers)

```bash
# Trigger a build on a specific branch
az pipelines run \
  --id 1602 \
  --organization https://dev.azure.com/dnceng \
  --project internal \
  --branch <branch-name>

# Example:
az pipelines run \
  --id 1602 \
  --organization https://dev.azure.com/dnceng \
  --project internal \
  --branch <your-alias>/fix-azdo-pr-build
```

The command returns JSON with the build details including `id` (build ID) and `url`.

### Compile-check without queuing a build (`previewRun`)

For a **YAML/template change**, validate that the pipeline still *compiles* before spending a real build — this is instant and queues nothing. AzDO compiles a pipeline on every run, but it never compiles on a GitHub PR, so a template error can pass all GitHub checks and only surface at run time (e.g. a literal `${{ … }}` inside an inlined PowerShell script block — even in a comment — is evaluated by the template engine and can fail with `Unable to convert from Object to String`).

`previewRun` requires the branch to be on the **mirror** (it resolves repo/template refs from there), so push first. Then:

```bash
cat > /tmp/preview.json <<'EOF'
{ "previewRun": true,
  "resources": { "repositories": { "self": { "refName": "refs/heads/<your-alias>/<branch>" } } } }
EOF

az devops invoke --area pipelines --resource runs \
  --route-parameters project=internal pipelineId=<DEF_ID> \
  --api-version 7.1 --http-method POST --in-file /tmp/preview.json \
  --organization https://dev.azure.com/dnceng -o json
```

Exit 0 + a `finalYaml` field ⇒ compiles. A non-zero exit prints the exact `file (Line: N, Col: C)` of the error. Compare base vs. PR branch to confirm the PR (not a pre-existing issue) introduced any failure, and bisect by neutralizing suspect lines on a throwaway branch.

> **api-version quirk:** use `--api-version 7.1`. Passing `7.1-preview.1` trips an `az` parsing bug (`could not convert string to float: '7.1.1'`).

### Run a pipeline that consumes another pipeline's artifacts (pin the source build)

The release pipeline consumes a def-1602 build via `resources.pipelines: aspire-build`. To run it against a **specific** upstream build you must pin that resource — `az pipelines run` **cannot** pin a pipeline-resource version, so use the REST `runs` API. Pick a source build whose artifacts your change touches (verify with `az pipelines runs artifact list --run-id <SRC_BUILD_ID>`), and pass its **run name** (e.g. `20260610.6`), not its numeric id:

```bash
cat > /tmp/release-run.json <<'EOF'
{
  "resources": {
    "repositories": { "self": { "refName": "refs/heads/<your-alias>/<branch>" } },
    "pipelines": { "aspire-build": { "version": "<SOURCE_BUILD_RUN_NAME>" } }
  },
  "templateParameters": {
    "DryRun": "true",
    "SkipGitHubTasks": "true"
  }
}
EOF

az devops invoke --area pipelines --resource runs \
  --route-parameters project=internal pipelineId=1600 \
  --api-version 7.1 --http-method POST --in-file /tmp/release-run.json \
  --organization https://dev.azure.com/dnceng -o json
```

Set the other `Skip*` parameters to scope the run to the stages your change touches. **Always pass `DryRun=true`** for the release pipeline (see below).

## Pipeline structure

Don't rely on a snapshot here — the stages, job conditions, and variables change. Read the current definition from the repo:

- `eng/pipelines/azure-pipelines.yml` — stages, jobs, gating conditions
- `eng/pipelines/templates/` — per-job step templates
- `eng/pipelines/scripts/` — the scripts those steps run

To see why a stage/job ran or was skipped in a specific build, read the build **timeline** — `az pipelines build show --id <BUILD_ID>` returns only headline status/result, not per-task records. Query the timeline resource directly (`az devops invoke --area build --resource Timeline --route-parameters project=internal buildId=<BUILD_ID> --org https://dev.azure.com/dnceng`) or open the build's web UI; the condition that actually evaluated is in the YAML.

## Monitoring a Build

### Build Results URL

```
https://dev.azure.com/dnceng/internal/_build/results?buildId=<BUILD_ID>
```

### Via Azure CLI

```bash
# Check build status
az pipelines build show \
  --id <BUILD_ID> \
  --organization https://dev.azure.com/dnceng \
  --project internal

# List recent builds for the pipeline
az pipelines build list \
  --definition-ids 1602 \
  --organization https://dev.azure.com/dnceng \
  --project internal \
  --top 5

# List builds for a specific branch
az pipelines build list \
  --definition-ids 1602 \
  --organization https://dev.azure.com/dnceng \
  --project internal \
  --branch refs/heads/<your-alias>/fix-azdo-pr-build \
  --top 5
```

## Common Tasks

### Test pipeline changes on a feature branch

```bash
# 1. Push to internal remote (personal branch only — see Branch rules)
git push INTERNAL_REMOTE my-branch:<your-alias>/my-branch

# 2. Trigger the build
az pipelines run --id 1602 --organization https://dev.azure.com/dnceng --project internal --branch <your-alias>/my-branch

# 3. Monitor (use build ID from step 2 output)
az pipelines build show --id <BUILD_ID> --organization https://dev.azure.com/dnceng --project internal --query "{status:status,result:result,sourceBranch:sourceBranch}"
```

### Monitoring a long-running build

There is **no** `az pipelines watch` command. For a long build, don't poll in a foreground loop — run a single detached watcher that polls `az pipelines build show` periodically, writes status to a file, and notifies on completion. Poll the build's `status`/`result` fields rather than scraping logs.

### Limits: what you can't fully validate on a personal branch

Some stages only run on `main`/`release/*` and will be skipped or fail on a `<your-alias>/...` branch, so they can't be exercised this way:

- **Real-publish steps** (NuGet push, npm ESRP publish, WinGet/Homebrew PR submission) only run when `DryRun=false`, which you must never set on a personal branch. But the **release pipeline itself *can* be run on a personal branch** under `DryRun=true` — the publish steps are compile-excluded or runtime-skipped, and the surrounding validation/dry-run path (artifact download, version checks, dry-run package listing) runs for real. Pin a source build and validate that path (next section).
- Steps that read the `publish-build-assets` variable group fail on non-`main`/non-`release/*` branches — by Arcade convention that group is only pulled for non-PR official branches. A feature-branch build legitimately can't access it.

When validating pipeline changes, confirm up front whether the path you're testing is even reachable from a personal branch; if not, validate the *mechanism* safely (next section) rather than running it for real on a release branch.

### Validating publish/release-only changes safely

When a change only runs on `main`/`release/*` (publish, NuGet push, WinGet/Homebrew PR submission, release notifications), validate the mechanism **without real side effects**. Never let a publishing or PR-submitting step run live during validation. In order of preference:

**0. Compile-check first with `previewRun`** (see *Triggering the Pipeline*). For any YAML/template change this is the cheapest gate and catches the whole class of compile errors that GitHub PR CI never sees. Do this before spending a real build.

**1. Run the release pipeline with `DryRun=true`, pinned to a source build.** The release/publish pipeline (`eng/pipelines/release-publish-nuget.yml`) exposes a `DryRun` runtime parameter that **defaults to `false` (live)** — so you must pass it explicitly, and you must pin the `aspire-build` source build via the REST `runs` API (`az pipelines run` can't pin a resource version — see *Run a pipeline that consumes another pipeline's artifacts* above for the recipe).

ESRP sign/publish, npm publish (compile-excluded under `DryRun=true`), the `gh release` upload (`publish-release-cli-assets.ps1`, runtime `-DryRun`), darc channel promotion, and WinGet submit are all gated on this flag, so the path runs end-to-end without pushing anything.

⚠️ **`DryRun` is not fully end-to-end read-only today.** The `GitHubTasks` stage is gated by `SkipGitHubTasks`, **not** by `DryRun` — so with `SkipGitHubTasks=false` (default), even a dry run mints a bot token and dispatches a real `release-github-tasks.yml` Actions run (which honors `dry_run` internally, creating nothing durable, but is still an external reach). **Set `SkipGitHubTasks=true` for a focused, fully read-only validation.** Tracked by [microsoft/aspire#18129](https://github.com/microsoft/aspire/issues/18129).

**Always verify dry-run actually engaged** — don't assume it. The scripts print it; grep the job log for:

```
DryRun: True        # publish-release-cli-assets.ps1
Dry Run: true       # release-publish-nuget.yml
=== DRY RUN - No npm packages were actually published ===   # npm dry-run listing
```

If you don't see it, treat the step as having run live. (A malformed `-DryRun` argument has silently bound positionally before and run live against the wrong target — confirm from the log, don't trust the intent.)

**2. Extract the step's script and run it locally** with test inputs and `-DryRun`. Best when you're changing script logic (version compute, manifest/cask generation, notifications) rather than YAML wiring — no pipeline, no side effects, fastest loop.

**3. Test gating, not effect.** If the change only affects *when* a stage runs, trigger builds on representative branches and inspect which stages were scheduled vs skipped in the build timeline. The publish never needs to fire.

Do **not** validate by repointing publish targets at a personal fork / test feed / test repo — a misconfiguration hits the real target, which is the side effect you're trying to avoid.

### Reduce the pipeline to one job (scratch worktree)

To iterate fast on a single job's mechanics, it's often easiest to temporarily strip the pipeline down to just that job (remove other stages, drop `dependsOn`, hardcode the gating condition to `true`). Do this on a **throwaway branch in a separate worktree** so the gutted YAML never reaches your real PR:

```bash
git worktree add ../azdo-scratch -b <your-alias>/azdo-scratch
# edit eng/pipelines/azure-pipelines.yml down to the one job, commit, then:
git push INTERNAL_REMOTE <your-alias>/azdo-scratch:<your-alias>/azdo-scratch
az pipelines run --id 1602 --organization https://dev.azure.com/dnceng --project internal --branch <your-alias>/azdo-scratch
```

Caveats:

- A reduced job validates the job's **own logic, not its integration.** Stripping upstream stages removes the variables and artifacts it would normally receive, so passing here doesn't guarantee it passes in a full run — re-validate the wiring end-to-end before merging.
- Keep the job's own setup steps (restore, etc.) when slimming.
- Clean up afterward: remove the worktree and delete the scratch branch on the internal remote.
- The reduced YAML isn't in your PR, so note in the PR description what you validated and link the build.
