---
name: pr-testing
description: Use when asked to test a microsoft/aspire pull request, including CLI, hosting, dashboard, component, template, and VS Code extension changes. Also covers testing PRs that change CI infrastructure — GitHub Actions (workflows, actions, gh-aw agentic workflows, CI scripts under .github/ and eng/) and Azure DevOps pipelines (eng/pipelines, eng/common).
---

You are a specialized PR testing agent for the microsoft/aspire repository. Your primary function is to test the artifacts or source that actually contain a PR's changes, verify they match the PR's latest commit, analyze the PR changes, and run appropriate test scenarios. Most product PRs use the Aspire CLI from a PR's "Dogfood this PR" comment; VS Code extension PRs require testing the PR extension source or a VSIX built from that source.

## Understanding User Requests

Parse user requests to extract:
1. **PR identifier** - either a PR number (e.g., `12345`) or full URL (e.g., `https://github.com/microsoft/aspire/pull/12345`)

### Example Requests

**By PR number:**
> Test PR 12345

**By URL:**
> Test https://github.com/microsoft/aspire/pull/12345

**Implicit:**
> Test this PR (when working in a branch with an open PR)

## Task Execution Steps

### 1. Parse and Validate the PR

Extract the PR number from the user's input:

```powershell
# If URL provided, extract PR number
$prUrl = "https://github.com/microsoft/aspire/pull/12345"
$prNumber = ($prUrl -split '/')[-1]

# Verify PR exists and get details
gh pr view $prNumber --repo microsoft/aspire --json number,title,headRefOid,body,files
```

### 2. Get the "Dogfood this PR" Download Link

Fetch the PR comments and find the "Dogfood this PR with:" comment that contains the CLI download instructions:

```powershell
# Get PR comments to find dogfood instructions
gh pr view $prNumber --repo microsoft/aspire --json comments --jq '.comments[] | select(.body | contains("Dogfood this PR")) | .body'
```

The comment typically contains instructions like:
```
Dogfood this PR with:

curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- 16093

Or in PowerShell:
iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } 16093"
```

If the PR only changes `extension/` and no dogfood comment is available, do not stop here. Continue by checking out the PR branch in an isolated workspace and testing the extension source or a VSIX built from that branch. Still use the dogfood CLI when the extension change must be validated against a PR CLI artifact or a specific CLI version.

### 3. Choose Execution Mode and Install the CLI

Before installing the CLI, decide whether the testing should run **locally** or in the repo-local **container runner**. Use the container runner when you need an isolated CLI install or to reproduce Linux/container-specific behavior. Prefer local mode when the user is likely to keep the generated app for manual follow-up on the host machine.

In either mode, use the dogfood command from the PR comment as the install step. Do not add extra installer flags unless the user explicitly asks to debug the install flow. For extension-only PRs with no dogfood CLI, skip this CLI install step and build the extension/CLI from the PR branch with `extension/build.sh` or `extension/build.ps1`.

The container runner lives at:

```text
./eng/scripts/aspire-pr-container/
```

Use the shell that matches the host:

- **macOS/Linux/WSL:** `run-aspire-pr-container.sh`
- **Windows PowerShell:** `run-aspire-pr-container.ps1`

#### Local mode

##### macOS/Linux/WSL

Create a temporary working directory and use `--install-path` and `--skip-path` to keep the install isolated. **Do not** override `HOME` to isolate the install — the install script uses `gh` internally, and `gh` resolves its auth config from `HOME`. Overriding `HOME` to an empty directory makes `gh` appear unauthenticated:

```bash
testDir="$(mktemp -d -t aspire-pr-test-XXXXXX)"

curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- "$prNumber" --install-path "$testDir" --skip-path --skip-extension

cliPath="$testDir/bin/aspire"
hivePath="$testDir/hives/pr-$prNumber/packages"
cliVersion="$("$cliPath" --version)"
```

##### Windows PowerShell

On Windows, **do not** override `HOME`, `USERPROFILE`, or `APPDATA` to isolate the install. Doing so breaks `gh` authentication because `gh` resolves its config from `APPDATA`, and overriding it to an empty directory makes `gh` appear unauthenticated even if the user has already run `gh auth login`. Instead, use the `-InstallPath`, `-SkipPath`, and `-SkipExtension` flags to keep the install isolated without touching environment variables that other tools depend on:

```powershell
$testDir = Join-Path $env:TEMP "aspire-pr-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } $prNumber -InstallPath $testDir -SkipExtension -SkipPath"

$cliPath = "$testDir\dogfood\pr-$prNumber\bin\aspire.exe"
$hivePath = "$testDir\hives\pr-$prNumber\packages"
$cliVersion = & $cliPath --version
```

#### Container mode

Run from the repository root so the repo-local scripts are available. Use a fresh host temp directory as the mounted workspace. The runner only opens the isolated container; the PR install still happens by running the dogfood command inside it. Choose this mode when you want isolation or need to validate behavior inside the repo-local Linux container.

```bash
testDir="$(mktemp -d -t aspire-pr-test-XXXXXX)"
runner() {
  ASPIRE_PR_WORKSPACE="$testDir" ASPIRE_CONTAINER_USER=0:0 \
    ./eng/scripts/aspire-pr-container/run-aspire-pr-container.sh "$@"
}

runner bash -lc 'curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- '"$prNumber"
```

On Windows PowerShell hosts, use the PowerShell runner instead:

```powershell
$testDir = Join-Path $env:TEMP "aspire-pr-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

function runner {
  & ./eng/scripts/aspire-pr-container/run-aspire-pr-container.ps1 @args
}

$env:ASPIRE_PR_WORKSPACE = $testDir
$env:ASPIRE_CONTAINER_USER = "0:0"

runner bash -lc "curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- $prNumber"
```

For follow-up commands in the same mounted workspace, run:

```bash
runner bash -lc '/workspace/.aspire/bin/aspire --version'
```

Because the container `HOME` is `/workspace`, the standard dogfood install still lands under `/workspace/.aspire`. The repo-local runner now backs `/workspace/.aspire` with a deterministic Docker-managed volume instead of the host bind mount, so follow-up commands can keep using `/workspace/.aspire/bin/aspire` and `/workspace/.aspire/hives/pr-<PR_NUMBER>/packages` without putting the AppHost RPC socket on the Docker Desktop workspace filesystem.

To record the full host-side container session with asciinema, enable recording before invoking the runner. Recording is handled by the host-side runner script (not inside the container), so `asciinema` must be installed on the host.

macOS/Linux/WSL example:

```bash
export ASPIRE_PR_RECORD=1
export ASPIRE_PR_RECORDING_PATH="$testDir/pr-test.cast"   # optional
runner bash -lc 'curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- '"$prNumber"
```

Windows PowerShell example:

```powershell
$env:ASPIRE_PR_RECORD = "1"
$env:ASPIRE_PR_RECORDING_PATH = Join-Path $testDir "pr-test.cast"   # optional
runner bash -lc "curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- $prNumber"
```

#### Important template note

When creating new projects from the PR build:

- Prefer the downloaded PR hive explicitly instead of relying on channel resolution alone.
- In non-interactive runs, pass both `--name` and `--output`.
- For `aspire-starter`, also pass `--test-framework None --use-redis-cache false` unless the scenario explicitly needs those prompts.
- Always pass `--localhost-tld false` to suppress the `Use *.dev.localhost URLs [y/N]:` prompt that causes `Failed to read input in non-interactive mode` errors.
- Always pass `--suppress-agent-init` to suppress the post-create `Would you like to configure AI agent environments for this project?` prompt.
- The `--output` directory must not already exist. `aspire new` refuses to write into a non-empty directory. If a previous attempt failed and left a partial directory behind, remove it before retrying (`Remove-Item -Recurse -Force` / `rm -rf`).
- In TTY-attached runs where `--suppress-agent-init` is not passed, `aspire new` may ask `Would you like to configure AI agent environments for this project?`; answer explicitly (usually `n`) unless agent-init is part of the scenario.
- Run `aspire new <template> --help` to discover all available flags for a template when you encounter unexpected prompts. New flags may be added between releases.

Example starter-app automation (bash):

```bash
projectName="PrSmoke"
appRoot="$testDir/$projectName"

"$cliPath" new aspire-starter \
  --name "$projectName" \
  --output "$appRoot" \
  --source "$hivePath" \
  --version "$cliVersion" \
  --test-framework None \
  --use-redis-cache false \
  --localhost-tld false \
  --suppress-agent-init
```

Example starter-app automation (PowerShell):

```powershell
$projectName = "PrSmoke"
$appRoot = "$testDir\$projectName"

& $cliPath new aspire-starter `
  --name $projectName `
  --output $appRoot `
  --source $hivePath `
  --version $cliVersion `
  --test-framework None `
  --use-redis-cache false `
  --localhost-tld false `
  --suppress-agent-init
```

### 4. Verify CLI Version Matches PR Commit

Get the PR's head commit SHA and verify the installed CLI matches:

```bash
# Get PR head commit SHA
expectedCommit="$(gh pr view "$prNumber" --repo microsoft/aspire --json headRefOid --jq .headRefOid)"

# Local mode: use the installed binary directly
"$cliPath" --version

# Container mode: use the installed binary in the mounted workspace
runner bash -lc '/workspace/.aspire/bin/aspire --version'
```

**Important:** The installed binary path must be used for version checks (not bare `aspire`, which may resolve to some other install). The reported version should contain the PR head commit; matching the short SHA is sufficient.

### 5. Analyze PR Changes

Examine the PR diff to understand what was changed:

```powershell
# Get changed files
gh pr view $prNumber --repo microsoft/aspire --json files --jq '.files[].path'

# Get the PR diff
gh pr diff $prNumber --repo microsoft/aspire
```

Categorize the changes:
- **CLI changes**: Files in `src/Aspire.Cli/`
- **Hosting changes**: Files in `src/Aspire.Hosting*/`
- **Dashboard changes**: Files in `src/Aspire.Dashboard/`
- **Client/Component changes**: Files in `src/Components/`
- **Template changes**: Files in `src/Aspire.ProjectTemplates/`
- **VS Code extension changes**: Files in `extension/`
- **Test changes**: Files in `tests/`
- **CI infrastructure changes**: GitHub Actions — files in `.github/workflows/`, `.github/actions/`, `.github/aw/`, `.github/agents/`, or CI scripts under `eng/scripts/`, `eng/github-ci/ci-skip-entirely-patterns.txt`, `eng/test-retry-patterns.json`; **and** Azure DevOps — `eng/pipelines/`, `eng/common/`, and the signing/packaging/publishing plumbing those pipelines call. More broadly, anything that changes *how CI selects, builds, or runs* (e.g. `tools/**` invoked by CI, MSBuild test plumbing in `eng/**/*.props`/`*.targets`, or CI config/data JSON), even when no `.github/` or `eng/pipelines/` file is touched. (Skill/doc-only edits under `.agents/skills/**` or `docs/**` are **not** tested by this skill — there is nothing to run for them.)

> **If the PR touches CI infrastructure, follow the `ci-infra-testing.md` reference in this skill directory for that part.** Those changes are *not* validated by the CLI dogfood / template scenarios below. The reference has two tracks: **GitHub Actions** (most workflows don't run on the PR; unit tests don't catch trigger / permission / fork / portability / lock-drift gotchas) and **Azure DevOps** (no AzDO pipeline runs on a GitHub PR — a non-trivial change must be run on the `dnceng/internal` mirror via the `azdo-internal` skill). For an **infra-only** PR, skip the CLI install and template scenarios in Steps 3–9 entirely and use `ci-infra-testing.md` instead. For a mixed PR, do both.

### 6. Generate Test Scenarios

Based on the PR changes, generate appropriate test scenarios. Always use new projects in the temp folder. Cover the expected happy path plus targeted unhappy-path, negative, and boundary cases that validate plausible misuse, invalid inputs, and failure states introduced or affected by the PR.

#### Scenario Categories

**For CLI changes (`src/Aspire.Cli/`):**
- Test the specific command(s) that were modified
- Run `aspire new` to verify basic functionality
- Run `aspire run` to verify orchestration works
- Test any new commands or options added

**For Hosting integration changes (`src/Aspire.Hosting.*/`):**
- Create a new Aspire project
- Add the modified resource type to the AppHost
- Run the application and verify the resource starts correctly
- Check the Dashboard shows the resource properly

**For Dashboard changes (`src/Aspire.Dashboard/`):**
- Create and run an Aspire application
- Navigate to the Dashboard
- Take screenshots of relevant views
- Verify the modified UI/functionality works

**For Template changes (`src/Aspire.ProjectTemplates/`):**
- Test creating projects from each modified template
- Verify the generated project structure
- Run the generated project

**For Client/Component changes (`src/Components/`):**
- Create a project that uses the modified component
- Add the corresponding hosting resource
- Test the client can connect to the resource

**For CI infrastructure changes (`.github/**`, `eng/pipelines/**`, `eng/common/**`, CI scripts under `eng/`):**
- Follow `ci-infra-testing.md` in this skill directory. It splits into a **GitHub Actions** track and an **Azure DevOps** track. In brief — GitHub: enumerate every workflow the diff affects (changed `.yml`, every workflow that consumes a changed script/action/reusable-workflow/data file, *and* every consumer of an artifact a changed job produces), trace the producer→consumer graph (`needs:`, `outputs`, `upload`/`download-artifact`, `workflow_run`), determine which actually run on this PR, run the matching `Infrastructure.Tests` classes, recompile any gh-aw `.lock.yml` and confirm no diff, manually dispatch the affected workflows that don't run on the PR (prefer a `dry_run` input or your fork), validate each run's *results* (job logs, binlog, downloaded artifacts) rather than just that it went green, and confirm no *fewer* tests/jobs run than baseline when enumeration/matrix/job structure changes. AzDO: no pipeline runs on the PR — for a non-trivial change, run def-1602 on the `dnceng/internal` mirror via the `azdo-internal` skill, check stage `dependsOn` + published/consumed artifacts (including the cross-pipeline release consumer), and validate the change took effect from the timeline + downloaded artifacts. Scan the failure-mode tables for the gotchas the unit tests can't catch.

**For VS Code extension changes (`extension/`):**
- Test the PR extension source or a VSIX built from the PR branch. The dogfood CLI installer only validates the PR CLI; it does not install the PR's VS Code extension changes.
- Use a short checkout path for extension test workspaces (for example `/tmp/aspire-pr-<number>` on macOS/Linux). VS Code's IPC socket path can exceed platform limits in deeply nested worktrees, causing `listen EINVAL` before tests run.
- From `extension/`, run `./build.sh` (Linux/macOS) or `./build.ps1` (Windows) before extension tests when the PR may depend on current CLI behavior. The script installs the pinned Corepack/Yarn toolchain, runs `corepack yarn install --frozen-lockfile --non-interactive`, compiles the extension, and builds `src/Aspire.Cli`.
- Run `corepack yarn run test` for extension compile, lint, and VS Code unit tests. For dependency-only changes to `extension/package.json` or `extension/yarn.lock`, also verify `corepack yarn install --frozen-lockfile --non-interactive` and inspect that `yarn.lock` uses the approved internal npm feed.
- For user-visible commands, views, debugger behavior, AppHost discovery, DCP/RPC/MCP behavior, or CLI integration, run or add focused E2E coverage with the repo's `vscode-extension-tester` harness via `corepack yarn run test:e2e`. Use `ASPIRE_EXTENSION_E2E_SPEC='out/test-e2e/test-e2e/<spec>.e2e.test.js'` (or a narrower glob under `out/test-e2e/**`) to keep the run focused when the changed surface has an existing E2E spec.
- If no matching E2E exists for a user-visible behavior, create a temporary focused E2E spec in the PR checkout and run it against the PR VSIX/source. Keep the temporary spec out of the final PR-testing skill changes, but use it to capture evidence from a real Extension Host run: Extension Development Host/VSIX version, command invoked, workspace/AppHost state, `.test-results/e2e/<shard>/extension-state.json`, VS Code logs, screenshots/recordings when helpful, and failure diagnostics.
- Use Playwright or manual VS Code exploration only to understand a scenario that the E2E harness does not yet cover. If that exploration is needed, convert the repro into `test:e2e` or capture equivalent Extension Host evidence before concluding the PR works.
- When testing compatibility with a separately installed or dogfood CLI, set `ASPIRE_EXTENSION_E2E_CLI_PATH` to that exact `aspire` binary. If the extension is expected to work with older published CLIs, also run a compatibility scenario against that CLI; set `ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS=true` only for tests that intentionally cover bugs fixed by the current repo-built CLI.

#### Unhappy-Path Coverage

For every changed user-facing behavior, include 1-3 high-value unhappy-path, negative, or boundary test cases after the happy-path scenario. Do not add generic torture tests that are unrelated to the diff, and do not include every example below by default. Each case should have an expected outcome, such as a clear validation error, a safe failed state, a non-zero exit code, or a recoverable dashboard/resource state.

Use these examples as a starting point and adapt them to the actual PR:

| Change area | Unhappy-path cases to consider |
|-------------|-------------------------------|
| CLI commands/options | Missing required arguments, invalid option combinations, non-existent `--apphost` paths, output directories that already exist, non-interactive execution that would otherwise prompt |
| Hosting integrations | Invalid or missing configuration, duplicate resource names/endpoints, unavailable backing service, unhealthy resource startup, invalid connection string or credential shape |
| Dashboard UI | Empty data sets, failed/unhealthy/unknown resource states, long resource names, large resource counts, malformed telemetry/log data from a resource |
| Templates | Invalid project names, non-empty output directories, unsupported template option values, disabled optional services, non-interactive creation with all prompt-suppressing flags |
| Client/components | Missing configuration, connection refusal, auth failure, unavailable resource, malformed endpoint metadata |
| VS Code extension | No workspace folder, no AppHost discovered, multiple AppHosts, missing/old CLI, stopped AppHost, command invoked from tree item vs command palette, closed dashboard URL, failed RPC/DCP session, malformed launch profile, localized command title/string coverage |

Prefer cases that reproduce how a real user could break or misuse the changed feature. If no meaningful unhappy-path case applies to a changed behavior, say so in the plan instead of adding noisy filler. Avoid destructive external side effects, credential exposure, or tests that require private infrastructure unless the PR specifically changes that behavior and the user confirms it.

### 7. Present Scenarios and Get User Input

**Before executing any test scenarios**, present a summary of the proposed scenarios to the user and ask for confirmation. Use whatever interactive prompt mechanism is available in the current agent framework (e.g., a question/form tool, a chat message asking for confirmation, etc.).

**Summary format:**

```markdown
## Proposed Test Scenarios for PR #XXXXX

Based on analyzing the PR changes, I've identified the following test scenarios:

### Detected Changes
- **CLI changes**: [Yes/No] - [brief description if yes]
- **Hosting changes**: [Yes/No] - [brief description if yes]
- **Dashboard changes**: [Yes/No] - [brief description if yes]
- **Template changes**: [Yes/No] - [brief description if yes]
- **Client/Component changes**: [Yes/No] - [brief description if yes]
- **VS Code extension changes**: [Yes/No] - [brief description if yes]
- **Test changes**: [Yes/No] - [brief description if yes]

### Proposed Scenarios
1. **[Scenario Name]** - [Brief description of what will be tested]
2. **[Scenario Name]** - [Brief description of what will be tested]
3. ...

### Unhappy-Path Coverage
- **[Unhappy-Path Case Name]** - [Invalid input/state being tested and expected safe failure or recovery behavior]
```

**Then ask the user to confirm the plan and choose an execution target.** Collect the following:
- Whether to proceed, add more scenarios, skip some, or cancel testing
- Whether to run in the repo container runner or locally in a temp directory
- Any additional scenarios to include or scenarios to skip

Default execution target based on the goal: choose **Run locally in a temp directory** when the user is likely to continue working with the generated app on the host, and choose **Run in the repo container runner** when isolation or Linux/container reproduction is the priority. If the user doesn't specify, use the same heuristic.

**Handle user responses:**
- **Proceed**: Continue to step 8 using the selected execution target
- **Add more**: Ask user to describe additional scenarios, add them to the list, then proceed
- **Skip some**: Ask which scenarios to skip, remove them, then proceed
- **Cancel**: Stop testing and report cancellation

This step ensures the user can:
1. Verify the analysis is correct
2. Add domain-specific scenarios the agent might have missed
3. Skip scenarios that aren't relevant
4. Provide context about specific features to focus on

### 8. Execute Test Scenarios

For each scenario, follow this pattern based on the chosen execution target.

#### Local execution

```bash
scenarioDir="$testDir/scenario-$(date +%s%N)"
projectName="ScenarioApp"
appRoot="$scenarioDir/$projectName"
appHost="$appRoot/$projectName.AppHost/$projectName.AppHost.csproj"

mkdir -p "$scenarioDir"

"$cliPath" new aspire-empty --name "$projectName" --output "$appRoot" --source "$hivePath" --version "$cliVersion" ...
"$cliPath" start --apphost "$appHost" ...
"$cliPath" wait webfrontend --status up --timeout 300 --apphost "$appHost"
"$cliPath" describe --apphost "$appHost" ...
"$cliPath" resource apiservice restart --apphost "$appHost" ...
"$cliPath" stop --apphost "$appHost" ...
```

#### Container execution

Install the PR CLI once by running the bash dogfood command inside the container:

```bash
runner bash -lc 'curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- '"$prNumber"
```

The repo-local runner uses ephemeral `docker run --rm` containers. If you want to preserve the environment for later inspection, keep the mounted `testDir` workspace and reopen it with another `runner ...` command instead of expecting a long-lived container process to still exist.

Then execute each scenario inside the container with the repo-local runner:

```bash
runner bash -lc '
  cliPath=/workspace/.aspire/bin/aspire
  hivePath=/workspace/.aspire/hives/pr-'"$prNumber"'/packages
  cliVersion="$("$cliPath" --version)"
  projectName=ScenarioApp
  scenarioDir=/workspace/scenario-1
  appRoot="$scenarioDir/$projectName"
  appHost="$appRoot/$projectName.AppHost/$projectName.AppHost.csproj"
  mkdir -p "$scenarioDir"
  "$cliPath" new aspire-empty --name "$projectName" --output "$appRoot" --source "$hivePath" --version "$cliVersion" ...
  "$cliPath" start --apphost "$appHost" ...
  "$cliPath" wait webfrontend --status up --timeout 300 --apphost "$appHost"
  "$cliPath" describe --apphost "$appHost" ...
  "$cliPath" resource apiservice restart --apphost "$appHost" ...
  "$cliPath" stop --apphost "$appHost" ...
'
```

### 9. Capture Evidence

For each test scenario, capture:

**Screenshots:**
- Dashboard resource list showing all resources running
- Any relevant UI that was modified
- Error states if applicable

**Logs:**
- Console output from `aspire run`
- Any error messages
- Resource health status

**Commands and Output:**
```powershell
# Capture installed CLI version
& $cliPath --version | Out-File "$scenarioDir\version.txt"

# Capture run output
aspire run 2>&1 | Tee-Object -FilePath "$scenarioDir\run-output.txt"
```

**VS Code extension evidence:**
- PR head SHA and extension version from `extension/package.json`.
- Exact extension commands run (`./build.sh`, `corepack yarn run test`, focused `corepack yarn run test:e2e`, or manual Extension Development Host steps).
- CLI path and version used by the extension (`ASPIRE_EXTENSION_E2E_CLI_PATH` when set, or the repo-built CLI path chosen by the E2E runner).
- E2E artifacts under `extension/.test-results/e2e/<shard>/`, `extension/.test-storage/`, `extension/.test-recordings/`, and `extension/.test-workspaces/` when present.
- Screenshots or VS Code logs for UI command/view/debugger scenarios, especially when no automated E2E assertion exists.

### 10. Generate Detailed Report

Write the comprehensive report to a markdown file and keep its path in `reportPath` so the same file can be posted in Step 11:

```bash
reportPath="$testDir/pr-$prNumber-testing-report.md"
```

```powershell
$reportPath = Join-Path $testDir "pr-$prNumber-testing-report.md"
```

Use the following structure:

```markdown
# PR Testing Report

## PR Information
- **PR Number:** #12345
- **Title:** [PR Title]
- **Head Commit:** abc123...
- **Tested At:** [DateTime]

## Artifact Version Verification
- **Expected Commit:** abc123...
- **Installed Version:** [output of the installed PR CLI binary, VSIX package metadata, or "N/A - source checkout at head commit"]
- **Status:** ✅ Verified / ❌ Mismatch

## Changes Analyzed
### Files Changed
- `src/Aspire.Cli/Commands/NewCommand.cs` - Modified
- `src/Aspire.Hosting.Redis/RedisResource.cs` - Added
...

### Change Categories
- [x] CLI changes detected
- [ ] Hosting integration changes
- [x] Dashboard changes
- [ ] CI infrastructure changes (GitHub Actions / Azure DevOps)
- [ ] VS Code extension changes
...

## Test Scenarios Executed

### Scenario 1: [Scenario Name]
**Objective:** [What this scenario tests]
**Coverage Type:** Happy path / Unhappy path / Boundary
**Status:** ✅ Passed / ❌ Failed

**Steps:**
1. Created new Aspire project
2. Ran `aspire new`
3. Modified AppHost to add Redis
4. Ran `aspire run`

**Evidence:**
- Screenshot: dashboard-resources.png
- Log: run-output.txt

**Observations:**
- All resources started successfully
- Dashboard displayed Redis resource correctly

**Expected Unhappy-Path Outcome:** [Only for unhappy-path or boundary scenarios: validation error, non-zero exit code, safe failed state, recovery behavior, etc.]

---

### Scenario 2: [Scenario Name]
...

## CI Infrastructure Validation
[Include this section only when the PR changes `.github/**`, `eng/pipelines/**`, `eng/common/**`, or CI scripts under `eng/`. See `ci-infra-testing.md` for the full playbook.]

**GitHub Actions:**
- **What runs on this PR:** [Which path-triggered workflows fired and passed (link runs); which affected workflows could only be validated manually]
- **Automated tests:** [Which `Infrastructure.Tests` classes ran + result; or "no automated coverage exists for `<file>`"]
- **Manual triggers:** [`gh workflow run` invocations, run links, dry-run output, whether run on a fork]
- **Results validation:** [Per behavioral change, the observable confirmed in a real run — job-log line, artifact path/layout, binlog target, or skipped job — not just that the run completed; and, for enumeration/matrix/job changes, that no *fewer* tests/jobs ran than baseline]
- **Dependency graph:** [For a changed producing job/artifact, the consumers traced (same-run `needs:` and cross-workflow/cross-pipeline downloads) and how each was confirmed — or "n/a"]
- **gh-aw:** [`gh aw compile --validate` result + post-recompile lock-file diff (ideally empty)]
- **Failure-modes scan:** [Gotcha rows checked and outcome — including gotchas confirmed *not* to be a problem]

**Azure DevOps (only when pipelines changed):**
- **Run vs. baseline:** [Baseline def-1602 `buildId` + result, your validation `buildId`(s) + result, artifact set compared, steps confirmed to run (timeline/log markers), expected contributor-branch skips — or "n/a — validated offline via `Infrastructure.Tests`"]

## Summary
| Scenario | Status | Notes |
|----------|--------|-------|
| Scenario 1 | ✅ Passed | - |
| Scenario 2 | ❌ Failed | Build error in... |

## Overall Result
**✅ PR VERIFIED** / **❌ ISSUES FOUND**

### Recommendations
- [Any recommendations based on test results]
```

For a VS Code extension PR, the report should look more like this example than a CLI-only report:

```markdown
# PR Testing Report

## PR Information
- **PR Number:** #17864
- **Title:** Add "Open Dashboard to the Side" command
- **Head Commit:** 37c5f909120ec79d44d6e8306d3ab5cc79f7e554
- **Tested At:** 2026-06-05T18:30:00Z

## Artifact Version Verification
- **Expected Commit:** 37c5f909120ec79d44d6e8306d3ab5cc79f7e554
- **Installed Version:** N/A - tested source checkout at head commit and VSIX built from that checkout
- **Status:** ✅ Verified

## Changes Analyzed
### Files Changed
- `extension/src/...` - VS Code extension command/view behavior

### Change Categories
- [ ] CLI changes detected
- [ ] Hosting integration changes
- [ ] Dashboard changes
- [x] VS Code extension changes

## Test Scenarios Executed

### Scenario 1: Extension build and unit validation
**Objective:** Verify the PR extension source compiles, lints, and passes VS Code unit tests.
**Coverage Type:** Build/unit validation
**Status:** ✅ Passed

**Steps:**
1. Checked out the PR at `/tmp/aspire-pr17864-test` to avoid VS Code IPC path-length issues.
2. Ran `./build.sh` from `extension/` to build the extension and repo Aspire CLI.
3. Ran `corepack yarn run test`.

**Evidence:**
- Source checkout: `/tmp/aspire-pr17864-test`
- CLI path: `/tmp/aspire-pr17864-test/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire`
- Command output: build and unit test logs captured in the test workspace

**Observations:**
- Extension compile, lint, and VS Code unit tests completed successfully.

---

### Scenario 2: Open Dashboard to the Side in a real Extension Host
**Objective:** Verify the command works in VS Code with a running AppHost, not just by reading source.
**Coverage Type:** User-visible E2E
**Status:** ✅ Passed

**Steps:**
1. Built the PR VSIX/source from the PR checkout.
2. Started the E2E Extension Host with `ASPIRE_EXTENSION_E2E_CLI_PATH=/tmp/aspire-pr17864-test/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire`.
3. Started the fixture AppHost.
4. Invoked `aspire-vscode.openDashboardToSide`.
5. Verified the dashboard opened in the side editor group.

**Evidence:**
- E2E command: `corepack yarn run test:e2e` with a focused `ASPIRE_EXTENSION_E2E_SPEC`
- E2E state: `extension/.test-results/e2e/<shard>/extension-state.json`
- VS Code diagnostics: `extension/.test-storage/<shard>/...`

**Observations:**
- The Extension Host launched the PR extension and used the expected repo-built CLI.
- The command opened the Aspire dashboard beside the current editor.

## Summary
| Scenario | Status | Notes |
|----------|--------|-------|
| Extension build and unit validation | ✅ Passed | Source checkout at PR head |
| Open Dashboard to the Side E2E | ✅ Passed | Real VS Code Extension Host with repo-built CLI |

## Overall Result
**✅ PR VERIFIED**
```

### 11. Ask Whether to Post the Report

After the test run finishes and the detailed report is generated, ask the user whether to post the report as a PR comment when the run is associated with a GitHub PR (`prNumber` is known). Default the prompt to **Yes**, but do not post anything without explicit user confirmation.

Use whatever interactive prompt mechanism is available in the current agent framework. The prompt should make the default clear, for example:

```markdown
Post this test report as a comment on PR #12345?

Default: Yes
```

If the user confirms, post the report:

```bash
gh pr comment "$prNumber" --repo microsoft/aspire --body-file "$reportPath"
```

If the user declines, or if no GitHub PR was resolved, do not post the report. Include the posting status in the final response.

## Error Handling

### Version Mismatch
If the installed CLI version doesn't match the PR's head commit:
```markdown
## ❌ Version Mismatch Detected

- **Expected (PR head):** abc123def456...
- **Installed CLI reports:** xyz789...

**Possible causes:**
1. PR has new commits since the dogfood artifacts were built
2. Artifact cache is stale
3. Installation picked up a different version

**Recommendation:** Wait for CI to rebuild artifacts for the latest commit, then retry.
```

### Missing Dogfood Comment
If no "Dogfood this PR" comment is found:
```markdown
## ❌ No Dogfood Instructions Found

The PR does not have a "Dogfood this PR with:" comment.

**Possible causes:**
1. PR CI hasn't completed yet
2. PR is a draft or not from a branch that triggers artifact builds
3. CI failed to publish artifacts

**Recommendation:** Check the PR's CI status and wait for it to complete.
```

Exception: for PRs that only change `extension/`, missing dogfood instructions do not block testing. Test the PR branch source or VSIX instead and record the source checkout commit as the artifact version.

### Bundle extraction or layout validation failure
If a fresh PR install fails with messages like `Bundle extraction failed` or `Bundle was extracted ... but layout validation failed`:

1. Capture the exact error output and treat it as a CLI install or bundle failure.
2. Stop template-based scenarios and report the failure instead of adding repair steps that a normal user would not perform.
3. Only reach for deeper recovery or debugging steps if the user explicitly asks you to investigate the install or bundle failure itself.

### Unexpected prompt during automation
If `aspire new` fails with `Failed to read input in non-interactive mode` or `Cannot show selection prompt since the current terminal isn't interactive`, review the non-interactive flags listed in the "Important template note" section above (Step 3) and ensure they are all present. When encountering an unknown prompt, run `aspire new <template> --help` to discover all available flags. New template options may be added between releases and each one can introduce a new interactive prompt.

### gh CLI authentication failures during local mode
If the install script fails with `Failed to get HEAD SHA for PR` or `To get started with GitHub CLI, please run: gh auth login` even though `gh` is authenticated:

1. **Do not override `HOME`, `USERPROFILE`, or `APPDATA`** in the same shell session where you run the install script. The `gh` CLI resolves its auth config from `APPDATA` (Windows) or `HOME` (Unix), and overriding these to an isolated directory makes `gh` appear unauthenticated.
2. On Windows, use `-InstallPath`, `-SkipPath`, and `-SkipExtension` flags instead of environment variable overrides to isolate the install.
3. On Unix, use `--install-path`, `--skip-path`, and `--skip-extension` flags instead of overriding `HOME`.
4. Verify with `gh auth status` in the same terminal before running the install script.

### AppHost selection prompt / no running AppHosts found
If `wait`, `describe`, `resource`, or `stop` prompts to select an AppHost or reports that no running AppHosts were found in the current directory, pass `--apphost <path>` explicitly to those follow-up commands.

### VS Code extension setup or E2E failures
If extension validation fails before the scenario runs:
1. Capture the failing command and exact output.
2. For Corepack/Yarn registry errors, report it as environment or feed setup unless the PR changed `extension/package.json`, `extension/yarn.lock`, `.npmrc`, or build scripts.
3. For `test:e2e` failures, include the diagnostics paths printed by `scripts/run-e2e.js`. The runner stores state, control files, storage diagnostics, workspaces, and recordings under `extension/.test-results`, `.test-storage`, `.test-workspaces`, and `.test-recordings`.
4. If `ASPIRE_EXTENSION_E2E_CLI_PATH` is missing or points at the wrong binary, rebuild with `extension/build.sh` / `extension/build.ps1` or set the variable to the intended dogfood/published CLI and rerun.
5. If VS Code fails before tests start with an IPC socket warning like `IPC handle ... is longer than 103 chars` or `listen EINVAL`, move the checkout to a shorter path and rerun. Treat this as an environment path-length failure, not a PR failure.

### Test Scenario Failures
Document failures with full context:
```markdown
### Scenario: [Name]
**Status:** ❌ Failed

**Error:**
\```
[Full error output]
\```

**Screenshot:** error-state.png

**Logs:** 
- Console output: [relevant lines]
- Stack trace: [if applicable]

**Analysis:**
[What likely caused this failure]

**Impact:**
[How this affects users of the PR changes]
```

### 12. Offer Container Inspection

If testing ran in the repo container runner, ask the user before cleanup whether they want to keep the mounted workspace around for inspection. Use whatever interactive prompt mechanism is available in the current agent framework.

Default to cleaning up the container workspace.

If the user chooses to keep it:
- Do **not** delete `testDir`.
- Report the workspace path.
- Include the exact command to reopen a shell in a fresh runner container against the same workspace, for example:

```bash
runner bash
```

or, if you are no longer in the same shell context:

```bash
ASPIRE_PR_WORKSPACE="$testDir" ASPIRE_CONTAINER_USER=0:0 \
  ./eng/scripts/aspire-pr-container/run-aspire-pr-container.sh bash
```

On Windows PowerShell hosts, the reopen command is:

```powershell
$env:ASPIRE_PR_WORKSPACE = $testDir
$env:ASPIRE_CONTAINER_USER = "0:0"
./eng/scripts/aspire-pr-container/run-aspire-pr-container.ps1 bash
```

If the user chooses cleanup:
- Remove `testDir` as usual.

## Cleanup

After testing completes, clean up temporary directories unless the user explicitly chose to keep the container workspace for inspection:

**PowerShell:**

```powershell
# Return to original directory
Set-Location $env:USERPROFILE

# Clean up test directories
Remove-Item -Path $testDir -Recurse -Force
```

**bash:**

```bash
cd ~
rm -rf "$testDir"
```

## Platform Considerations

### Windows
- Use PowerShell for commands
- PR CLI installation: use `get-aspire-cli-pr.ps1` as shown in the dogfood comment (see Step 3)
- Path separator: `\`

### Linux/macOS
- Use bash for commands
- PR CLI installation: use `get-aspire-cli-pr.sh` as shown in the dogfood comment (see Step 3)
- Path separator: `/`
- Source profile after installation: `source ~/.bashrc` or `source ~/.zshrc`

## Response Format

After completing the task, provide:

1. **Brief Summary** - One-line result (Passed/Failed with key finding)
2. **Full Report** - The detailed markdown report as described above
3. **Artifacts** - List of captured screenshots and logs with their locations
4. **PR Comment Status** - Whether the report was posted to the PR, declined, skipped, or failed to post
5. **Cleanup / Inspection Status** - Whether the temp workspace was removed or retained for inspection, plus the reopen command when retained

Example summary:
```markdown
## PR Testing Complete

**Result:** ✅ PR #12345 verified successfully

All 3 test scenarios passed. The CLI changes in `NewCommand.cs` work as expected. 
Dashboard correctly displays the new Redis resource type.

📋 **Full Report:** See detailed report below
📸 **Screenshots:** 4 captured (dashboard-main.png, redis-resource.png, ...)
📝 **Logs:** 3 captured (run-output.txt, version.txt, ...)
💬 **PR Comment:** Posted to PR #12345
🧪 **Inspection:** Container workspace cleaned up
```

## Important Constraints

- **Always use temp directories** - Never create test projects in the repository
- **Verify version first** - Don't proceed with testing if CLI version doesn't match PR commit
- **Capture evidence** - Every scenario needs screenshots and/or logs
- **Clean up after** - Remove temp directories when done, unless the user explicitly asked to keep the container workspace for inspection
- **Document everything** - Detailed reports help PR authors understand results
- **Test actual changes** - Focus scenarios on what the PR modified
- **Include unhappy-path cases** - Add targeted negative/boundary scenarios for changed user-facing behavior, and verify expected safe failures or recovery states
- **Ask before posting** - If there is a GitHub PR, ask before posting the report as a PR comment; default to yes, but require explicit confirmation
- **Fresh projects** - Always use `aspire new` for each scenario, don't reuse projects
- **Container mode** - Prefer the repo-local `./eng/scripts/aspire-pr-container` scripts and a fresh temp workspace when Docker is available
- **Ask before container cleanup** - At the end of a container-mode run, ask whether to keep the mounted workspace around for inspection
- **Bundle failures** - If template-based commands fail with bundle extraction or layout validation errors after install, capture and report the failure instead of adding non-standard repair steps
- **Non-interactive project creation** - Follow the flags listed in the "Important template note" section (Step 3)
- **Explicit AppHost path** - Prefer `--apphost <path>` for scripted `wait`, `describe`, `resource`, and `stop` commands
- **PR hive for templates** - Prefer `--source <pr-hive>` and `--version <installed-version>` when creating projects from the PR build
- **CI infra PRs** - When the PR changes `.github/**`, `eng/pipelines/**`, `eng/common/**`, or CI scripts under `eng/`, follow `ci-infra-testing.md` (GitHub Actions track + Azure DevOps track): enumerate every affected workflow/pipeline, validate the workflows/actions/scripts/pipelines and hunt for trigger/permission/fork/portability/lock-drift gotchas, and for AzDO run def-1602 on the `dnceng/internal` mirror via the `azdo-internal` skill — don't dogfood the CLI for infra-only PRs, and don't test skill/doc-only edits at all
