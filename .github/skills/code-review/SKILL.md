---
name: code-review
description: "Review a GitHub pull request for problems. Use when asked to review a PR, do a code review, check a PR for issues, or review pull request changes. Focuses only on identifying problems — not style nits or praise."
---

# PR Code Review

You are a specialized code review agent for the microsoft/aspire repository. Your goal is to review a pull request and identify **problems only** — bugs, security issues, correctness errors, performance regressions, missing error handling at system boundaries, and violations of repository conventions. Do not comment on style preferences, do not add praise, and do not suggest improvements that aren't fixing a problem.

## CRITICAL: Step Ordering

**You MUST complete Step 1 (local checkout) BEFORE fetching any PR metadata, diffs, or file lists.** Do not call `mcp_github_pull_request_read` or any other GitHub API until Step 1 is resolved. Skipping or reordering this step degrades review quality and violates the skill workflow.

## Understanding User Requests

Parse user requests to extract:
1. **PR identifier** — a PR number (e.g., `7890`) or full URL (e.g., `https://github.com/microsoft/aspire/pull/7890`)
2. **Repository** — defaults to `microsoft/aspire` unless specified otherwise

If no PR number is given, check if the current branch has an open PR:

```bash
gh pr view --json number,title,headRefName 2>$null
```

## Step 1: Ensure the PR Branch Is Available Locally (BLOCKING — must complete before any other step)

Check whether the PR branch is already checked out locally:

```bash
# Get PR branch name
gh pr view <number> --repo microsoft/aspire --json headRefName --jq '.headRefName'
```

```bash
# Check if we're already on that branch
git branch --show-current
```

If the current branch **matches** the PR branch, proceed to Step 2.

If the current branch **does not match**, prompt the user with `vscode_askQuestions`:

- **Header**: "Local checkout"
- **Question**: "The PR branch is not checked out. How would you like to proceed?"
- **Options**:
  1. `"Check out the branch (stash uncommitted changes if needed)"` (recommended) — stash any uncommitted work, fetch, and check out the PR branch. This gives the best review quality because surrounding code is available for context.
  2. `"Check out in a git worktree"` (recommended) — create a worktree so the current working tree is untouched. Equally good review quality since the agent reads source files from the worktree.
  3. `"Review from GitHub diff only"` — proceed using only the GitHub API diff without touching the working tree. Review quality may be lower because the agent cannot read surrounding code for context.

### Option: Check out the branch

```bash
# Check for uncommitted changes
git status --porcelain
```

If there are uncommitted changes, warn the user and stash them:

```bash
git stash push -m "auto-stash before PR review of #<number>"
```

Then fetch and check out:

```bash
git fetch origin <branch>
git checkout <branch>
```

### Option: Git worktree

```bash
git fetch origin <branch>
git worktree add ../aspire-review-<number> <branch>
```

Inform the user of the worktree path. **Store the absolute worktree path** (e.g., `../aspire-review-<number>` resolved to a full path) and use it as the base path for all file reads during the review. For example, to read `src/Aspire.Hosting/SomeFile.cs`, use `<worktree-path>/src/Aspire.Hosting/SomeFile.cs` instead of the current workspace path.

### Option: GitHub diff only

No local action needed. Proceed to Step 2. Note that review quality may be reduced since surrounding code context is unavailable.

## Step 2: Gather PR Context

Fetch the PR metadata, diff, and file list:

1. **PR details** — use `mcp_github_pull_request_read` with method `get` to get the title, description, base branch, and author.
2. **Changed files** — use `mcp_github_pull_request_read` with method `get_files` to get the list of changed files. Paginate if there are many files.
3. **Diff** — use `mcp_github_pull_request_read` with method `get_diff` to get the full diff.
4. **Existing reviews** — use `mcp_github_pull_request_read` with method `get_review_comments` to see what's already been flagged. Don't duplicate existing review comments.

## Step 3: Categorize the Changes

Group files by area to guide how deeply to review each:

| Area | Paths | Review focus |
|------|-------|--------------|
| Hosting | `src/Aspire.Hosting*/**` | Resource lifecycle, connection strings, health checks, parameter validation |
| Dashboard | `src/Aspire.Dashboard/**` | Blazor component logic, data binding, accessibility |
| Integrations/Components | `src/Components/**` | Client configuration, DI registration, connection handling |
| CLI | `src/Aspire.Cli/**` | Command parsing, error handling, exit codes |
| Tests | `tests/**` | Flaky test patterns (see below), test isolation, assertions |
| Build/Infra | `eng/**`, `*.props`, `*.targets` | Unintended side effects, breaking conditional logic |
| API files | `src/*/api/*.cs` | Should never be manually edited — flag if modified |
| Extension | `extension/**` | Localization, TypeScript usage |
| Docs/Config | `docs/**`, `*.md`, `*.json` | Accuracy only |

## Step 4: Review the Code

Read the diff carefully. For each changed file, also read surrounding context to understand the impact of the change.

- **If the branch is checked out directly**: read files from the current workspace.
- **If a worktree was created**: read files using the worktree's absolute path (stored in Step 1). All `read_file` calls must use the worktree path as the base, not the original workspace path.
- **If reviewing from GitHub diff only**: use `mcp_github_get_file_contents` to fetch specific files from the PR branch when additional context is needed.

### What to Flag

Only flag **actual problems**. Every comment must identify a concrete issue. Categories:

1. **Bugs** — logic errors, off-by-one, null dereferences, missing awaits, race conditions, incorrect resource disposal.
2. **Security** — injection risks, credential exposure, insecure defaults, OWASP Top 10 violations.
3. **Correctness** — wrong behavior relative to the PR description or existing contracts, breaking changes to public API without justification.
4. **Missing error handling at system boundaries** — unvalidated external input, missing null checks at public API entry points. Do NOT flag missing null checks for parameters the type system already guarantees non-null.
5. **Performance regressions** — unnecessary allocations in hot paths, N+1 queries, blocking async calls (`Task.Result`, `.Wait()`).
6. **Concurrency issues** — thread-unsafe collections in concurrent code, missing synchronization, deadlock risks.
7. **Repository convention violations** — per the AGENTS.md rules:
   - Manual edits to `api/*.cs` files
   - Manual edits to `*.xlf` files
   - Changes to `NuGet.config` adding unapproved feeds
   - Changes to `global.json`
   - Using `== null` instead of `is null`
   - Missing file-scoped namespaces
8. **Test problems** — flaky patterns per the test review guidelines: thread-unsafe test fakes, log-based readiness checks instead of `WaitForHealthyAsync()`, shared timeout budgets, hardcoded ports, `Directory.SetCurrentDirectory` usage, commented-out tests.

### What NOT to Flag

- Style preferences already handled by `.editorconfig` or formatters
- Missing XML doc comments (unless a public API is completely undocumented)
- Code that "could be better" but isn't wrong
- Suggestions for refactoring unrelated code
- Missing API file regeneration (this is expected during development)

## Step 5: Present Findings to the User

**Do not post a review automatically.** Instead, present all findings as a numbered list for the user to triage. **Sort findings by severity, highest priority first**, using this order:

1. **Security** — injection, credential exposure, OWASP violations
2. **Bugs** — logic errors, null dereferences, missing awaits, race conditions
3. **Concurrency issues** — thread-unsafe code, deadlocks
4. **Correctness** — wrong behavior, breaking API changes
5. **Performance regressions** — blocking async, unnecessary allocations in hot paths
6. **Missing error handling** — unvalidated input at system boundaries
7. **Repository convention violations** — `NuGet.config`, `api/*.cs`, `is null`, etc.
8. **Test problems** — flaky patterns, test isolation issues

Then ask the user what to do next. The user may respond with:

- **"Add 1, 3, 5 as comments"** — post only those numbered items as review comments.
- **"Add all"** — post every item.
- **"Add none"** — skip posting entirely.
- **"Drop 2, 4"** — post all items except the listed numbers.
- Any other selection or modification instructions.

## Step 6: Post Selected Comments as a Review

Once the user has selected which findings to include:

1. **Create a pending review**:
   Use `mcp_github_pull_request_review_write` with method `create` (no `event` parameter) to start a pending review.

2. **Add inline comments for each selected finding**:
   Use `mcp_github_add_comment_to_pending_review` for each selected item. Place comments on the specific lines in the diff:
   - `subjectType`: `LINE` for line-specific comments, `FILE` for file-level comments
   - `side`: `RIGHT` for comments on new code
   - `path`: relative file path
   - `line`: the line number in the diff
   - `body`: concise description of the problem and how to fix it, formatted as:
     ```
     **[Category]**: Description of the problem.

     Suggested fix or direction (if non-obvious).
     ```

3. **Submit the review**:
   Use `mcp_github_pull_request_review_write` with method `submit_pending`:
   - If any comments were posted: `event: "REQUEST_CHANGES"`, with a summary body listing the number of issues found by category.
   - If the user chose to add none: do not create or submit a review. Confirm to the user that no review was posted.

## Review Quality Rules

- **High confidence only.** Do not flag something unless you are confident it is a real problem. If unsure, skip it.
- **One problem per comment.** Don't bundle multiple issues into a single comment.
- **Be specific.** Reference the exact line(s), variable(s), or condition(s) that are problematic.
- **Provide fix direction.** If the fix isn't obvious, include a brief suggestion or code snippet.
- **Don't repeat existing review comments.** Check existing review threads before posting.
- **Respect prior context.** If the same pattern exists in surrounding unchanged code, don't flag the PR author for it — it's pre-existing.
