# Auto-rerun outerloop failures

This document describes the workflow that automatically reruns failed
[Outerloop Tests](../../.github/workflows/tests-outerloop.yml) runs.

## How it works

When an `Outerloop Tests` run finishes with a failure, the
[`auto-rerun-outerloop-failures.yml`](../../.github/workflows/auto-rerun-outerloop-failures.yml)
workflow asks GitHub to rerun the failed jobs. It keeps doing this for up to 3
reruns (4 total attempts), then stops.

The rerun is **unconditional**: there is no failure analysis, no log/test
pattern matching, and no pull-request comment. The scheduled outerloop runs this
targets have no associated PR, so a failed run is simply rerun as-is until the
attempt cap is reached.

`tests-outerloop.yml` also has a narrow `pull_request` paths trigger (for changes
to a few CI-orchestration workflow files). Those PR-triggered runs are
**excluded** from auto-rerun (`workflow_run.event != 'pull_request'`): a human is
watching the PR and can use GitHub's **Re-run failed jobs** button, and
auto-rerunning would mask genuine breakage in exactly the PRs that change
outerloop CI.

The whole workflow is the eligibility `if` plus a single `rerun-failed-jobs` API
call — there is no script logic to maintain. This is deliberately separate from
the PR-facing
[auto-rerun-transient-ci-failures](auto-rerun-transient-ci-failures.md) workflow,
which classifies failures and posts PR comments: that one's unconditional
behavior is a temporary `FORCE_RERUN_ALL` measure and its normal path is
PR-shaped, whereas outerloop's unconditional rerun is the permanent intent.

```text
Outerloop Tests run fails
        │
        ▼
┌────────────────────────────────────────┐
│  Eligible? (all in the job `if`)       │
│  • conclusion == 'failure'            │
│  • run_attempt <= 3                   │
│  • event != 'pull_request'            │
│  • repository_owner == 'microsoft'    │
└──────────────┬─────────────────────────┘
               │ yes
               ▼
   POST rerun-failed-jobs for the run
```

There is intentionally no `workflow_dispatch` path: GitHub's UI already provides
a **Re-run failed jobs** button on any run for manual reruns.

## Safety rails

All rails live in the job-level `if`, so the rerun step only runs when every
condition holds:

| Rail | Detail |
|------|--------|
| **Attempt limit** | The run must be on attempt ≤ 3. Reruns fire from attempts 1, 2, and 3, so a run gets up to 3 automatic reruns (4 total attempts). |
| **Failure-only triggering** | Only fires on `workflow_run.conclusion == 'failure'`. A `cancelled` run never triggers a rerun. |
| **Scheduled/manual only** | Only fires when the outerloop run's trigger was not `pull_request` (`workflow_run.event != 'pull_request'`). PR-triggered outerloop runs (narrow paths filter) are left for the PR author to rerun manually. |
| **Repository guard** | Only runs on `microsoft/aspire` (`github.repository_owner == 'microsoft'`). |

GitHub's `rerun-failed-jobs` API reruns **all** failed jobs for the attempt
(there is no API to rerun a subset), which is exactly the desired behavior here.

## Files

| File | Role |
|------|------|
| [`.github/workflows/auto-rerun-outerloop-failures.yml`](../../.github/workflows/auto-rerun-outerloop-failures.yml) | The workflow: trigger, eligibility gate, and the single rerun API call. |
| [`.github/workflows/tests-outerloop.yml`](../../.github/workflows/tests-outerloop.yml) | The `Outerloop Tests` workflow that this one watches. |
