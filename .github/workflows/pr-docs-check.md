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
    # NOTE: do NOT set a static `reviewers:` here. The drafted PR's reviewer is
    # the SME identified from the source aspire PR's reviews at runtime, and
    # that decision can't live in static frontmatter. The `notify-source-pr`
    # safe-output job below requests the SME on the drafted PR after creation.
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
  jobs:
    notify-source-pr:
      name: "Notify source PR"
      description: |
        Post the documentation analysis result as a comment on the source
        microsoft/aspire pull request and (when a draft documentation PR was
        opened on microsoft/aspire.dev) request a review from the SME
        identified from the source PR.

        Emit exactly one `notify_source_pr` item per run, after you've finished
        any `create_pull_request` or no-docs-needed reasoning. Use `result:
        "drafted"` when you just emitted a `create_pull_request`; use `result:
        "skipped"` when no docs PR is needed. DO NOT try to embed the drafted
        PR's URL or number in the `summary` text — the workflow knows them
        from the safe-outputs handler and will substitute the real values.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        contents: read
      inputs:
        source_pr_number:
          description: "PR number on microsoft/aspire that triggered this run."
          required: true
          type: number
        result:
          description: "'drafted' if a docs PR was opened on microsoft/aspire.dev, or 'skipped' if no docs PR was needed."
          required: true
          type: string
        sme_login:
          description: "GitHub login of the SME from the source PR (preferred reviewer for the drafted docs PR). Empty string if no SME was identified."
          required: false
          type: string
        target_branch:
          description: "Effective target branch on microsoft/aspire.dev (only meaningful when result is 'drafted')."
          required: false
          type: string
        summary:
          description: "Short markdown summary of the documentation changes (when drafted) or rationale (when skipped). 1-3 sentences plus optional bullet list. DO NOT include the drafted PR URL or number — the workflow injects those automatically."
          required: true
          type: string
      steps:
        - name: Mint aspire-bot token (microsoft/aspire)
          id: aspire-token
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
            private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
            owner: microsoft
            repositories: aspire
        - name: Mint aspire-bot token (microsoft/aspire.dev)
          id: aspire-dev-token
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
            private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
            owner: microsoft
            repositories: aspire.dev
        - name: Post status comment on source PR
          uses: actions/github-script@v9
          env:
            DRAFT_PR_URL: ${{ needs.safe_outputs.outputs.created_pr_url }}
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspire-token.outputs.token }}
            script: |
              const fs = require('fs');
              const MARKER = '<!-- pr-docs-check:notify-source-pr -->';
              const SUMMARY_MAX = 2000;

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.warning(`Agent output file not found at ${outputPath}; skipping comment.`);
                return;
              }

              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item in agent output; nothing to post.');
                return;
              }

              // Source PR number is supplied by the agent. Validate it as a
              // positive integer with a sane upper bound; the safe-jobs framework
              // does not pass workflow-context expressions through env: cleanly,
              // and threat detection has already gated this output.
              const agentNumber = parseInt(String(item.source_pr_number), 10);
              if (!Number.isInteger(agentNumber) || agentNumber <= 0 || agentNumber > 10_000_000) {
                core.warning(`Invalid source_pr_number from agent: ${item.source_pr_number}; skipping comment.`);
                return;
              }
              const sourcePrNumber = agentNumber;

              const result = (item.result || '').toString().trim().toLowerCase();
              const targetBranch = (item.target_branch || '').toString().trim();
              const draftUrl = (process.env.DRAFT_PR_URL || '').trim();
              const draftNumber = (process.env.DRAFT_PR_NUMBER || '').trim();

              // Bound the agent-supplied summary so a malformed item can't blow up the comment.
              let summary = (item.summary || '').toString().trim();
              if (summary.length > SUMMARY_MAX) {
                summary = summary.slice(0, SUMMARY_MAX) + '\n\n_(summary truncated)_';
              }

              let body;
              if (result === 'drafted' && draftUrl) {
                const branchSuffix = targetBranch ? ` targeting \`${targetBranch}\`` : '';
                const numberDisplay = draftNumber || '?';
                body = [
                  MARKER,
                  `📝 Documentation has been drafted in [microsoft/aspire.dev#${numberDisplay}](${draftUrl})${branchSuffix}.`,
                  '',
                  summary,
                  '',
                  '> [!NOTE]',
                  '> This draft PR needs human review before merging.'
                ].join('\n');
              } else if (result === 'drafted') {
                // Agent intended to draft a PR but the safe-outputs handler did not produce
                // a created_pr_url. Surface this as a failure rather than a "skipped" result.
                body = [
                  MARKER,
                  '⚠️ Documentation drafting was attempted but the draft PR could not be confirmed.',
                  '',
                  `See the workflow run for details: ${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`,
                  '',
                  summary
                ].join('\n');
              } else {
                body = [
                  MARKER,
                  '✅ No documentation update needed.',
                  '',
                  summary
                ].join('\n');
              }

              // Best-effort hide-older-comments: minimize prior comments on this PR that
              // carry our marker. Mirrors the framework's hide-older-comments behavior
              // for re-runs of the same workflow on the same PR.
              try {
                const existing = await github.paginate(github.rest.issues.listComments, {
                  owner: 'microsoft',
                  repo: 'aspire',
                  issue_number: sourcePrNumber,
                  per_page: 100,
                });
                for (const c of existing) {
                  if (c.body && c.body.includes(MARKER)) {
                    try {
                      await github.graphql(
                        `mutation($id: ID!) { minimizeComment(input: { subjectId: $id, classifier: OUTDATED }) { minimizedComment { isMinimized } } }`,
                        { id: c.node_id }
                      );
                    } catch (e) {
                      core.warning(`Failed to minimize comment ${c.id}: ${e.message}`);
                    }
                  }
                }
              } catch (e) {
                core.warning(`Failed to enumerate prior comments: ${e.message}`);
              }

              await github.rest.issues.createComment({
                owner: 'microsoft',
                repo: 'aspire',
                issue_number: sourcePrNumber,
                body,
              });
              core.info(`Posted ${result || 'unknown'} comment on microsoft/aspire#${sourcePrNumber}`);
        - name: Request SME review on draft PR
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/github-script@v9
          env:
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspire-dev-token.outputs.token }}
            script: |
              const fs = require('fs');

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.info('Agent output file not found; skipping reviewer request.');
                return;
              }
              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item; skipping reviewer request.');
                return;
              }
              const sme = (item.sme_login || '').toString().trim().replace(/^@/, '');
              if (!sme) {
                core.info('No SME login provided; leaving draft PR without an explicit reviewer.');
                return;
              }

              const draftNumber = parseInt(String(process.env.DRAFT_PR_NUMBER || ''), 10);
              if (!Number.isInteger(draftNumber) || draftNumber <= 0) {
                core.warning(`Invalid draft PR number: ${process.env.DRAFT_PR_NUMBER}`);
                return;
              }
              try {
                await github.rest.pulls.requestReviewers({
                  owner: 'microsoft',
                  repo: 'aspire.dev',
                  pull_number: draftNumber,
                  reviewers: [sme],
                });
                core.info(`Requested @${sme} as reviewer on microsoft/aspire.dev#${draftNumber}`);
              } catch (e) {
                // Best-effort: an SME may not be assignable on aspire.dev (no write access,
                // outside collaborator, etc.). Don't fail the job over this.
                core.warning(`Failed to request reviewer @${sme} on microsoft/aspire.dev#${draftNumber}: ${e.message}`);
              }

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

## Step 2: Identify the Subject-Matter Expert (SME)

Determine which human is the best fit to review the drafted documentation PR. The
SME is the person most familiar with the change in the source `microsoft/aspire`
PR — typically the human who reviewed/approved it, except when the PR was
authored by GitHub Copilot Coding Agent (in which case the SME is the human who
**initiated the Copilot session**, not whoever happened to approve the bot's
output).

### Step 2a: If the source PR was authored by Copilot Coding Agent

Fetch the source PR's `user.login` and `user.type`. If the PR was authored by a
Copilot bot — that is, `user.type == "Bot"` AND `user.login` matches `Copilot`,
`copilot-swe-agent`, or any login containing `copilot` and ending in `[bot]` —
then the **human session originator** (the person who assigned `@copilot` to
an issue and therefore initiated the session) is recorded in the PR's
`assignees[]` field alongside the `Copilot` bot itself. This person is the SME
because they framed the original problem and have the deepest context for the
change, even though they didn't author the code.

Apply the following logic:

1. Read `pull_request.assignees[]` from the source PR.
2. Filter out bot accounts: any login matching `Copilot`, `copilot-swe-agent`,
   anything ending in `[bot]`, or matching `dependabot`, `github-actions`,
   `aspire-bot`.
3. If exactly one human assignee remains, set `SME_LOGIN` = that login and
   **skip the rest of Step 2**. That person initiated the Copilot session and
   is the subject-matter expert.
4. If multiple human assignees remain, prefer the assignee whose latest review
   state on the source PR is `APPROVED`. If still ambiguous, pick the one whose
   login appears earliest in `assignees[]`. **Skip the rest of Step 2.**
5. If no human assignees remain (unusual — Copilot Coding Agent normally
   assigns the originator), fall through to Step 2b.

### Step 2b: For human-authored PRs (or as a fallback from Step 2a)

Use the GitHub tools to list pull request reviews for the source PR
(`GET /repos/microsoft/aspire/pulls/{N}/reviews`) and apply the following logic:

1. **Collapse reviews by reviewer.** For each unique reviewer login, keep only their
   *most recent* review event (the latest `submitted_at`).
2. **Exclude** the source PR author and any bot account (login ending in `[bot]`,
   or matching `dependabot`, `github-actions`, `aspire-bot`, `copilot`, etc.).
3. **Prefer** reviewers whose latest collapsed state is `APPROVED`. Among those,
   pick the one with the most recent `submitted_at`.
4. **Fallback A**: if no `APPROVED` reviewer exists, pick the reviewer with any
   non-`COMMENTED`-only state (for example, `CHANGES_REQUESTED`) whose latest
   `submitted_at` is most recent.
5. **Fallback B**: if no reviews exist at all, look at CODEOWNERS for the changed
   files in `microsoft/aspire` and use the first individual login (skip team
   handles). Treat this as a hint, not a strong signal.
6. **Final fallback**: leave `SME_LOGIN` empty (the workflow will draft the PR
   without an explicit reviewer rather than guess).

Capture the chosen login as `SME_LOGIN`. Do NOT include the `@` prefix. You will pass
this to the `notify_source_pr` safe output later.

## Step 3: Determine Target Branch on `microsoft/aspire.dev`

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

## Step 4: Use an Existing Target Branch, Otherwise Fall Back to `main`

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

## Step 5: Detect Significant Changes and Decide Whether a Docs PR Is Required

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

## Step 6: If No Docs PR Is Required

If you determine that no docs PR is required because the change is not significant or the
existing docs already cover it sufficiently:

1. Emit a single `notify_source_pr` safe output with:
   - `source_pr_number`: the source PR number from Step 1.
   - `result`: `"skipped"`.
   - `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was found).
   - `summary`: a short markdown rationale (1–3 sentences). For example:
     `"Test/build changes only — existing docs already cover this behavior."`.
2. **Stop here** — do not proceed to the remaining steps. Do **not** emit
   `create_pull_request` or any other safe output for the no-docs path.

## Step 7: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`microsoft/aspire.dev` workspace. This skill contains comprehensive guidelines for
writing documentation on the Aspire docs site, including:

- Site structure and file organization
- Astro/MDX conventions and frontmatter requirements
- Required component imports (Starlight components)
- Formatting rules, code block conventions, and linking patterns

**You must follow all guidelines in the doc-writer skill when writing documentation.**

## Step 8: Browse Existing Documentation

Explore the existing documentation in `src/frontend/src/content/docs/` to:

- Identify pages that cover the affected feature area
- Confirm the documentation gap you identified in Step 5
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced

## Step 9: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.mdx` files in place
- **For new pages**: Create new `.mdx` files in the appropriate directory following
  the doc-writer skill's conventions for frontmatter, imports, and structure

Keep the changes focused on the significant user-facing change that triggered this
workflow. Prefer updating the smallest correct set of pages over broad speculative edits.

Ensure all changes follow the doc-writer skill guidelines from Step 7. Include:
- Proper frontmatter (`title`, `description`)
- Required Starlight component imports
- Code examples where appropriate
- Cross-references to related documentation pages
- Correct use of Aside, Steps, Tabs, and other components

## Step 10: Create Draft PR

Create a draft pull request on `microsoft/aspire.dev` with:

**Base branch**: the effective target branch chosen in Step 4. When emitting
the `create_pull_request` safe output, set its `base` field to that branch
(for example, `release/13.3`, `release/13.2.1`, or `main`). If Step 4 fell
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

Do **not** include `reviewers` in the `create_pull_request` emission. The SME
identified in Step 2 is requested as a reviewer by the `notify_source_pr`
safe-output job, not by `create_pull_request`.

## Step 11: Notify Source PR

After emitting `create_pull_request`, emit a single `notify_source_pr` safe output
with:

- `source_pr_number`: the source PR number from Step 1.
- `result`: `"drafted"`.
- `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was found).
- `target_branch`: the effective target branch from Step 4 (for example,
  `release/13.3` or `main`).
- `summary`: a short markdown summary (1–3 sentences plus optional bullet list)
  of the documentation changes made. List the files modified or created. Do **not**
  describe links here — the workflow injects the drafted PR's URL automatically.

> [!IMPORTANT]
> Do **not** try to compose the drafted PR's URL or PR number yourself in the
> `summary` text. The `notify_source_pr` safe-output job knows the real values
> from the safe-outputs handler and will substitute them when posting the
> comment. Likewise, do **not** call `add_comment` for either the "drafted" or
> "skipped" path — `notify_source_pr` is the only commenting path used by this
> workflow.
