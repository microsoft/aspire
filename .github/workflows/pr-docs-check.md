---
description: |
  Analyzes merged pull requests for documentation needs. When a PR is merged
  against main or release/* branches, this workflow reviews the changes and
  determines if documentation updates are required on microsoft/aspire.dev.
  If updates are needed, it creates a draft PR with the changes following
  the doc-writer skill conventions. It also comments on the original PR
  with a link to the draft PR (or a "no docs needed" message).

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

if: >-
  (github.event.pull_request.merged == true || github.event_name == 'workflow_dispatch')
  && github.repository_owner == 'microsoft'

checkout:
  # gh-aw checks out the workflow repository first, then overlays the current
  # workspace with aspire.dev because that is where documentation changes are
  # authored. Read aspire PR details via GitHub tools instead of relying on
  # local files from the initial workflow-repository checkout.
  # Recompile this workflow with gh-aw v0.67.2+; v0.67.1 emits a broken
  # cross-job checkout token handoff that GitHub Actions strips as a secret.
  - repository: microsoft/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]
    current: true

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
  create-pull-request:
    title-prefix: "[docs] "
    labels: [docs-from-code]
    draft: true
    target-repo: "microsoft/aspire.dev"
    # Keep protected-file handling explicit in the source of truth. Copilot
    # workflows automatically protect AGENTS.md alongside dependency manifests
    # and repository security config unless this policy is intentionally relaxed.
    protected-files: blocked
    fallback-as-issue: true
  add-comment:
    target-repo: "microsoft/aspire"
    # Note: The compiler adds 'discussions: write' permission which is not a valid
    # GitHub Apps API scope and breaks token minting. After any recompile, manually
    # remove 'discussions: write' and 'permission-discussions: write' from the lock
    # file. Tracked: https://github.com/github/gh-aw/issues/25467
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
> The agent runs with `microsoft/aspire.dev` as the current workspace, so use
> GitHub tools for the source `microsoft/aspire` PR details and diff instead of
> expecting a local checkout of the merged PR contents to remain available.
>
> For security, this workflow only auto-activates for merged PRs whose head
> repository is `microsoft/aspire`. Unlike the old `pull_request_target` hook,
> fork-based PRs are intentionally excluded from automatic activation; use
> `workflow_dispatch` with `pr_number` when a maintainer wants to run the docs
> check manually for a merged fork PR.

## Step 1: Gather PR Information

Use the GitHub tools to read the full pull request details from `microsoft/aspire` for
the PR number above, including:

- Title and description
- Author username
- Base branch (e.g., `main` or `release/X.Y`)
- The full diff of all changed files

If this was triggered via `workflow_dispatch`, use the `pr_number` input to look up
the PR details.

## Step 2: Analyze Changes for Documentation Needs

Review the PR diff and metadata for user-facing changes that warrant documentation:

**Changes that typically need documentation:**
- New public APIs: methods, classes, interfaces, or extension methods
- New features or capabilities: hosting integrations, client integrations, CLI commands,
  or dashboard features
- Breaking changes: removed or renamed APIs, behavioral changes, migration needs
- New configuration options: settings, environment variables, or parameters
- New resource types: Aspire hosting resources or cloud service integrations
- Significant behavioral changes: service discovery, health checks, telemetry, or deployment

**Changes that do NOT typically need documentation:**
- Internal refactoring with no public API surface changes
- Test-only changes
- Build/CI infrastructure changes
- Bug fixes that don't change documented behavior
- Dependency version bumps
- Code style or formatting changes

## Step 3: If No Documentation Needed

If you determine that no documentation updates are needed:

1. **Comment on the PR** in `microsoft/aspire` (PR number from Step 1) with:
   - A brief message confirming no documentation updates are required
   - A short explanation of why (e.g., "internal refactoring only", "test changes only")
2. **Stop here** — do not proceed to the remaining steps.

## Step 4: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`microsoft/aspire.dev` workspace. This skill contains comprehensive guidelines for
writing documentation on the Aspire docs site, including:

- Site structure and file organization
- Astro/MDX conventions and frontmatter requirements
- Required component imports (Starlight components)
- Formatting rules, code block conventions, and linking patterns

**You must follow all guidelines in the doc-writer skill when writing documentation.**

## Step 5: Browse Existing Documentation

Explore the existing documentation in `src/frontend/src/content/docs/` to:

- Identify pages that cover the affected feature area
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced

## Step 6: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.mdx` files in place
- **For new pages**: Create new `.mdx` files in the appropriate directory following
  the doc-writer skill's conventions for frontmatter, imports, and structure

Ensure all changes follow the doc-writer skill guidelines from Step 4. Include:
- Proper frontmatter (`title`, `description`)
- Required Starlight component imports
- Code examples where appropriate
- Cross-references to related documentation pages
- Correct use of Aside, Steps, Tabs, and other components

## Step 7: Create Draft PR

Create a draft pull request on `microsoft/aspire.dev` with:

**Title**: A clear, concise title describing the documentation work
(the `[docs]` prefix will be added automatically)

**Description** that includes:
- A prominent link to the source PR: `Documents changes from microsoft/aspire#<number>`
- The PR author mention: `@<author>`
- A summary of what documentation was added or changed
- A list of files modified or created
- Whether pages were updated or newly created

## Step 8: Comment on Source PR

After the draft PR is created, **comment on the original PR** in `microsoft/aspire`
(PR number from Step 1) with:

- A message indicating documentation updates have been drafted
- A link to the newly created draft PR on `microsoft/aspire.dev`
- A brief summary of what documentation changes were made
- A note that the draft PR needs human review before merging
