---
name: code-review
description: "Review a GitHub pull request for concrete problems and identify sensitive semantic changes that require human review. Use when asked to review a PR, do a code review, check a PR for issues, or review pull request changes. Does not report style nits or praise."
---

# PR Code Review

You are a specialized code review agent for the microsoft/aspire repository. Your goal is to review a pull request, identify **concrete problems**, and separately identify sensitive semantic changes that require human judgment. Problems include bugs, security issues, correctness errors, performance regressions, missing error handling at system boundaries, and violations of repository conventions. Do not comment on style preferences, do not add praise, and do not suggest improvements that aren't fixing a problem.

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

1. **PR details** — use `mcp_github_pull_request_read` with method `get` to get the title, description, base branch, author, current head commit, and commit history.
2. **Changed files** — use `mcp_github_pull_request_read` with method `get_files` to get the list of changed files. Paginate if there are many files.
3. **Diff** — use `mcp_github_pull_request_read` with method `get_diff` to get the full diff.
4. **Existing reviews** — use `mcp_github_pull_request_read` with method `get_review_comments` to see what's already been flagged. Don't duplicate existing review comments.
5. **Approval state** — identify the latest non-bot human `APPROVED` review and the commit it covered. Bot reviews, review-thread replies, and approvals on older commits do not establish that the current head received human review.

If the MCP response does not expose the head, commit history, or review commit IDs, retrieve them with:

```bash
gh pr view <number> --repo <owner>/<repo> --json headRefOid
gh api repos/<owner>/<repo>/pulls/<number>/commits --paginate \
  --jq '.[] | {sha: .sha, message: .commit.message}'
gh api repos/<owner>/<repo>/pulls/<number>/reviews --paginate \
  --jq '.[] | {reviewer: .user.login, user_type: .user.type, state: .state, commit_id: .commit_id, submitted_at: .submitted_at}'
```

Use active reviews with `state: "APPROVED"` and `user_type` other than `"Bot"`. Compare a reachable approved commit with the current head using `git diff <approved-commit> <head-commit>`; for diff-only reviews, use GitHub's compare API. If the approved commit is no longer reachable or cannot be compared with the current head, do not assume the approval is current; report that approval coverage cannot be established when the PR also contains a sensitive semantic change.

## Step 3: Categorize the Changes

Group files by area to guide how deeply to review each:

| Area | Paths | Review focus |
|------|-------|--------------|
| Hosting | `src/Aspire.Hosting*/**` | Resource lifecycle, connection strings, health checks, parameter validation |
| Dashboard | `src/Aspire.Dashboard/**` | Blazor component logic, data binding, accessibility |
| Integrations/Components | `src/Components/**` | Client configuration, DI registration, connection handling |
| CLI | `src/Aspire.Cli/**` | Command parsing, error handling, exit codes |
| Tests | `tests/**` | Flaky test patterns (see below), test isolation, assertions |
| Deployment | `src/Aspire.Hosting.Azure*/**`, `src/Aspire.Hosting.Docker/**`, `src/Aspire.Hosting.Kubernetes/**`, `tests/Aspire.Hosting.*Kubernetes.Tests/**`, `tests/Aspire.Cli.EndToEnd.Tests/**/Kubernetes*`, `tests/Aspire.Deployment.EndToEnd.Tests/**` | Kubernetes/Helm, Docker, and Azure artifacts plus real deployment behavior, provisioning, cleanup |
| Build/Infra | `eng/**`, `*.props`, `*.targets` | Unintended side effects, breaking conditional logic |
| API files | `src/*/api/*.cs` | Should never be manually edited — flag if modified |
| Extension | `extension/**` | Localization, TypeScript usage |
| Docs/Config | `docs/**`, `*.md`, `*.json` | Accuracy only |

### Semantic Risk Classification (MANDATORY)

In addition to grouping files by area, independently classify the **meaning** of the changes. This classification is provenance-neutral: AI or automation authorship alone is not a trigger, and human authorship is not an exemption.

A single trigger in this table is sufficient to require human review:

| Category | Triggers | Implications to call out |
|----------|----------|--------------------------|
| Trust, security, or supply-chain boundary | Package/image registries, feeds, download endpoints, signing, provenance, integrity, audit checks, TLS/certificate handling, authentication, authorization, secrets, permissions, or execution of downloaded content | Wrong or compromised source, weakened verification, credential exposure, unauthorized access, code execution, compliance impact |
| Internal-to-customer boundary | Internal services, domains, mirrors, caches, credentials, flags, or CI assumptions flow into shipped defaults, runtime code, templates, lockfiles, generated Dockerfiles/manifests, or deployment output | Customer authentication failures, outages or throttling, dependency on internal policy/availability, accidental infrastructure disclosure |
| Customer-facing defaults or source precedence | Defaults, fallbacks, endpoint selection, configuration precedence, opt-in/opt-out behavior, or fail-open/fail-closed behavior changes | Silent behavior changes, compatibility breaks, customer outages, environment-specific failures, loss of customer control |
| Scope or intent mismatch | A dependency, security-remediation, generated-file, mechanical, or refactoring PR also changes handwritten production behavior, removes or replaces checks, or buries semantic changes in a noisy diff | Reviewers and tests may validate the stated task instead of the actual product change; generated churn can hide a high-impact edit |
| Approval freshness | Material production, security, deployment, or customer-facing changes were added after the latest non-bot human approval | The approval does not cover the current head or its updated implications |

For every triggered change, create a separate **Human review required** item even if no concrete defect is proven. Combine closely related triggers into one item and list all applicable categories rather than duplicating the same explanation.

Human-review items do not lower the confidence bar for findings. They identify a decision or risk that this skill must not clear on its own.

## Step 4: Review the Code

Read the diff carefully. For each changed file, also read surrounding context to understand the impact of the change.

- **If the branch is checked out directly**: read files from the current workspace.
- **If reviewing from GitHub diff only**: use `mcp_github_get_file_contents` to fetch specific files from the PR branch when additional context is needed.

### Human-Review Analysis for Sensitive Changes

For every semantic risk trigger:

1. **Trace source to shipped sinks** — follow changed constants, URLs, defaults, configuration, and security decisions through every consumer. Search for the old and new values. Include runtime behavior, CLI behavior, templates, lockfiles, generated artifacts, containers, deployment output, and documentation where applicable.
2. **Separate build infrastructure from product behavior** — determine whether a value intended for repository CI, an internal mirror, a test environment, or a developer override became a customer-facing default. A green internal build does not prove that a shipped default works for customers.
3. **Compare removed behavior** — inspect deleted and replacement code side by side. For security controls, state what property the old control enforced and require evidence that the replacement preserves or intentionally changes it.
4. **Validate outcomes, not implementation shape** — a test that asserts a constant, command-line argument, snapshot, generated text, or the PR's stated intent proves propagation only. Look for a test or manual scenario at the real boundary, including whichever relevant failure modes apply: representative customer credentials/network access, uncached or transitive dependencies, unavailable services, invalid input, and override behavior.
5. **Challenge the premise** — do not assume the PR description, existing tests, or earlier AI review correctly identifies the desired trust boundary or customer default. Compare the change with existing product behavior and repository/customer separation.
6. **Check approval freshness** — compare the current head with the commit covered by the latest non-bot human approval. Review the intervening diff, not merely the commit count. Require renewed human review only when those later commits are material; documentation-only or test-only follow-ups do not make an approval stale by themselves.

Passing tests, an existing bot review, or an approval on an older commit does not resolve a human-review item.

#### Calibration: npm registry regression ([#19370](https://github.com/microsoft/aspire/issues/19370))

[PR #18858](https://github.com/microsoft/aspire/pull/18858) replaced the public npm registry with an internal mirror in CLI package operations, generated Dockerfiles, and shipped template lockfiles while also changing integrity/signature behavior inside a dependency-security remediation. Even before reproducing the eventual `E401`, this required human review for:

- The supply-chain trust boundary and the equivalence of removed/replaced security checks.
- Internal build infrastructure becoming a customer-facing default.
- The blast radius across CLI installs, generated containers, and templates.
- Handwritten product behavior hidden inside a large dependency/generated-file diff.
- Material changes added after the latest human approval.

Tests that asserted the internal registry constant or snapshots only proved that the new value propagated. Representative validation needed to install through the customer-facing path with anonymous/customer access and an uncached transitive dependency, while also verifying that internal-feed overrides remained opt-in. Apply this reasoning to analogous registries, mirrors, endpoints, credentials, and security controls; do not special-case npm.

### Impact Analysis for Tests and Regressions

Before deciding whether tests are sufficient, perform a code-based impact analysis. Do not stop at "tests pass" or "there are tests"; map the changed code paths to the behaviors that could regress, then compare that list to the test changes.

For each non-trivial production change, identify:

1. **Changed behavior** — what behavior changed, using concrete code paths, methods, or configuration names from the diff.
2. **Affected surfaces** — which user or system surfaces can observe the change: public API, AppHost model, DCP/runtime orchestration, CLI, dashboard, deployment output, VS Code extension, generated artifacts, logs/telemetry, configuration, persistence, networking, or security-sensitive flows.
3. **Regression risks** — the specific ways the changed behavior could break existing scenarios, including timing/order changes, persisted state compatibility, restarts/retries, resource cleanup, cross-resource references, environment variables, connection strings, endpoint URLs, port allocation, and platform/container-runtime differences.
4. **Expected regression coverage** — the focused tests or scenario tests that should fail without the fix or would catch the risky behavior changing again.
5. **Coverage gaps** — any impacted behavior that is not covered by the PR's tests or by clearly relevant existing tests.

Use the impact analysis to drive coverage review. A PR can have many tests and still be missing the regression test that matters. Conversely, do not demand every test category when the impact analysis shows the change does not affect that surface.

When the impact analysis is useful to explain a test finding, present it concisely in the finding: identify the impacted code path, the regression risk, and the missing test shape. For example: "This changes `DcpExecutor.PrepareServices()` port allocation timing, but there is no regression test showing a dependent resource can resolve the endpoint before workload creation."

### Test Coverage Review

Every review must evaluate whether the PR has appropriate tests for the type of behavior being changed. Do not require tests for purely mechanical refactors, comments, or documentation-only changes, but do flag missing or insufficient coverage when production behavior changes and there is no explicit, convincing justification in the PR. Regression coverage is especially important: bug fixes and behavior changes should include tests that would have failed before the fix, not just broad happy-path coverage or regenerated snapshots.

Use this mapping when deciding whether coverage is appropriate:

| Change type | Expected coverage to look for |
|-------------|-------------------------------|
| Core logic, resource model, integrations, parsers, validation, error handling, public API behavior | Unit or integration tests in the matching `tests/*.*Tests/` project |
| User-visible Aspire CLI commands, prompts, terminal workflows, install/update behavior, or command output contracts | CLI end-to-end coverage under `tests/Aspire.Cli.EndToEnd.Tests/`, in addition to focused unit tests where practical |
| Dashboard UI, browser-only behavior, authentication flows, layout, or interactions that bUnit cannot realistically exercise | Dashboard Playwright coverage under `tests/Aspire.Dashboard.Tests/Integration/Playwright/`, in addition to `tests/Aspire.Dashboard.Tests/` or `tests/Aspire.Dashboard.Components.Tests/` coverage for logic/components |
| Deployment, publish, provisioning, generated Kubernetes/Helm/Bicep/Docker artifacts, Azure resource wiring, or deployed endpoint behavior | Deployment end-to-end coverage under `tests/Aspire.Deployment.EndToEnd.Tests/` when the behavior depends on actual deployment; generated artifact snapshot tests alone are not sufficient for deployment behavior changes |
| VS Code extension commands, tree views, debugger flows, RPC/DCP/MCP integration, extension UI, or CLI integration visible through VS Code | VS Code extension E2E coverage under `extension/src/test-e2e/`, in addition to Mocha unit tests under `extension/src/test/` where practical |

For deployment changes, be especially strict: emitting or updating Helm charts, Kubernetes YAML, Docker Compose, Bicep, JSON manifests, or snapshot files only proves the serializer output. If the PR changes deployment behavior, resource connectivity, provisioning order, infrastructure composition, environment variables, endpoint exposure, health, cleanup, or upgrade behavior, look for a deployment test that actually deploys and verifies the scenario. It is acceptable for the PR to update or refactor an existing deployment E2E test instead of adding a brand-new one, but the resulting test must exercise the changed behavior.

When specialized coverage is missing and the appropriate shape is unclear, use or reference the relevant skill for review context: `cli-e2e-testing`, `dashboard-testing`, `deployment-e2e-testing`, or `vscode-extension`.

### What to Flag

Only flag **actual problems** in the **Findings** section. Every comment must identify a concrete issue. Do not turn a human-review risk into a finding unless the diff provides evidence of an actual defect. Categories:

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
13. **Code comment guidance** — apply the `AGENTS.md` Code comments guidance when reviewing changed code. Flag only concrete problems, such as comments that contradict the code, workaround comments without a tracking link, parser/protocol/log parsing that omits the raw shape needed to understand edge cases, or comments around privacy/security-sensitive behavior that fail to explain the opt-in, scope, or WHY. Do not flag subjective missing comments or ask for comments on obvious code.
14. **Test problems** — flaky patterns per the test review guidelines: thread-unsafe test fakes, log-based readiness checks instead of `WaitForHealthyAsync()`, shared timeout budgets, hardcoded ports, `Directory.SetCurrentDirectory` usage, commented-out tests.
15. **Missing or insufficient test coverage** — production behavior changed without appropriate coverage for the affected surface, or a bug fix lacks a focused regression test that would have failed before the fix. Be specific about the impacted code path, the regression risk, which behavior is untested, and which coverage type is expected. For deployment changes, explicitly flag PRs that only update generated manifests or snapshots when a deployment E2E test should verify the deployed behavior.

### What NOT to Flag

- Style preferences already handled by `.editorconfig` or formatters
- Missing XML doc comments (unless a public API is completely undocumented)
- Suggestions for refactoring unrelated code
- Missing API file regeneration (this is expected during development)
- Missing tests for documentation-only changes, comment-only changes, mechanical renames, or refactors that demonstrably preserve behavior

### Reviewing refactored / moved code

When code is moved from one file to another (e.g., extracting a class), treat the moved code as if it were newly written. Specifically:

- **Flag pre-existing issues in moved code.** If buggy or unsafe code is copy-pasted into a new file, flag it. The refactoring is an opportunity to fix it. Mark these as "Pre-existing issue, good opportunity to fix during this refactoring."
- **Diff old vs. new behavior.** When a type/class is deleted and replaced, explicitly compare the old and new implementations. Look for: removed overrides, changed exception behavior, relaxed validation, lost invariant checks.
- **Check callers of removed types.** If `OldClass` is removed and replaced by `NewClass<T>`, verify that all call sites that depended on `OldClass`-specific behavior still work correctly.

## Step 5: Present Findings and Human-Review Items

**Do not post a review automatically.** Always present these two sections:

### Findings

Present concrete, high-confidence problems as a numbered list for the user to triage, ordered by potential impact. If there are none, say "No findings."

### Human review required

Present every semantic risk item separately as `HR1`, `HR2`, and so on. If there are none, say "None." Each item must include:

- **Category** — one or more categories from the semantic risk table.
- **Evidence** — the exact changed code, configuration, artifact, or approval-to-head diff that triggered the item.
- **Affected surfaces** — the customer and system surfaces reached by the change.
- **Implications** — concrete security, availability, compatibility, operational, or supply-chain consequences.
- **Human validation** — the decision, expertise, or representative scenario a human must validate.
- **Approval freshness** — whether an existing human approval covers the current head.

These items are mandatory risk disclosures, not proven bugs, and are not eligible for automatic conversion into inline review comments. If the same change also has a concrete finding, cross-reference it rather than repeating the full explanation.

Then ask the user what to do next. The user may respond with:

- **"Add 1, 3, 5 as comments"** — post only those numbered items as review comments.
- **"Add all"** — post every item.
- **"Add none"** — skip posting entirely.
- Any other selection or modification instructions.

## Step 6: Post Selected Comments as a Review

Once the user has selected which findings to include:

### Human-review approval gate

If any **Human review required** item remains unresolved:

- Do not recommend or submit an `APPROVE` review.
- If the user asks to approve, require explicit confirmation that a human reviewed the listed item(s), their implications, and the material changes at the current head. A request such as "add all" or a passing CI run is not confirmation.
- Selected concrete findings may still be submitted with `event: "COMMENT"` while human review is pending.
- Do not infer resolution from the author addressing an inline comment, an AI re-review, or an approval attached to an older commit.

After the user explicitly confirms that human review occurred, continue with the existing approval and auto-merge checks.

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
- **Make human-review items evidence-based.** Tie every item to an exact sensitive semantic change and explain its implications. Do not emit generic "this is risky" or "a human should check this" warnings.
- **Keep findings and human-review items distinct.** A risk requiring judgment is not automatically a defect. Conversely, finding a concrete defect does not remove the need for human review of the underlying sensitive decision.
- **Do not infer safety from provenance or process.** Author identity, AI review, test volume, green CI, and stale approval are not substitutes for tracing the changed behavior.
- **Do not approve unresolved human-review items.** Explicit confirmation must cover the current head and the implications listed by the skill.
- **One problem per comment.** Don't bundle multiple issues into a single comment.
- **Be specific.** Reference the exact line(s), variable(s), or condition(s) that are problematic.
- **Provide fix direction.** If the fix isn't obvious, include a brief suggestion or code snippet.
- **Don't repeat existing review comments.** Check existing review threads before posting.
