---
description: |
  Analyzes merged pull requests for significant user-facing changes. When a
  PR is merged against main or release/* branches, this workflow determines
  whether microsoft/aspire.dev needs a documentation PR. If documentation
  updates are required, it creates a draft PR with the changes following the
  doc-writer skill conventions. The draft PR targets the aspire.dev branch
  resolved from the source PR's release reasoning (PR milestone, linked-issue
  milestone, then source PR base), using the matching release/* branch when it
  already exists and falling back to aspire.dev main otherwise. It also
  comments on the original PR with a link to the draft PR (or a "no docs
  needed" message).

on:
  pull_request:
    types: [closed]
    branches:
      - main
      - release/*
  workflow_dispatch:
    inputs:
      pr_number:
        description: "PR number to analyze"
        required: true
        type: string
  stale-check: false

if: >-
  (github.event.pull_request.merged == true || github.event_name == 'workflow_dispatch')
  && github.repository_owner == 'microsoft'

checkout:
  # Use aspire.dev as the current workspace because that is where documentation
  # changes are authored, and keep a mirrored checkout under _repos so the
  # safeoutputs create_pull_request tool can reliably rediscover the target repo
  # in multi-repo mode.
  - repository: microsoft/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]
    current: true
    # Fetch release/* refs in addition to the default branch so Step 3 can
    # detect whether the resolved release branch (for example, release/13.3)
    # exists on microsoft/aspire.dev. With the default shallow checkout, only
    # the default branch ref is available locally, which causes the
    # "release branch exists" check to always fall back to main.
    fetch: ["release/*"]
  - repository: microsoft/aspire.dev
    path: _repos/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]

permissions:
  contents: read
  pull-requests: read
  issues: read

network:
  allowed:
    - defaults
    - github

tools:
  github:
    toolsets: [repos, issues, pull_requests]
    # Keep the guard policy explicit so gh-aw does not inject a separate
    # auto-lockdown github-script step with an independently resolved action pin.
    min-integrity: approved
    allowed-repos:
      - microsoft/*
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev", "aspire"]

safe-outputs:
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire.dev", "aspire"]
  steps:
    - name: Mirror target repo checkout
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      uses: actions/checkout@v6.0.2
      with:
        repository: microsoft/aspire.dev
        # Seed the mirrored workspace at aspire.dev main. The safe-outputs
        # handler will fetch and use the agent-provided `base` override when
        # creating the PR, restricted by `allowed-base-branches` below.
        ref: main
        token: ${{ steps.safe-outputs-app-token.outputs.token }}
        persist-credentials: false
        path: _repos/aspire.dev
        fetch-depth: 1
    - name: Configure mirrored target repo Git credentials
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      working-directory: _repos/aspire.dev
      env:
        REPO_NAME: "microsoft/aspire.dev"
        SERVER_URL: ${{ github.server_url }}
        GIT_TOKEN: ${{ steps.safe-outputs-app-token.outputs.token }}
      run: |
        git config --global user.email "github-actions[bot]@users.noreply.github.com"
        git config --global user.name "github-actions[bot]"
        git config --global am.keepcr true
        SERVER_URL_STRIPPED="${SERVER_URL#https://}"
        git remote set-url origin "https://x-access-token:${GIT_TOKEN}@${SERVER_URL_STRIPPED}/${REPO_NAME}.git"
        echo "Mirrored checkout configured with standard GitHub Actions identity"
  create-pull-request:
    title-prefix: "[docs] "
    labels: [docs-from-code]
    # On pull_request-triggered runs, request the original aspire PR author as
    # reviewer for the generated aspire.dev draft PR. On workflow_dispatch runs
    # this expression resolves to empty and no reviewer is auto-requested.
    reviewers: "${{ github.event.pull_request.user.login || '' }}"
    draft: true
    # Default to aspire.dev main, but allow the agent to override the PR base
    # per run using the milestone/linked-issue/source-base reasoning in the
    # prompt body. Restrict overrides to main and release/*.
    base-branch: main
    allowed-base-branches:
      - main
      - release/*
    target-repo: "microsoft/aspire.dev"
    # Keep protected-file handling explicit in the source of truth. Copilot
    # workflows automatically protect AGENTS.md alongside dependency manifests
    # and repository security config unless this policy is intentionally relaxed.
    protected-files: blocked
    fallback-as-issue: true
  add-comment:
    target-repo: "microsoft/aspire"
    # This workflow only comments on pull requests, so opt out of discussion
    # support to avoid requesting the invalid GitHub App discussions scope.
    # Tracked: https://github.com/github/gh-aw/issues/25467
    discussions: false
    hide-older-comments: true

timeout-minutes: 20
---

# PR Documentation Check

Analyze a merged pull request in `microsoft/aspire` and determine whether documentation
updates are needed on the `microsoft/aspire.dev` documentation site. If updates are
needed, create a draft PR with the actual documentation changes.

## Context

- **Source repository**: `microsoft/aspire`
- **PR Number**: `${{ github.event.pull_request.number || github.event.inputs.pr_number }}`
- **PR Title**: `${{ github.event.pull_request.title }}`

> [!NOTE]
> The agent runs with `microsoft/aspire.dev` as the current workspace and also
> has a mirrored checkout at `_repos/aspire.dev`, so use GitHub tools for the
> source `microsoft/aspire` PR details and diff instead of expecting a local
> checkout of the merged PR contents to remain available.
>
> After you resolve the target docs branch, use the local
> `microsoft/aspire.dev` workspace to check whether that branch already exists.
> If the resolved release branch does not exist on `aspire.dev`, fall back to
> `main` for both the docs edits and the draft PR base. The
> `create_pull_request` safe output is configured to accept an explicit `base`
> override for `main` and `release/*`; use it.
>
> For security, this workflow only auto-activates for merged PRs whose head
> repository is `microsoft/aspire`. Unlike the old `pull_request_target` hook,
> fork-based PRs are intentionally excluded from automatic activation; use
> `workflow_dispatch` with `pr_number` when a maintainer wants to run the docs
> check manually for a merged fork PR.

## Step 1: Gather PR Information

Use the GitHub tools to read the pull request details from `microsoft/aspire` for the
PR number above, including:

- Title and description
- Author username
- Base branch (e.g., `main` or `release/X.Y`)
- Milestone (if any)
- Any issues linked via `Closes #N` / `Fixes #N` / `Resolves #N` in the PR body
- The list of changed files

Start with the PR metadata and changed-file list. Only inspect diff hunks for files that
are likely to affect user-facing behavior, configuration, or public API surface, or when
the significance is unclear from filenames alone.

If this was triggered via `workflow_dispatch`, use the `pr_number` input to look up
the PR details.

## Step 2: Determine Target Branch on `microsoft/aspire.dev`

All draft docs PRs must be based on the `microsoft/aspire.dev` branch that
corresponds to the release represented by the source PR. Resolve the target
branch using this priority order, stopping at the first match:

1. **PR milestone.** Use the milestone title on the source PR itself.
2. **Linked issue milestone.** For each issue linked via `Closes/Fixes/Resolves #N`
   in the PR body, use the first non-empty milestone title found (in link
   order).
3. **PR base branch.** If the PR's base branch matches `release/X.Y` or
   `release/X.Y.Z`, use that directly.
4. **Fallback.** Use `main`.

Normalize milestone titles with the regex `^v?(\d+)\.(\d+)(?:\.(\d+))?`:

- Match with two groups (for example, `13.3`, `13.3 - Preview 1`, `v13.3`) →
  `release/13.3`.
- Match with three groups (for example, `13.2.1`, `v13.2.1`) →
  `release/13.2.1`.
- Titles that don't match the regex fall through to the next priority.

The result of this step is a single **target branch** string, either `main` or
`release/X.Y[.Z]`.

## Step 3: Use an Existing Target Branch, Otherwise Fall Back to `main`

Use the local `microsoft/aspire.dev` workspace for this step.

1. If the resolved target branch is `main`, use `main`.
2. If the resolved target branch is `release/X.Y[.Z]`, check whether
   `origin/release/X.Y[.Z]` already exists on `microsoft/aspire.dev`.
3. If that release branch exists, use it as the effective target branch.
4. If that release branch does **not** exist, fall back to `main` as the
   effective target branch.
5. Fetch the effective target branch and switch the current workspace to that
   branch before editing docs.

Do **not** create or push new branches in this step. This step only chooses an
existing `microsoft/aspire.dev` branch to use for documentation edits and for
the draft PR base.

## Step 4: Detect Significant Changes and Decide Whether a Docs PR Is Required

Review the PR metadata and candidate diffs for **significant user-facing changes**.

A docs PR is required only when **both** of these are true:

1. The PR introduces a significant user-facing change.
2. The current `microsoft/aspire.dev` documentation does **not** already cover that
   change well enough.

For each candidate docs-worthy change, identify:

- Evidence from the PR (changed files, APIs, commands, options, configuration, or
  behavior)
- Who is affected (app developers, AppHost authors, CLI users, dashboard users, etc.)
- The likely docs surface area (`get-started`, integration guide, reference page,
  command docs, migration guidance, and so on)

**Changes that are usually significant enough to consider documentation:**
- New public APIs: methods, classes, interfaces, or extension methods
- New features or capabilities: hosting integrations, client integrations, CLI commands,
  or dashboard features
- Breaking changes: removed or renamed APIs, behavioral changes, migration needs
- New configuration options: settings, environment variables, or parameters
- New resource types: Aspire hosting resources or cloud service integrations
- Significant behavioral changes: service discovery, health checks, telemetry, or deployment

**Changes that usually do NOT require a docs PR:**
- Internal refactoring with no public API surface changes
- Test-only changes
- Build/CI infrastructure changes
- Bug fixes that don't change documented behavior
- Dependency version bumps
- Code style or formatting changes

Do not create a docs PR for minor or already-understood changes just because they touch a
docs-adjacent area. If the change is small, internal, or already covered by existing docs,
treat it as **no docs PR required**.

## Step 5: If No Docs PR Is Required

If you determine that no docs PR is required because the change is not significant or the
existing docs already cover it sufficiently:

1. **Comment on the PR** in `microsoft/aspire` (PR number from Step 1) with:
    - A brief message confirming no documentation PR is required
    - A short explanation of why (for example: "internal refactoring only",
      "test/build changes only", or "existing docs already cover this behavior")
2. **Stop here** — do not proceed to the remaining steps.

## Step 6: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`microsoft/aspire.dev` workspace. This skill contains comprehensive guidelines for
writing documentation on the Aspire docs site, including:

- Site structure and file organization
- Astro/MDX conventions and frontmatter requirements
- Required component imports (Starlight components)
- Formatting rules, code block conventions, and linking patterns

**You must follow all guidelines in the doc-writer skill when writing documentation.**

## Step 7: Browse Existing Documentation

Explore the existing documentation in `src/frontend/src/content/docs/` to:

- Identify pages that cover the affected feature area
- Confirm the documentation gap you identified in Step 4
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced

## Step 8: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.mdx` files in place
- **For new pages**: Create new `.mdx` files in the appropriate directory following
  the doc-writer skill's conventions for frontmatter, imports, and structure

Keep the changes focused on the significant user-facing change that triggered this
workflow. Prefer updating the smallest correct set of pages over broad speculative edits.

Ensure all changes follow the doc-writer skill guidelines from Step 6. Include:
- Proper frontmatter (`title`, `description`)
- Required Starlight component imports
- Code examples where appropriate
- Cross-references to related documentation pages
- Correct use of Aside, Steps, Tabs, and other components

## Step 9: Create Draft PR

Create a draft pull request on `microsoft/aspire.dev` with:

**Base branch**: the effective target branch chosen in Step 3. When emitting
the `create_pull_request` safe output, set its `base` field to that branch
(for example, `release/13.3`, `release/13.2.1`, or `main`). If Step 3 fell
back to `main` because the resolved release branch does not exist on
`microsoft/aspire.dev`, use `main` here and say so in the PR description.

**Title**: A clear, concise title describing the documentation work
(the `[docs]` prefix will be added automatically)

**Description** that includes:
- A prominent link to the source PR: `Documents changes from microsoft/aspire#<number>`
- The PR author mention: `@<author>`
- The effective target branch and how it was chosen (for example, "Targeting
  `release/13.3` based on the source PR milestone `13.3`." or "Falling back to
  `main` because `release/13.3` does not exist on `microsoft/aspire.dev`.")
- Why this PR is needed (the significant change and the docs gap it addresses)
- A summary of what documentation was added or changed
- A list of files modified or created
- Whether pages were updated or newly created

## Step 10: Comment on Source PR

After the draft PR is created, **comment on the original PR** in `microsoft/aspire`
(PR number from Step 1) with:

- A message indicating documentation updates have been drafted
- A link to the newly created draft PR on `microsoft/aspire.dev`
- The target branch the draft PR was opened against (for example,
  `release/13.3`)
- A brief summary of what documentation changes were made
- A note that the draft PR needs human review before merging
