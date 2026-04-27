---
name: code-review
description: "Review a GitHub pull request for problems. Use when asked to review a PR, do a code review, check a PR for issues, or review pull request changes. Focuses only on identifying problems — not style nits or praise."
---

# PR Code Review

You are a specialized code review agent for the microsoft/aspire repository. Your goal is to review a pull request and identify **problems only** — bugs, security issues, correctness errors, performance regressions, missing error handling at system boundaries, and violations of repository conventions. Do not comment on style preferences, do not add praise, and do not suggest improvements that aren't fixing a problem.

## CRITICAL: Step Ordering

**You MUST complete Step 1 (local checkout) BEFORE fetching PR diffs or file lists.** Branch-discovery calls (e.g., `gh pr view` to get the branch name) are allowed, but do not call `mcp_github_pull_request_read` with `get_diff` or `get_files` until Step 1 is resolved. Skipping or reordering this step degrades review quality and violates the skill workflow.

## Understanding User Requests

Parse user requests to extract:
1. **PR identifier** — a PR number (e.g., `7890`) or full URL (e.g., `https://github.com/microsoft/aspire/pull/7890`)
2. **Repository** — defaults to `microsoft/aspire` unless specified otherwise

If no PR number is given, check if the current branch has an open PR:

```bash
gh pr view --json number,title,headRefName 2>/dev/null
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

If the current branch **does not match**, ask the user how they'd like to proceed:

- **Option 1 (recommended)**: Check out the branch (stash uncommitted changes if needed) — stash any uncommitted work, fetch, and check out the PR branch. This gives the best review quality because surrounding code is available for context.
- **Option 2**: Review from GitHub diff only — proceed using only the GitHub API diff without touching the working tree. Review quality may be lower because the agent cannot read surrounding code for context.

### Option: Check out the branch

```bash
# Check for uncommitted changes
git status --porcelain
```

If there are uncommitted changes, warn the user and stash them:

```bash
git stash push -m "auto-stash before PR review of #<number>"
```

Then check out the PR branch (this handles both same-repo and fork PRs):

```bash
gh pr checkout <number> --repo microsoft/aspire
```

### Option: GitHub diff only

No local action needed. Proceed to Step 2. Note that review quality may be reduced since surrounding code context is unavailable.

## Step 2: Gather PR Context

Fetch the PR metadata, diff, and file list. This skill uses the `mcp_github_*` tools (MCP GitHub integration). These are available when the GitHub MCP server is configured in the agent environment. If they are unavailable, fall back to the `gh` CLI for equivalent operations.

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
- **If reviewing from GitHub diff only**: use `mcp_github_get_file_contents` to fetch specific files from the PR branch when additional context is needed.

### What to Flag

Only flag **actual problems**. Every comment must identify a concrete issue. Categories:

1. **Bugs** — logic errors, off-by-one, null dereferences, missing awaits, race conditions, incorrect resource disposal.
2. **Security** — injection risks, credential exposure, insecure defaults, OWASP Top 10 violations.
3. **Correctness** — wrong behavior relative to the PR description or existing contracts, breaking changes to public API without justification.
4. **Behavioral contract changes** — when a type/class is replaced, removed, or refactored, check whether any behavioral contracts were silently changed. Examples: a property that previously threw on invalid access now returns a default value; an override that enforced an invariant is gone; a method that validated input no longer does.
5. **Weakened invariants** — check whether validation was relaxed during refactoring. Examples: `SingleOrDefault` (throws on duplicates) replaced by `FirstOrDefault` (silently picks first); `Debug.Assert` guarding a release-relevant invariant that should be an `if` + `throw`; precondition checks that were removed.
6. **Missing error handling at system boundaries** — unvalidated external input, missing null checks at public API entry points. Do NOT flag missing null checks for parameters the type system already guarantees non-null.
7. **Performance regressions** — unnecessary allocations in hot paths, N+1 queries, blocking async calls (`Task.Result`, `.Wait()`).
8. **Concurrency issues** — thread-unsafe collections in concurrent code, missing synchronization, deadlock risks.
9. **Temporal coupling and initialization safety** — fields initialized to `null!` with a separate `Initialize()` method that must be called before use; DI registrations that depend on call ordering; any pattern where forgetting a call causes a runtime NRE with no compile-time safety.
10. **Resource leaks** — `IDisposable` objects (e.g., `CancellationTokenSource`, `SemaphoreSlim`) that are created but never disposed, even if the pattern was moved from elsewhere.
11. **Dead code and stale comments** — comments describing behavior the code no longer implements; unused variables; `ToList()` calls with comments like "materialize to check count" where the count is never checked.
12. **Repository convention violations** — per the AGENTS.md rules:
    - Manual edits to `api/*.cs` files
    - Manual edits to `*.xlf` files
    - Changes to `NuGet.config` adding unapproved feeds
    - Changes to `global.json`
    - Using `== null` instead of `is null`
13. **Test problems** — flaky patterns per the test review guidelines: thread-unsafe test fakes, log-based readiness checks instead of `WaitForHealthyAsync()`, shared timeout budgets, hardcoded ports, `Directory.SetCurrentDirectory` usage, commented-out tests.

### What NOT to Flag

- Style preferences already handled by `.editorconfig` or formatters
- Missing XML doc comments (unless a public API is completely undocumented)
- Suggestions for refactoring unrelated code
- Missing API file regeneration (this is expected during development)

### Reviewing refactored / moved code

When code is moved from one file to another (e.g., extracting a class), treat the moved code as if it were newly written. Specifically:

- **Flag pre-existing issues in moved code.** If buggy or unsafe code is copy-pasted into a new file, flag it. The refactoring is an opportunity to fix it. Mark these as "Pre-existing issue, good opportunity to fix during this refactoring."
- **Diff old vs. new behavior.** When a type/class is deleted and replaced, explicitly compare the old and new implementations. Look for: removed overrides, changed exception behavior, relaxed validation, lost invariant checks.
- **Check callers of removed types.** If `OldClass` is removed and replaced by `NewClass<T>`, verify that all call sites that depended on `OldClass`-specific behavior still work correctly.

## Step 5: Present Findings to the User

**Do not post a review automatically.** Instead, present all findings as a numbered list for the user to triage. Order by potential impact.

Then ask the user what to do next. The user may respond with:

- **"Add 1, 3, 5 as comments"** — post only those numbered items as review comments.
- **"Add all"** — post every item.
- **"Add none"** — skip posting entirely.
- Any other selection or modification instructions.

## Step 6: Post Selected Comments as a Review

Once the user has selected which findings to include:

### Auto-merge safety check

Before submitting a review with `event: "APPROVE"`, check whether the PR has auto-merge enabled:

```bash
gh pr view <number> --repo microsoft/aspire --json autoMergeRequest --jq '.autoMergeRequest'
```

If the result is **non-null** (auto-merge is enabled) **and** the review includes comments, warn the user:

> **Warning:** This PR has auto-merge enabled. Approving it will likely trigger an automatic merge before the author has a chance to address your review comments. Would you like to:
>
> 1. **Approve anyway** — submit as APPROVE (auto-merge may proceed immediately).
> 2. **Downgrade to comment** — submit as COMMENT instead so the author can address feedback first.

Wait for the user's response before proceeding. If they choose option 2, use `event: "COMMENT"` instead of `"APPROVE"`.

### Posting the review

1. **Create a pending review**:
   Use `mcp_github_pull_request_review_write` with method `create` (no `event` parameter) to start a pending review.

2. **Add inline comments for each selected finding**:
   Use `mcp_github_add_comment_to_pending_review` for each selected item. Place comments on the specific lines in the diff:
   - `subjectType`: `LINE` for line-specific comments, `FILE` for file-level comments
   - `side`: `RIGHT` for comments on new code
   - `path`: relative file path
   - `line`: the line number in the diff
   - `body`: concise description of the problem and how to fix it

3. **Submit the review**:
   Use `mcp_github_pull_request_review_write` with method `submit_pending`:
   - If any comments were posted and the user explicitly asked to approve: use `event: "APPROVE"` only if auto-merge is not enabled on the PR, or the user confirmed they want to approve after seeing the auto-merge warning.
   - If any comments were posted and the user did not ask to approve: use `event: "COMMENT"`.
   - In either case, include a summary body listing the number of issues found by category. Do not use `"REQUEST_CHANGES"` unless the user explicitly asks for it.
   - If the user chose to add none: do not create or submit a review. Confirm to the user that no review was posted.

## Review Quality Rules

- **Flag only concrete, high-confidence problems.** Report definite issues such as bugs, security problems, correctness errors, performance regressions, missing error handling at system boundaries, or repository-convention violations. Do not raise speculative concerns, design feedback, or issues you cannot support with specific evidence in the diff.
- **One problem per comment.** Don't bundle multiple issues into a single comment.
- **Be specific.** Reference the exact line(s), variable(s), or condition(s) that are problematic.
- **Provide fix direction.** If the fix isn't obvious, include a brief suggestion or code snippet.
- **Don't repeat existing review comments.** Check existing review threads before posting.
