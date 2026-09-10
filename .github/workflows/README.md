# GitHub Workflows

## Main to Release 14.0 Synchronization

`sync-main-to-release-14.yml` keeps the advance `release/14.0` integration branch
up to date while `main` develops 13.6. It runs daily at **08:23 UTC** and can be
dispatched manually, but only from `main` in `microsoft/aspire`.

The workflow uses the existing Aspire App secrets (`ASPIRE_BOT_APP_ID` and
`ASPIRE_BOT_PRIVATE_KEY`) with repository-scoped contents and pull-request write
permissions. It only calls GitHub APIs; it never checks out or executes branch
code with the bot token.

Each batch creates a `sync/main-to-release-14.0/<main-sha>` branch pointing at a
snapshot of `main`, then opens a PR into `release/14.0`. There is at most one open
sync PR. An open PR is reconciled rather than replaced or force-pushed, preserving
manual conflict resolutions and avoiding CI churn. Commits arriving on `main`
while that PR is open are picked up by the next run after it merges.

### Auto-merge prerequisites

- Enable **Allow merge commits** and **Allow auto-merge** in repository settings.
- Allow merge commits in every ruleset applying to `release/14.0`. A separate
  `main` ruleset can continue requiring squash merges for normal feature PRs.
- Ensure branch push restrictions allow the Aspire App to merge. Required status
  checks and reviews are respected, not bypassed or self-approved by the bot.

With an approval requirement, a maintainer still needs to approve each PR; it
then merges automatically when the remaining requirements pass. Fully unattended
merges also require a deliberate branch-policy decision about reviews. The
workflow does not change any repository settings or branch protections.

To exempt only the App from approvals, use an approval-only ruleset targeting
exactly `release/14.0`, with the Aspire App in its bypass list using **For pull
requests only**. Keep required CI (including `Final Results`) in a **separate**
ruleset without an App bypass. Keep force-push and deletion protections outside
the bypassable ruleset too. Remove or adjust overlapping approval requirements,
including classic branch protection: a more permissive ruleset does not override
another rule. The bypass is granted to the merging App, not to a PR author; this
workflow additionally verifies the PR's bot author, repository, branch prefix,
and workflow marker before requesting a merge.

Native auto-merge is intentionally the only deferred merge mechanism here.
There are [reports that App approval bypasses are not honored by native
auto-merge](https://github.com/orgs/community/discussions/190610), even when the
normal merge API honors them. If a sync PR remains blocked on approval after CI
passes, approve it manually; a CI-completion merge handler can be considered
later. Do not solve that problem by giving the App permission to bypass CI.
See GitHub's [bypass configuration](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository#granting-bypass-permissions-for-your-branch-or-tag-ruleset)
and [rule layering](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets#about-rule-layering)
documentation.

If repository merge commits or auto-merge are disabled, the workflow leaves the
PR open, warns in the run summary, and retries on the next run. Conflicts likewise
leave an actionable PR open. An already-ready PR is merged normally with a
merge commit and an expected-head check, without an administrative bypass. If
GitHub has not computed mergeability after a short retry window, rerun the
workflow from `main` or let the next daily run reconcile it. Other API or
permission failures fail the run and are covered by the scheduled-workflow watchdog.

### Resolving conflicts

Fetch and check out the sync PR's branch, merge `origin/release/14.0` into it,
resolve conflicts, and push to that same branch. Keep the **14.0** version in
`eng/Versions.props` when resolving version conflicts; other dependency changes
from `main` should still flow forward. The next run retries enabling auto-merge.

Never resolve these conflicts by merging `release/14.0` into `main`: doing so
would leak 14.0-only changes into 13.6. Always merge sync PRs using **merge commits**,
not squash or rebase, so Git can recognize already-integrated main commits.

### Ending the temporary branch arrangement

1. Create `release/13.6` from `main` for stabilization.
2. Disable this workflow in Actions, cancel any queued/active runs, and merge or
   close any outstanding sync PR before starting the reverse integration.
3. Merge `release/14.0` back into `main`, preserving its 14.0 version. Prefer a
   merge commit for this one-off integration too; a squash-only `main` ruleset
   needs a temporary exception if preserving that ancestry.
4. Remove the temporary synchronization workflow and its watchdog entry from
   `monitor-scheduled-workflows.config.json`. `main` now develops 14.0; 13.6 fixes
   can follow the normal release-branch backmerge process.

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
