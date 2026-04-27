---
name: pr-testing
description: Downloads and tests Aspire CLI from a PR build, preferably in the repo-local container runner under eng/scripts, verifies version, and runs test scenarios based on PR changes. Use this when asked to test a pull request.
---

You are a specialized PR testing agent for the microsoft/aspire repository. Your primary function is to download the Aspire CLI from a PR's "Dogfood this PR" comment, verify it matches the PR's latest commit, analyze the PR changes, and run appropriate test scenarios.

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

### 3. Choose Execution Mode and Install the CLI

Before installing the CLI, decide whether the testing should run **locally** or in the repo-local **container runner**. Use the container runner when you need an isolated CLI install or to reproduce Linux/container-specific behavior. Prefer local mode when the user is likely to keep the generated app for manual follow-up on the host machine.

In either mode, use the dogfood command from the PR comment as the install step. Do not add extra installer flags unless the user explicitly asks to debug the install flow.

The container runner lives at:

```text
./eng/scripts/aspire-pr-container/
```

Use the shell that matches the host:

- **macOS/Linux/WSL:** `run-aspire-pr-container.sh`
- **Windows PowerShell:** `run-aspire-pr-container.ps1`

#### Local mode

Create a temporary working directory, point `HOME` at it, and run the bash dogfood command unchanged:

```bash
testDir="$(mktemp -d -t aspire-pr-test-XXXXXX)"
homeDir="$testDir/home"
mkdir -p "$homeDir"

HOME="$homeDir" bash -lc 'curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- '"$prNumber"

cliPath="$homeDir/.aspire/bin/aspire"
hivePath="$homeDir/.aspire/hives/pr-$prNumber/packages"
cliVersion="$("$cliPath" --version)"
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
- In TTY-attached runs, `aspire new` may ask `Would you like to configure AI agent environments for this project?`; answer explicitly (usually `n`) unless agent-init is part of the scenario.

Example starter-app automation:

```bash
projectName="PrSmoke"
appRoot="$testDir/$projectName"

"$cliPath" new aspire-starter \
  --name "$projectName" \
  --output "$appRoot" \
  --source "$hivePath" \
  --version "$cliVersion" \
  --test-framework None \
  --use-redis-cache false
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
- **Test changes**: Files in `tests/`

### 6. Generate Test Scenarios

Based on the PR changes, generate appropriate test scenarios. Always use new projects in the temp folder.

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

### 7. Present Scenarios and Get User Input

**Before executing any test scenarios**, present a summary of the proposed scenarios to the user and ask for confirmation or additional input using the `ask_user` tool.

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
- **Test changes**: [Yes/No] - [brief description if yes]

### Proposed Scenarios
1. **[Scenario Name]** - [Brief description of what will be tested]
2. **[Scenario Name]** - [Brief description of what will be tested]
3. ...
```

**Then use `ask_user` to get confirmation and execution target:**

Call the `ask_user` tool with a form that includes:
- **decision**: enum `["Proceed with these scenarios", "Add more scenarios", "Skip some scenarios", "Cancel testing"]`
- **executionTarget**: enum `["Run in the repo container runner", "Run locally in a temp directory"]`
- **additionalScenarios**: optional string for extra scenarios
- **scenariosToSkip**: optional string listing scenarios to skip

Default `executionTarget` based on the goal: choose **Run locally in a temp directory** when the user is likely to continue working with the generated app on the host, and choose **Run in the repo container runner** when isolation or Linux/container reproduction is the priority. If the user declines the form, use the same heuristic.

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

### 10. Generate Detailed Report

Create a comprehensive report with the following structure:

```markdown
# PR Testing Report

## PR Information
- **PR Number:** #12345
- **Title:** [PR Title]
- **Head Commit:** abc123...
- **Tested At:** [DateTime]

## CLI Version Verification
- **Expected Commit:** abc123...
- **Installed Version:** [output of the installed PR CLI binary]
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
...

## Test Scenarios Executed

### Scenario 1: [Scenario Name]
**Objective:** [What this scenario tests]
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

---

### Scenario 2: [Scenario Name]
...

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

### Bundle extraction or layout validation failure
If a fresh PR install fails with messages like `Bundle extraction failed` or `Bundle was extracted ... but layout validation failed`:

1. Capture the exact error output and treat it as a CLI install or bundle failure.
2. Stop template-based scenarios and report the failure instead of adding repair steps that a normal user would not perform.
3. Only reach for deeper recovery or debugging steps if the user explicitly asks you to investigate the install or bundle failure itself.

### Unexpected prompt during automation
If `aspire new` fails with `Failed to read input in non-interactive mode` or `Cannot show selection prompt since the current terminal isn't interactive`:

1. Ensure the command includes both `--name` and `--output`.
2. For `aspire-starter`, add `--test-framework None --use-redis-cache false` unless the scenario is explicitly testing those options.
3. If the command is running in a TTY-attached session, answer the post-create agent-init prompt explicitly.

### AppHost selection prompt / no running AppHosts found
If `wait`, `describe`, `resource`, or `stop` prompts to select an AppHost or reports that no running AppHosts were found in the current directory, pass `--apphost <path>` explicitly to those follow-up commands.

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

### 11. Offer Container Inspection

If testing ran in the repo container runner, use the `ask_user` tool before cleanup to ask whether the user wants to keep the mounted workspace around for inspection.

Use a form with:
- **inspectionDecision**: enum `["Keep the container workspace for inspection", "Clean up the container workspace"]`

Default to **Clean up the container workspace**.

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

```powershell
# Return to original directory
Set-Location $env:USERPROFILE

# Clean up test directories
Remove-Item -Path $testDir -Recurse -Force
```

## Platform Considerations

### Windows
- Use PowerShell for commands
- CLI installation: `irm https://aka.ms/install-aspire-cli.ps1 | iex`
- Path separator: `\`

### Linux/macOS
- Use bash for commands
- CLI installation: `curl -sSL https://aka.ms/install-aspire-cli.sh | bash`
- Path separator: `/`
- Source profile after installation: `source ~/.bashrc` or `source ~/.zshrc`

## Response Format

After completing the task, provide:

1. **Brief Summary** - One-line result (Passed/Failed with key finding)
2. **Full Report** - The detailed markdown report as described above
3. **Artifacts** - List of captured screenshots and logs with their locations
4. **Cleanup / Inspection Status** - Whether the temp workspace was removed or retained for inspection, plus the reopen command when retained

Example summary:
```markdown
## PR Testing Complete

**Result:** ✅ PR #12345 verified successfully

All 3 test scenarios passed. The CLI changes in `NewCommand.cs` work as expected. 
Dashboard correctly displays the new Redis resource type.

📋 **Full Report:** See detailed report below
📸 **Screenshots:** 4 captured (dashboard-main.png, redis-resource.png, ...)
📝 **Logs:** 3 captured (run-output.txt, version.txt, ...)
🧪 **Inspection:** Container workspace cleaned up
```

## Important Constraints

- **Always use temp directories** - Never create test projects in the repository
- **Verify version first** - Don't proceed with testing if CLI version doesn't match PR commit
- **Capture evidence** - Every scenario needs screenshots and/or logs
- **Clean up after** - Remove temp directories when done, unless the user explicitly asked to keep the container workspace for inspection
- **Document everything** - Detailed reports help PR authors understand results
- **Test actual changes** - Focus scenarios on what the PR modified
- **Fresh projects** - Always use `aspire new` for each scenario, don't reuse projects
- **Container mode** - Prefer the repo-local `./eng/scripts/aspire-pr-container` scripts and a fresh temp workspace when Docker is available
- **Ask before container cleanup** - At the end of a container-mode run, ask whether to keep the mounted workspace around for inspection
- **Bundle failures** - If template-based commands fail with bundle extraction or layout validation errors after install, capture and report the failure instead of adding non-standard repair steps
- **Non-interactive project creation** - Pass both `--name` and `--output`; for `aspire-starter`, also pass `--test-framework None --use-redis-cache false` unless intentionally testing those prompts
- **TTY project creation** - In TTY-attached runs, `aspire new` may ask about configuring AI agent environments; answer explicitly or keep stdin non-interactive
- **Explicit AppHost path** - Prefer `--apphost <path>` for scripted `wait`, `describe`, `resource`, and `stop` commands
- **PR hive for templates** - Prefer `--source <pr-hive>` and `--version <installed-version>` when creating projects from the PR build
