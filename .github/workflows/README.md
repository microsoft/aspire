# GitHub Workflows

## Agentic workflow maintenance

Agentic workflows are authored in `.github/workflows/*.md`. Upgrade the active
compiler to the latest stable release and inspect its suggested migrations before
recompiling:

```shell
gh extension upgrade aw
gh aw version
gh aw fix
gh aw compile --force-refresh-action-pins --schedule-seed microsoft/aspire
gh aw compile --schedule-seed microsoft/aspire
```

`gh aw fix` is a dry run unless `--write` is supplied. Its write mode also refreshes
authoring agents and skills; apply source migrations deliberately rather than
adding unrelated scaffolding. Keep the pinned `setup-cli` action and its `version`
input in `copilot-setup-steps.yml` aligned with the compiler used to generate the
workflows and with `validate-agentic-workflows.yml`. Updating only the setup
action does not update the installed compiler; its `version` input controls that.

`recompile-agentic-workflows.yml` uses `pull_request: closed` to handle merged
upstream Dependabot PRs targeting the default branch, including grouped GitHub
Actions updates. Unmerged closures are skipped. It detects changed
`github/gh-aw-actions` pins from the PR diff and verifies their release comments
against upstream tag commits (or resolves an uncommented SHA through upstream
tags). It never selects a compiler from the PR title or the latest release.
It can also be run manually from the default branch using **Run workflow** or
`gh workflow run recompile-agentic-workflows.yml`. Manual dispatch selects the
newest release already pinned by `github/gh-aw-actions` references in the default
branch's workflow YAML, verifies those pins against upstream tags, and aligns both
compiler inputs before recompiling. It does not fetch the latest upstream release.
Manual runs reuse the `recompile-agentic-workflows-manual` branch and update its
open PR, refreshing its title and body to match the selected release; if there
are no changes, they open no PR.
The workflow checks out only the trusted default branch, skips superseded pins,
aligns both `setup-cli` pins and version inputs, and runs:

```shell
gh extension install github/gh-aw --pin <detected-version> --force
gh aw version
gh aw compile --no-check-update --schedule-seed microsoft/aspire
gh aw lint --shellcheck
```

It does not run `gh aw upgrade` or refresh authoring scaffolding. Compilation or
lint failures prevent publication, unexpected changed paths fail the run, and an
empty diff opens no PR. Aspirey creates or updates a stable branch per source PR;
reruns do not reopen a closed follow-up PR. Runs sharing a follow-up branch are
serialized: each source PR gets its own concurrency group, and manual dispatches
share a separate group. Unrelated PR closures cannot replace a queued gh-aw update;
a pending rerun can only be replaced by another run for the same source.
Version verification captures both stdout and stderr
because older gh-aw releases print their version to stderr.
The existing `ASPIRE_BOT_APP_ID` and `ASPIRE_BOT_PRIVATE_KEY` secrets are
required as both **Actions secrets** (manual runs) and **Dependabot secrets**
(Dependabot-triggered runs), using the same names. GitHub does not expose normal
Actions secrets to Dependabot-triggered `pull_request` workflows, even on a manual
rerun; see [GitHub's Dependabot secrets guidance](https://docs.github.com/en/code-security/reference/supply-chain-security/troubleshoot-dependabot/dependabot-on-actions#accessing-secrets).
The workflow explicitly grants `GITHUB_TOKEN` only Contents and Pull requests read
permissions; all writes use the separately minted App token. No privileged
`pull_request_target` trigger or dangerous-trigger suppression is needed.
The App installation must grant **Contents**, **Pull requests**, and
**Workflows** write permissions to push generated workflow YAML. Newly introduced
third-party action SHAs still require the repository/enterprise allow-list update
before the generated PR is merged.

Commit the generated `.lock.yml` files and `.github/aw/actions-lock.json` alongside
their source changes. `agentics-maintenance-microsoft-aspire.dev.yml` is generated
by the same full compilation despite not having a `.lock.yml` suffix. Do not edit
generated workflows manually. The second compilation should produce no further
changes.

Always pass `--schedule-seed microsoft/aspire` when generating workflows for this
repository. The literal `microsoft/aspire` string and each workflow's identifier
deterministically select a time for flexible schedules such as `daily around 9am`.
Without an explicit seed, gh-aw derives the repository identity from Git remotes;
forks or checkouts without a remote can generate a different cron expression than
CI. Two compilations in the same checkout can agree while still differing from CI.
The explicit seed preserves the canonical schedule regardless of checkout context.

`validate-agentic-workflows.yml` recompiles with the pinned gh-aw version, checks
for generated-file drift, runs `gh aw lint --shellcheck` as a blocking lint gate,
and runs the `Category=AgenticWorkflow` contracts in `Infrastructure.Tests`.
Class-level traits group generated-workflow, validation trigger/drift, and shared
process-runner tests without including unrelated negative-test diagnostics.
Apply this trait to new agentic contract classes so dedicated validation includes them.
Main CI covers selector routing when the trigger map or lint policy changes.
README-only changes do not trigger dedicated agentic validation; new Markdown
workflow sources still trigger it even before their generated locks exist.

Compile-time lint diagnostics alone are not a blocking gate. The lint command
owns the actionlint image and compatibility exceptions; `.github/actionlint.yaml`
is shared by both lint paths: it configures the runner label for handwritten
workflow linting and scopes the known stale-check output workaround to the
affected generated workflows. Main CI separately runs pinned core actionlint
over handwritten workflows and their local actions through
`lint-handwritten-workflows.sh`, which selects the files and excludes gh-aw output.
The version and linux_amd64 archive SHA-256 are pinned in `.github/actionlint-version.json`,
outside `.github/workflows`, so the Aspire bot can update them without workflow-write access.
The weekly `update-actionlint.yml` workflow opens a draft PR when a newer release exists. It
verifies the downloaded archive against the release API `sha256:` digest and lints the
handwritten workflows with the candidate before proposing the bump. That digest comes from the
same upstream release, so review the release notes; the committed hash then makes CI detect any
later substitution of the archive. Manual dispatch defaults to `validate`, which does not change
the updater branch or open a PR and is safe to run from a fork; `propose` runs only from the
upstream repository's default branch and follows the scheduled PR-update path when a newer release
is available. Scheduled and `propose` runs are serialized end to end by a shared concurrency
group, so each run picks its release only after the previous run has pushed.

Explicit action versions in Markdown survive recompilation, so update deprecated
inputs and action runtimes in the sources, not just the generated YAML. The
`client-id` input to `actions/create-github-app-token` replaces `app-id`; the
existing `ASPIRE_BOT_APP_ID` secret remains the identity source.

Action upgrades must update Markdown references before regenerating locks and
the action-pin cache. A generated-only pin update is not reproducible and the
drift check rejects it, even if the changed action versions are otherwise valid.

The compiler's diagnostic `agent` artifact does not include arbitrary files from
`/tmp/gh-aw/agent/`. Workflows that publish custom agent files must upload a named
artifact in `post-steps`, allowlist only the required paths, and download it in the
consuming safe-output job. CI analysis and milestone changelogs use this pattern;
their canonical safe-output JSON remains compiler-managed.

`locker.yml` still uses the archived `microsoft/vscode-github-triage-actions`
Node 20 action. There is no supported Node 24 upgrade for that dependency;
replacing it requires a separate migration of its authentication and locking
behavior, rather than merely changing an action pin.

## Quarantine/Disable Test Workflow

The `apply-test-attributes.yml` workflow allows repository maintainers to quarantine, unquarantine, disable, or enable tests directly from issue or PR comments.

### Commands

| Command | Description | Attribute Used |
|---------|-------------|----------------|
| `/quarantine-test` | Mark test(s) as quarantined (flaky) | `[QuarantinedTest]` |
| `/unquarantine-test` | Remove quarantine from test(s) | Removes `[QuarantinedTest]` |
| `/disable-test` | Disable test(s) due to an active issue | `[ActiveIssue]` |
| `/enable-test` | Re-enable previously disabled test(s) | Removes `[ActiveIssue]` |

### Syntax

```
/quarantine-test <test-name(s)> <issue-url> [--target-pr <pr-url>]
/unquarantine-test <test-name(s)> [--target-pr <pr-url>]
/disable-test <test-name(s)> <issue-url> [--target-pr <pr-url>]
/enable-test <test-name(s)> [--target-pr <pr-url>]
```

### Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `<test-name(s)>` | Yes | One or more test method names (space-separated) |
| `<issue-url>` | For quarantine/disable | URL of the GitHub issue tracking the problem |
| `--target-pr <pr-url>` | No | Push changes to an existing PR instead of creating a new one |

### Examples

#### Quarantine a flaky test (creates new PR)
```
/quarantine-test MyTestClass.MyTestMethod https://github.com/microsoft/aspire/issues/1234
```

#### Quarantine multiple tests
```
/quarantine-test TestMethod1 TestMethod2 TestMethod3 https://github.com/microsoft/aspire/issues/1234
```

#### Quarantine a test and push to an existing PR
```
/quarantine-test MyTestMethod https://github.com/microsoft/aspire/issues/1234 --target-pr https://github.com/microsoft/aspire/pull/5678
```

#### Unquarantine a test (creates new PR)
```
/unquarantine-test MyTestClass.MyTestMethod
```

#### Unquarantine and push to an existing PR
```
/unquarantine-test MyTestMethod --target-pr https://github.com/microsoft/aspire/pull/5678
```

#### Disable a test due to an active issue
```
/disable-test MyTestMethod https://github.com/microsoft/aspire/issues/1234
```

#### Enable a previously disabled test
```
/enable-test MyTestMethod
```

#### Comment on a PR to push changes to that PR
When you comment on a PR (not an issue), the workflow will automatically push changes to that PR's branch instead of creating a new PR. You can override this by specifying `--target-pr`.

### Behavior

1. **Permission Check**: Only users with write access to the repository can use these commands.
2. **Processing Indicator**: The workflow adds an 👀 reaction to your comment when it starts processing.
3. **Status Comments**: The workflow posts comments to indicate:
   - ⏳ Processing started
   - ✅ Success (with link to created/updated PR)
   - ℹ️ No changes needed (test already in desired state)
   - ❌ Failure (with error details)

### Target PR Behavior

| Context | `--target-pr` specified | Result |
|---------|-------------------------|--------|
| Comment on Issue | No | Creates new PR from `main` |
| Comment on Issue | Yes | Pushes to specified PR |
| Comment on PR | No | Pushes to that PR's branch |
| Comment on PR | Yes | Pushes to specified PR (overrides) |

### Restrictions

- The `--target-pr` URL must be from the same repository
- Cannot push to PRs from forks
- Cannot push to closed PRs
- The PR branch must not be protected in a way that prevents pushes

### Concurrency

The workflow uses concurrency groups based on the issue/PR number to prevent race conditions when multiple commands are issued on the same issue.

## Backmerge Release Workflow

The `backmerge-release.yml` workflow automatically creates PRs to merge changes from `release/13.3` back into `main`.

### Schedule

Runs daily at 00:00 UTC (4pm PT during standard time, 5pm PT during daylight saving time). Can also be triggered manually via `workflow_dispatch`.

### Behavior

1. **Change Detection**: Checks if `release/13.3` has commits not in `main`
2. **PR Creation**: If changes exist, creates a PR to merge `release/13.3` → `main`
3. **Auto-merge**: Enables GitHub's auto-merge feature, so the PR merges automatically once approved
4. **Conflict Handling**: If merge conflicts occur, creates an issue instead of a PR

### Assignees

PRs and conflict issues are automatically assigned to @joperezr and @radical.

### Manual Trigger

To trigger manually, go to Actions → "Backmerge Release to Main" → "Run workflow".
