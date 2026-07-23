---
description: |
  Automatically reviews newly opened pull requests using the repository's
  `code-review` skill (.agents/skills/code-review/SKILL.md). When a PR is
  opened against this repo, the agent reads the PR diff via the GitHub API,
  applies the skill's review criteria (bugs, security, correctness, missing
  test coverage, convention violations), and posts the findings as the
  Aspire bot: inline line comments plus a single, non-blocking COMMENT review
  summarizing the issues by category. The review never approves or requests
  changes — it is advisory only.

max-daily-ai-credits: -1

on:
  # Use pull_request_target (not pull_request) on purpose. We want this review
  # to run on EVERY opened PR — including PRs from forks — and to post the
  # review as the Aspire bot. On a plain `pull_request` event GitHub withholds
  # all repository secrets when the PR head is a fork, so the ASPIRE_BOT_* app
  # secrets would be empty and the bot token could not be minted (the bot could
  # not post on fork PRs). `pull_request_target` runs in the BASE repository
  # context, where the secrets are available, so the bot token mints for fork
  # and same-repo PRs alike.
  #
  # SECURITY: `pull_request_target` runs with write-capable credentials, so it
  # must never execute untrusted PR code. This workflow does NOT check out the
  # PR head — the agent reads the PR diff/files exclusively through the GitHub
  # API (read-only github tools), and the only writes happen in gh-aw's separate
  # permission-scoped safe-outputs job. The default checkout below stays on the
  # base ref, which is where the trusted `code-review` skill file is read from.
  pull_request_target:
    types: [opened]
  # Allow manual runs. This is also what `gh aw run` and `gh aw trial` require —
  # both drive the workflow through workflow_dispatch — so keeping it here enables
  # both ad-hoc re-reviews and sandboxed trial testing. `pr_number` selects which
  # PR to review when the run is started by hand instead of by an opened PR; the
  # prompt and concurrency group resolve it via
  # `github.event.pull_request.number || github.event.inputs.pr_number`.
  workflow_dispatch:
    inputs:
      pr_number:
        # Not required, so `gh aw trial` (which dispatches with no way to pass
        # inputs) can launch the workflow and let `--trigger-context` supply the
        # PR via `github.event.pull_request.number`. When starting a run with
        # `gh aw run`, pass it explicitly: `gh aw run pr-auto-review -F pr_number=<N>`.
        description: "PR number to review (required for gh aw run; supplied via --trigger-context for gh aw trial)"
        required: false
        type: string
  # Add an 👀 reaction to the PR when the review starts, so authors get an
  # immediate signal that the automated review is running.
  reaction: eyes
  # The reaction (and activation guard) runs as the Aspire bot. On fork-sourced
  # pull_request_target runs the default GITHUB_TOKEN is still adequate for the
  # reaction, but minting the app token here keeps the bot identity consistent
  # with the review that follows.
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire"]
  # The review criteria come from a file checked into the repo, not from the
  # triggering event payload, so gh-aw's stale-check guard adds no value here
  # and would only cause the lock file to drift when the frontmatter changes.
  stale-check: false

# Only run for the canonical repository. This keeps the workflow from doing
# anything on forks of microsoft/aspire that copy the workflow file.
if: github.repository == 'microsoft/aspire'

# SECURITY (pull_request_target hardening): never check out the untrusted PR
# head. Pin the checkout to the trusted BASE commit of the target repository so
# the workspace only ever contains code that already exists in microsoft/aspire
# (this is where the agent reads `.agents/skills/code-review/SKILL.md`). The PR's
# proposed changes are read separately and read-only via the GitHub API in the
# agent prompt, so untrusted fork code is never executed in this privileged job.
# See https://securitylab.github.com/resources/github-actions-preventing-pwn-requests/
checkout:
  repository: ${{ github.repository }}
  ref: ${{ github.event.pull_request.base.sha }}

# Serialize runs per PR so a duplicate `opened` event can't post two reviews.
concurrency:
  group: pr-auto-review-${{ github.event.pull_request.number || github.event.inputs.pr_number }}
  cancel-in-progress: false

# The agent itself runs read-only. All writes (inline review comments and the
# consolidated review) are performed by gh-aw's separate, permission-scoped
# safe-outputs job, authenticated as the Aspire bot via the github-app below.
permissions:
  contents: read
  pull-requests: read
  issues: read
  copilot-requests: write

network:
  allowed:
    - defaults
    - github

tools:
  github:
    # repos => read file contents for surrounding context; pull_requests =>
    # read the PR diff, changed files, and existing review threads; issues =>
    # resolve any linked issues referenced by the skill's impact analysis.
    toolsets: [repos, pull_requests, issues]
    # Keep the integrity policy explicit so gh-aw doesn't inject a separate
    # auto-lockdown step with an independently resolved action pin.
    min-integrity: approved
    allowed-repos:
      - microsoft/aspire
    # Read the PR (including fork-sourced diffs) as the Aspire bot for
    # consistent, reliable API access.
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire"]

safe-outputs:
  # Post all writes as the Aspire bot rather than github-actions[bot].
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire"]
  # Inline, line-level comments — one concrete problem per comment. These are
  # buffered and attached to the consolidated review submitted below.
  create-pull-request-review-comment:
    max: 30
    side: RIGHT
    target: triggering
  # A single consolidated review. allowed-events is restricted to COMMENT so the
  # automated review is always advisory: it can never APPROVE or REQUEST_CHANGES
  # and therefore never blocks (or unblocks) merging.
  submit-pull-request-review:
    allowed-events: [COMMENT]
    target: triggering
    # Only attach the AI footer when the review has body text, so a terse
    # "no issues found" review stays clean.
    footer: if-body
---

# PR Auto-Review

You are an **unattended** code-review agent for the `microsoft/aspire` repository.
A pull request was just opened and you must review it automatically and post your
findings. There is no human in the loop during this run.

The pull request to review is **#${{ github.event.pull_request.number || github.event.inputs.pr_number }}**
in `microsoft/aspire`. (Its title is "${{ github.event.pull_request.title }}" when
this run was triggered by an opened PR; that value is empty when the run was started
manually via `workflow_dispatch`.) Always fetch the PR title, author, and other
metadata via the GitHub API in Step 3 rather than relying on the event payload.

## Step 1: Load the review criteria from the `code-review` skill

Read the file `.agents/skills/code-review/SKILL.md` from the checked-out
repository. It is the source of truth for **what to flag** and **what not to
flag**, including:

- the problem categories (bugs, security, correctness, weakened invariants,
  resource leaks, concurrency issues, performance regressions, convention
  violations, test problems, etc.),
- the **"What NOT to Flag"** list,
- the **impact analysis** and **test coverage** guidance (mapping changed code
  to the regression tests that should exist),
- the **refactored / moved code** rules, and
- the **Review Quality Rules** (high-confidence, concrete problems only; one
  problem per comment; be specific; provide fix direction; don't duplicate
  existing review comments).

**You must apply all of those criteria.** The only thing you change is *how the
review is delivered*, described next.

## Step 2: How this unattended run differs from the interactive skill

The skill is written for an interactive session. For this automated workflow,
**override** these parts of the skill:

- **Skip the skill's Step 1 (local checkout) entirely.** Do **not** check out
  the PR branch and do **not** run any PR code. This workflow runs with
  privileged credentials via `pull_request_target`, so executing untrusted
  fork code is forbidden. Review from the GitHub API diff only, using the
  `github` tools to fetch file contents from the PR head ref when you need
  surrounding context.
- **Skip the skill's Step 5 (present findings and wait for the user to triage)
  and its interactive Step 6 (manual review posting / auto-merge prompts).**
  There is no user to ask. Instead, post your findings directly using the
  safe-outputs described in Step 4 below.
- **Never approve and never request changes.** This review is always advisory.

## Step 3: Gather PR context

Using the `github` tools:

1. Read the PR metadata (title, description, base branch, author).
2. Read the list of changed files.
3. Read the full diff.
4. Read the existing review comments so you do **not** duplicate anything that
   has already been flagged.

Then categorize the changes by area and review depth exactly as described in the
skill ("Step 3: Categorize the Changes").

## Step 4: Review and post findings

Review the diff against the skill's criteria. Perform the impact analysis and
test-coverage review the skill describes — a PR can have many tests and still be
missing the regression test that matters.

Post your findings as follows:

- For each **concrete, high-confidence problem**, create one inline review
  comment with `create_pull_request_review_comment`:
  - one problem per comment,
  - placed on a specific line that exists in the PR diff (`side: RIGHT` for
    added/changed lines),
  - stating the problem precisely and giving fix direction (a short suggestion
    or snippet when the fix isn't obvious).
- After adding the inline comments, submit **exactly one** consolidated review
  with `submit_pull_request_review` using event `COMMENT`. The review body
  should be a short summary that:
  - lists the number of issues found grouped by category (e.g. Bugs: 2,
    Test coverage: 1), and
  - notes that this is an automated, advisory review produced from the
    `code-review` skill.

If you find **no** concrete problems, do not invent any. Submit a single short
`COMMENT` review stating that the automated review found no blocking problems,
and add no inline comments.

## Guardrails

- Flag only definite, evidence-backed problems from the diff. Do not raise
  speculative concerns, design opinions, style nits, or praise — follow the
  skill's "What NOT to Flag" list.
- Do not post more than ~30 inline comments; if there are more potential issues
  than that, include only the highest-impact ones and mention in the review
  body that the review was truncated.
- Do not duplicate existing review comments.
- Do not modify any files, push commits, or change PR state. Your only outputs
  are the inline review comments and the single consolidated COMMENT review.
