# Organization-funded Copilot reviews

The `Organization-funded Copilot reviews` workflow requests Copilot code review
(CCR) using an installation token for `aspire-repo-bot`, not the workflow's
`GITHUB_TOKEN` or a contributor's personal token. Organization funding is the
goal, not a verified billing guarantee.
It covers open PRs (including drafts and forks) targeting `main` or `release/**`
only when the PR author currently has write, maintain, or admin access to
`microsoft/aspire`. There is no license filter or guarantee of available quota.

**The default is dry-run only when the mode variable is unset. If it is already
`enabled`, merging this change switches subsequent runs to the App immediately.
Set dry-run and a pilot PR before merging to stage the rollout.** No GitHub
rulesets are changed by the workflow.

## App prerequisites

The Aspire App must be installed on `microsoft/aspire` with Pull requests: write.
The workflow uses the existing `ASPIRE_BOT_APP_ID` and `ASPIRE_BOT_PRIVATE_KEY`
Actions secrets. The token action accepts the existing identifier via its
`client-id` input, matching other repository workflows.

Each run mints an installation token scoped explicitly to the `microsoft`
installation and the `aspire` repository. Only Pull requests access is requested:
write when the mode is exactly `enabled`, otherwise read. Token creation or
permission failures stop the job; there is no fallback to `GITHUB_TOKEN`.

## Controls

Set repository variables under **Settings > Secrets and variables > Actions >
Variables**. No follow-up PR is needed to change modes.

| Variable | Value | Effect |
| --- | --- | --- |
| `COPILOT_REVIEW_MODE` | Unset or `dry-run` | Read metadata and report decisions; never request reviews. |
| `COPILOT_REVIEW_MODE` | `enabled` | Request reviews using the Aspire App. |
| `COPILOT_REVIEW_MODE` | `disabled` | Skip the job (kill switch). |
| `COPILOT_REVIEW_PR_NUMBER` | Unset | Consider all eligible PRs, including PRs opened before rollout. |
| `COPILOT_REVIEW_PR_NUMBER` | A positive PR number | Restrict both event-driven and scheduled processing to that PR for a pilot. |

Invalid settings fail rather than enabling writes or broadening the pilot.
Variables are read when a run starts. Disabling the workflow does not cancel an
already-running job or a review already requested: cancel the active workflow
run separately when stopping an incident.

## Trigger and reconciliation behavior

All processing paths (PR events, scheduled scans, manual dispatch, and dry-run)
check the PR author's current effective repository permission through GitHub's
collaborator-permission API. Read, triage, and no-access authors are skipped,
including bot authors without write access. A maintainer pushing a commit to
someone else's PR or manually dispatching the workflow does not bypass this
gate. Organization membership and `author_association` are not used as proxies
for write access. Custom roles use the API's effective base permission, which
maps maintain to write and triage to read.

GitHub's coding-agent author identity (`login: Copilot`, `type: Bot`) is skipped
before the permission lookup: the collaborator endpoint rejects it with
`404: Copilot is not a user`. The PR receives the normal no-write-access skip
decision and scheduled scans continue to subsequent PRs. This exception requires
both the bot type and exact login; other authors still use the permission API.
Unrelated 404 responses and other lookup failures are not swallowed.

The permission is checked again after fetching review history and current PR
state, before a request (or dry-run decision). Lookup failures or unexpected
permission values fail visibly without requesting a review. GitHub offers no
atomic operation combining a permission check and review request, so a permission
change concurrent with the final request can still race this check.

PR creation and new pushes trigger a metadata-only `pull_request_target` run,
including for draft PRs. Reopening a PR or marking a draft ready does not trigger
an additional run. Manual dispatch on `main` and a scheduled
scan every 15 minutes reconcile open PRs. GitHub can delay scheduled runs.

`pull_request_target` runs initiated by `dependabot[bot]` skip the entire job,
before App token creation, because [Dependabot PR events can lack Actions
secrets](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-on-actions#restrictions-when-dependabot-triggers-events).
Scheduled scans and maintainer manual dispatches use their own execution
context and credentials, but still enforce the author-permission gate:
Dependabot PRs without repository write access are skipped there as well.
Do not copy the App private key into Dependabot secrets.

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
- Both actions (`actions/create-github-app-token` and `actions/github-script`)
  are full-SHA-pinned. All policy logic is inline, requiring no checkout.
- The workflow's `GITHUB_TOKEN` has no granted permissions. The App installation
  token is restricted to this repository and Pull requests read/write as
  described above, plus GitHub's mandatory metadata read access. The token action
  revokes it during job cleanup; installation tokens also expire after one hour.
- The App private key is passed only to the token action, not to the inline
  reconciliation script. Unlike the narrowed token, the private key can mint
  tokens with the App's broader installed permissions: protecting the secret
  and trusted workflow/runtime remains essential. No PAT is used. The job uses
  an ephemeral GitHub-hosted runner with a ten-minute timeout.
- Repository and reviewer identities are fixed. PR-controlled values are API
  data, never interpolated into scripts or used as API URLs. Logs and summaries
  include only validated PR numbers, SHAs, and fixed decision messages.
- Runs outside `microsoft/aspire` are rejected. Manual dispatch must use `main`.
  Protect `main` and allowed release branches and require trusted review of
  workflow changes: `pull_request_target` executes the base branch's workflow.
- This job only requests reviews. It does not approve, merge, push fixes, or run
  suggestions. CCR's own runner/setup and secret configuration is a separate
  security boundary that must be assessed before the pilot.

Dry-run uses a read-only App token and never calls the write endpoint. It still
requires the App secrets for token creation; disabled mode skips the entire job.
The single-PR scope and kill switch are rollout controls, not a hard spending
limit. Eligible authors can generate potentially billable reviews by pushing
changes. Configure appropriate budgets/alerts and monitor
usage; reconsider cadence if this becomes expensive or is abused.

## Billing and rollout

[GitHub's CCR documentation](https://docs.github.com/en/copilot/concepts/agents/code-review#code-review-usage)
attributes built-in automatic reviews to the author and explicitly states that
bot-requested reviews are billed directly to the organization.
However, the previous `GITHUB_TOKEN` implementation showed an Actions-bot review
request followed by CCR starting on behalf of the contributor on PR #18530,
and a quota-limit rejection on PR #17949. Neither is a billing receipt, but
they undermine the assumption that a bot requester alone proves organization
funding. `GITHUB_TOKEN` is itself an installation token; using a separate App
changes the requesting identity, not a documented billing-account selector.

GitHub documents
[requesting the Copilot reviewer through REST](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review).
An App-token retry on #17949 also received a quota-limit rejection. Restricting
automation to authors with write access avoids automatically opting other
contributors into potentially personal usage; it does not fix attribution or
ensure eligible authors have budget. Actual execution, quota enforcement, and
billing under Microsoft's policies must be confirmed separately before broad rollout.

1. Before merging, set `COPILOT_REVIEW_MODE=dry-run` and
   `COPILOT_REVIEW_PR_NUMBER` to an agreed team-owned pilot PR. Cancel existing
   enabled workflow runs if needed; changing variables does not stop them.
   After merging, inspect workflow decisions and successful App token creation.
2. Identify applicable repository and organization automatic CCR rules,
   including "Review new pushes." Disable those paths before enabling writes,
   or they can still create author-attributed or duplicate reviews.
3. Assess CCR's downstream runner permissions and confirm organization
   funding/budget policies. Obtain the pilot participant's consent for possible
   personal allowance consumption; do not experiment on unsuspecting customers.
4. Set `COPILOT_REVIEW_MODE=enabled`. Confirm that `aspire-repo-bot[bot]` requests a
   review, then push another commit and confirm a re-review, including a push
   during an active review. Record PR/head, timestamps, requesting actor,
   execution attribution, and any quota errors for billing correlation.
5. Have a billing administrator confirm organization attribution and no
   contributor allowance consumption for these bot-requested reviews. An API
   success or a posted review alone is not proof of billing attribution.
6. Clear the pilot variable to cover all eligible write-access authors' open PRs. Keep human approval
   requirements unchanged and monitor spend and failed workflow runs.

If the App cannot initiate CCR or attribution remains unclear, leave writes
disabled and escalate the pilot evidence to GitHub support. Ask which principal
is checked for quota and which account is billed, and whether Actions and
independent App installation tokens are handled differently. Do not substitute
a personal token, silently escalate permissions, or infer billing solely from
the requesting bot or the displayed "on behalf of" identity.

Personal automatic-review settings and manual requests are outside this
workflow's control and retain their own billing attribution. Actions usage for
CCR's agentic capabilities is also separate from the AI credits for the review.
Rollback is `COPILOT_REVIEW_MODE=disabled`; do not automatically restore
author-attributed rulesets.
