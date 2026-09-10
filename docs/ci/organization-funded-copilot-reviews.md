# Organization-funded Copilot reviews

The `Organization-funded Copilot reviews` workflow requests Copilot code review
(CCR) using the repository's `GITHUB_TOKEN`, not a contributor's personal token.
It covers open PRs (including drafts) targeting `main` or `release/**`, including forks
and bot-authored PRs. There is no author permission or license filter.

**The default is dry-run. Merging this workflow does not enable billable review
requests or change any GitHub rulesets.**

## Controls

Set repository variables under **Settings > Secrets and variables > Actions >
Variables**. No follow-up PR is needed to change modes.

| Variable | Value | Effect |
| --- | --- | --- |
| `COPILOT_REVIEW_MODE` | Unset or `dry-run` | Read metadata and report decisions; never request reviews. |
| `COPILOT_REVIEW_MODE` | `enabled` | Request reviews using the Actions bot. |
| `COPILOT_REVIEW_MODE` | `disabled` | Skip the job (kill switch). |
| `COPILOT_REVIEW_PR_NUMBER` | Unset | Consider all eligible PRs, including PRs opened before rollout. |
| `COPILOT_REVIEW_PR_NUMBER` | A positive PR number | Restrict both event-driven and scheduled processing to that PR for a pilot. |

Invalid settings fail rather than enabling writes or broadening the pilot.
Variables are read when a run starts. Disabling the workflow does not cancel an
already-running job or a review already requested: cancel the active workflow
run separately when stopping an incident.

## Trigger and reconciliation behavior

PR creation and new pushes trigger a metadata-only `pull_request_target` run,
including for draft PRs. Reopening a PR or marking a draft ready does not trigger
an additional run. Manual dispatch on `main` and a scheduled
scan every 15 minutes reconcile open PRs. GitHub can delay scheduled runs.

Scheduled scans skip PRs with no activity in the last 14 days, using GitHub's
`updated_at` timestamp (not the PR creation date or latest commit date). The
cutoff is inclusive: an update exactly 14 days before the run is stale. The
summary reports `Skipped: no PR activity in the last 14 days.` before fetching
review history. This applies in dry-run and enabled modes, including a scoped
pilot. It does not close PRs or dismiss existing reviews.

Activity reflected in `updated_at`, including automated updates, makes an old
PR eligible again. Missing or invalid timestamps fail the run rather than
silently treating a PR as active. Push-triggered and manual runs do not apply
this staleness filter, so a maintainer can still manually reconcile an old PR.

All runs share one concurrency group without canceling the active run. GitHub
can replace pending runs; scheduled scanning recovers missed events. The scan
also handles PR retargeting and pushes made while Copilot is still reviewing.
Reopened PRs remain eligible for this scan, but reopening alone does not cause
another review of an already-reviewed head. Maintainers can manually request a
review when reopening a PR if one is needed immediately.
Each PR's current state is fetched from the API, not trusted from an old event.
Both open PR enumeration and review history are paginated.

A pending Copilot request defers processing. Once it completes, the next scan
requests a review if the current head SHA has not already been reviewed by
Copilot. Reviews by humans do not count. A dismissed Copilot review still counts
for that SHA. This is a review of the latest pushed head, not a separate review
of every intermediate commit in a multi-commit push.

The workflow re-fetches PR state immediately before requesting a review. GitHub
does not provide a transaction or idempotency key tying a review request to a
particular head SHA. A concurrent push or a review requested outside this
workflow can still race the API call. Scheduled reconciliation repairs missed
heads; it cannot guarantee exactly-once billing.

API errors fail the run visibly. Review-request POSTs are not automatically
retried because a timeout may occur after GitHub has accepted the request. A
later scan reads pending requests and completed reviews before requesting again.
A stuck pending request requires maintainer investigation; the workflow never
removes someone else's review request to force a retry.

## Security boundary

- The privileged workflow does not check out source, execute PR code, load local
  actions, install packages, or consume artifacts or caches.
- The only action is a full-SHA-pinned `actions/github-script`. All logic is
  inline so even loading policy code does not require checkout.
- The only token permission is `pull-requests: write`. The job uses an ephemeral
  GitHub-hosted runner with a ten-minute timeout. No App private key or PAT is
  used.
- Repository and reviewer identities are fixed. PR-controlled values are API
  data, never interpolated into scripts or used as API URLs. Logs and summaries
  include only validated PR numbers, SHAs, and fixed decision messages.
- Runs outside `microsoft/aspire` are rejected. Manual dispatch must use `main`.
  Protect `main` and allowed release branches and require trusted review of
  workflow changes: `pull_request_target` executes the base branch's workflow.
- This job only requests reviews. It does not approve, merge, push fixes, or run
  suggestions. CCR's own runner/setup and secret configuration is a separate
  security boundary that must be assessed before the pilot.

Dry-run uses the same token permissions but never calls the write endpoint.
The single-PR scope and kill switch are rollout controls, not a hard spending
limit. Once enabled for everyone, contributors can generate organization-paid
reviews by pushing changes. Configure organization budgets/alerts and monitor
usage; reconsider cadence if this becomes expensive or is abused.

## Billing and rollout

[GitHub's CCR documentation](https://docs.github.com/en/copilot/concepts/agents/code-review#code-review-usage)
attributes built-in automatic reviews to the author and explicitly states that
bot-requested reviews are billed directly to the organization.
[`GITHUB_TOKEN`](https://docs.github.com/en/actions/concepts/security/github_token)
is an installation token, not the identity of the contributor triggering the
workflow. GitHub documents
[requesting the Copilot reviewer through REST](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review).
These rules support this design, but actual execution and billing under
Microsoft's policies must be confirmed before broad rollout.

1. Merge in dry-run mode and inspect workflow decisions. No production billing
   settings or automatic review rules are changed by this PR.
2. Identify applicable repository and organization automatic CCR rules,
   including "Review new pushes." Disable those paths before enabling writes,
   or they can still create author-attributed or duplicate reviews.
3. Set `COPILOT_REVIEW_PR_NUMBER` to an agreed pilot PR. Assess CCR's downstream
   runner permissions and confirm organization funding/budget policies.
4. Set `COPILOT_REVIEW_MODE=enabled`. Confirm that the Actions bot starts a
   review, then push another commit and confirm a re-review, including a push
   during an active review. Include an external contributor in the pilot.
5. Have a billing administrator confirm organization attribution and no
   contributor allowance consumption for these bot-requested reviews. An API
   success or a posted review alone is not proof of billing attribution.
6. Clear the pilot variable to cover all eligible open PRs. Keep human approval
   requirements unchanged and monitor spend and failed workflow runs.

If `GITHUB_TOKEN` cannot initiate CCR under organization policy, leave writes
disabled until that failure is understood. An Aspire bot App installation token
is a possible follow-up, scoped to this repository with Pull requests: write.
Do not substitute a personal token, silently escalate permissions, or claim
organization billing based only on the human who triggered a workflow run.

Personal automatic-review settings and manual requests are outside this
workflow's control and retain their own billing attribution. Actions usage for
CCR's agentic capabilities is also separate from the AI credits for the review.
Rollback is `COPILOT_REVIEW_MODE=disabled`; do not automatically restore
author-attributed rulesets.
