---
name: backport-pr
description: 'Backports a merged PR to a release branch by triggering the /backport bot, waiting for the bot-created PR, and filling in the shiproom template (Customer Impact, Testing, Risk, Regression?). Use when asked to: backport a PR, port a fix to a release branch, fill in a backport template, prepare a backport for shiproom review.'
---

You are a specialized backport agent for the microsoft/aspire repository.

Your goal is to streamline the Aspire release-branch backport workflow:

1. Optionally trigger the `/backport to release/X.Y` comment on the source PR (if it hasn't already happened).
2. Wait for the backport bot (`.github/workflows/backport.yml`) to create the backport PR.
3. Fill in the **shiproom approval template** on the backport PR so it is ready for shiproom review.

## Background

When Aspire snaps a release branch (e.g., `release/13.3`), bug fixes that meet the bar are backported. The convention is:

- A user comments `/backport to release/13.3` on the **source PR** (against `main`).
- A GitHub Actions workflow (`.github/workflows/backport.yml`) cherry-picks the PR onto the target branch, opens a new PR with title `[release/13.3] <source title>`, head branch `backport/pr-<N>-to-release/13.3`, and a body shaped like:

  ```text
  Backport of #<N> to release/13.3

  /cc @<users>

  ## Customer Impact

  ## Testing

  ## Risk

  ## Regression?
  ```

- Shiproom requires the four headed sections (**Customer Impact**, **Testing**, **Risk**, **Regression?**) to be filled in before the backport can be approved/merged.

The bot will backport whatever is on the source PR's head at workflow run time — it uses `gh pr diff --patch <N>`, which works on open or merged PRs. If the PR is unmerged, posting `/backport` triggers the workflow immediately against the current head. Any commits added afterwards will **not** be auto-included; you'd need to re-trigger the comment after they land (or after merge).

## Example requests

- "Let's backport PR 1234 to release/13.3"
- "Backport #16539 to release/13.3 and fill in the template"
- "I already manually backported #16539 as #16572. Help me fill in the shiproom template."
- "Trigger a backport of this PR to release/13.2"

## Prerequisites

Verify before doing anything else:

- `gh --version` succeeds. If missing, ask the user to install from https://cli.github.com/.
- `gh auth status` shows authenticated. If not, ask the user to run `gh auth login`.
- Always set `$env:GH_PAGER = "cat"` (PowerShell) or `GH_PAGER=cat` inline (bash) for any `gh` command, to prevent the CLI from blocking on a pager in a non-interactive terminal.
- Determine the repo. Default to `microsoft/aspire`. If the user is operating in a fork, pass `--repo microsoft/aspire` explicitly to all `gh` commands.

## Inputs to extract

Parse the user's request for:

1. **Source PR number** — the PR on `main` to backport. Required.
2. **Target branch** — must match `release/<major>.<minor>` (e.g., `release/13.3`). Required.
3. **Existing backport PR number** — if the user already manually backported and just wants the template filled.

If anything is missing, ask the user with `ask_user` (one focused question at a time, with choices when the set of valid answers is small).

## Procedure

### 1. Validate the source PR

```powershell
$env:GH_PAGER = "cat"
gh pr view <source-pr> --repo microsoft/aspire --json number,title,state,mergedAt,body,author,labels,files,baseRefName,closingIssuesReferences
```

Check:

- `state` (`OPEN`/`CLOSED`/`MERGED`) and `mergedAt` (non-null when merged): if not yet merged, **warn the user**:
  > The source PR isn't merged yet. The bot will run as soon as the comment is posted and will backport whatever is on the PR head at that moment. Any new commits added to the source PR afterwards will not be auto-included in the backport — you'd need to re-trigger the `/backport` comment after they land (or after the PR is merged).

  Then ask whether to continue, abort, or wait until merged.
- `baseRefName` should be `main` (or another long-lived dev branch). Confirm with the user if it isn't.
- Confirm the target branch exists. URL-encode the slash in the branch name when calling `gh api`:

  ```powershell
  # Use the git refs endpoint, which handles slashes in branch names cleanly
  gh api "repos/microsoft/aspire/git/refs/heads/<target-branch>" --jq '.ref'
  ```

  If the call fails with 404, surface a clear error: "The target branch `<target-branch>` doesn't exist. Common typo, or the branch hasn't been cut yet."

### 2. Decide whether to trigger the backport

If the user already provided an existing backport PR number, **skip to step 5** (fill template directly).

Otherwise check whether the bot has already opened a backport PR, and whether a `/backport to <target>` comment was already posted.

```powershell
# Authoritative success check: has the bot already opened a backport PR for this (source, target) pair?
gh pr list --repo microsoft/aspire --state all `
  --head "backport/pr-<source-pr>-to-<target-branch>" `
  --json number,state,url,title

# Look for an existing /backport comment for this exact target (literal, not regex)
gh pr view <source-pr> --repo microsoft/aspire --json comments `
  --jq ".comments[] | select(.body | contains(\"/backport to <target-branch>\")) | {author: .author.login, body: .body, createdAt: .createdAt}"
```

Decide based on what you found:

- **An OPEN backport PR exists** for this `(source PR, target branch)` pair → surface its number/URL and **skip to step 5**.
- **A CLOSED or MERGED backport PR exists** → tell the user. Ask whether to (a) fill the template on that existing PR anyway, (b) reopen it, or (c) trigger a fresh attempt by posting `/backport` again. Don't silently re-trigger.
- **No backport PR, but a recent `/backport` comment exists with no failure response from the bot** → skip the comment posting, jump to step 4 (poll). Treat the existing comment as in-progress.
- **A previous `/backport` comment failed** (e.g., a `backporting … failed` comment from the bot) → ask the user whether to retry by posting a new `/backport` comment. Posting twice is allowed when the previous attempt failed; it isn't allowed when one is currently in-progress or already succeeded.
- **No prior trigger at all** → ask the user with `ask_user` to confirm posting the trigger comment. Show them the exact comment text that will be posted.

### 3. Post the `/backport to <target>` comment

Only after confirmation:

```powershell
gh pr comment <source-pr> --repo microsoft/aspire --body "/backport to <target-branch>"
```

Confirm to the user that the comment was posted and that the bot run will start shortly.

### 4. Wait for the bot to open the backport PR

Poll every **15 seconds**, up to **5 minutes total** (20 attempts). The **only authoritative success signal is a PR existing on the predictable head branch**. The bot does *not* post a comment with the new PR's link on success — only a "Started backporting" comment up front (with a workflow run link), and a comment on failure. So:

**Primary signal — PR exists with the predictable head branch (success):**

```powershell
gh pr list --repo microsoft/aspire --state all `
  --head "backport/pr-<source-pr>-to-<target-branch>" `
  --json number,url,state,title
```

If a PR is found, polling is done. Move to step 5.

**Secondary signal — bot comment on the source PR (in-progress / failure / workflow link):**

```powershell
gh pr view <source-pr> --repo microsoft/aspire --json comments `
  --jq ".comments[-10:][] | select(.body | (contains(\"backporting\") or contains(\"Started backporting\") or contains(\"failed\"))) | {author: .author.login, body: .body, createdAt: .createdAt}"
```

Bot author logins are typically `github-actions[bot]` or a GitHub App identity for `aspire-bot`. Match on **comment body content** (`Started backporting`, `backporting … failed`, `an error occurred while backporting`) rather than relying on author name alone — author identities can vary across runs.

A failure case to handle: if a comment matching `backporting … failed, the patch most likely resulted in conflicts. Please backport manually!` or `an error occurred while backporting` appears, **stop polling**, surface the message + workflow link to the user, and ask whether they want to:

- Manually resolve conflicts in a local checkout and provide the URL of the resulting backport PR so the skill can still fill the template, or
- Retry by posting `/backport` again (only useful if the underlying cause was transient — typically conflicts won't auto-resolve on retry).

If polling times out without any signal, tell the user clearly. Offer to keep waiting (another 5 min), open the workflow run page, or accept a manually-supplied backport PR URL.

### 5. Gather context for the template

Pull everything that will help draft the four sections:

```powershell
# Source PR details (title, body, files, linked issues)
gh pr view <source-pr> --repo microsoft/aspire `
  --json number,title,body,files,closingIssuesReferences,labels,additions,deletions

# Linked issues (use closingIssuesReferences from the JSON above; for each one):
gh issue view <issue-number> --repo microsoft/aspire --json number,title,body,labels
```

If the source PR body doesn't explicitly link an issue via `Fixes #N`/`Closes #N`, also scan the body for any issue references and ask the user which (if any) describe the customer-facing problem.

### 6. Draft the four template sections

Use the gathered context to draft each section. Keep drafts **concise and specific** — shiproom reviews many backports, so prefer a few focused sentences over long prose. Style examples from real backports:

- *Customer Impact:* "Customers get bad bicep generated that doesn't compile/deploy when doing something that should work."
- *Testing:* "New bicep baseline for the scenario." or "Existing unit tests + manual validation in TestShop playground."
- *Risk:* "Very low." (or Low / Medium / High with one-sentence justification)
- *Regression?:* "No" or "Yes — regressed in 13.2 by #<N>"

#### Drafting guidance per field

| Field | Prefer info from | Notes |
|-------|------------------|-------|
| **Customer Impact** | Linked issue body + source PR description | Describe the customer-visible symptom and who is affected, not the implementation. If the issue title is descriptive, lean on it. |
| **Testing** | Source PR description (look for "Testing"/"Validation" sections) and explicit statements about what was run | Require **explicit evidence** that tests ran/passed. Changed test files alone is not evidence — those tests may have been added but not validated against the release branch. If the source PR doesn't explicitly say what was tested, draft "Unknown — please confirm" rather than guessing. |
| **Risk** | Blast radius of the change: what areas/components it touches, whether it changes public API, behavior under concurrency, or shared infrastructure | Use **Very low / Low / Medium / High**. Diff size is a weak proxy — weigh the *behavioral surface* of the change. Default to **Low** for localized bug fixes, **Very low** for doc/cosmetic, escalate for cross-cutting changes, public API surface, or concurrency/lifetime changes. Always justify in one sentence. |
| **Regression?** | Explicit "regressed in vX.Y" statements in the source PR/issue, or labels that explicitly mark regressions (e.g., `regression`) | Labels like `Servicing-consider` indicate "considered for servicing", **not** that this is a regression. Without an explicit regression statement, draft "Unknown — please confirm." A confident "No" requires the source material to actually say so. |

### 7. Confirm with the user before writing

Show the user the drafted body and ask them to approve, edit, or replace each field. Use `ask_user` so they can either accept the draft or supply replacement text. Iterate field-by-field if needed; do not write the body until they approve.

### 8. Update the backport PR body

The bot's body looks like:

```text
Backport of #<N> to <target-branch>

/cc @<users>

## Customer Impact

## Testing

## Risk

## Regression?
```

If the existing body matches that shape (empty headers), **replace the empty section bodies with the drafted content**, leaving the header text and the `Backport of …` / `/cc` lines unchanged.

If the existing body is shaped differently (e.g., a manual backport PR with a description that doesn't include the four headers), **append** the four sections to the end of the existing body. Do not delete content the user wrote.

Write the new body to a temp file (`backport-body.md`) and update the PR:

```powershell
$env:GH_PAGER = "cat"
gh pr edit <backport-pr> --repo microsoft/aspire --body-file backport-body.md
Remove-Item backport-body.md
```

### 9. Wrap up

Tell the user:

- The backport PR URL.
- That the four template sections are filled.
- That shiproom can now review/approve.
- Any open follow-ups (e.g., conflicts that needed manual resolution, fields the user explicitly marked "Unknown — please confirm").

## Edge cases and notes

- **Idempotency.** Don't post `/backport to <target>` if there is an open backport PR for this `(source, target)` pair, or if a previous `/backport` comment is still in-progress with no failure response from the bot. Retries are allowed (with explicit user confirmation) when the previous attempt failed.
- **Multiple targets.** If the user asks to backport to several branches at once, treat each target as an independent run of this skill (one comment per target, one PR to fill per target).
- **Permissions.** The bot enforces that the commenter has `write` or `admin` access to the repo. If the user lacks permissions, the bot will fail; surface that clearly.
- **Locked source PRs.** The bot auto-unlocks. No special action needed.
- **Conflicts.** If the bot reports a `git am` conflict, do not attempt to resolve it for the user — the conflict resolution belongs in a real local checkout. Hand control back with the workflow link.
- **PR already exists.** If a force-pushed branch already exists, the bot updates it without opening a new PR. In that case the existing PR is the one to fill in.
- **Don't fabricate field values.** If the source PR/issue/diff doesn't justify a confident answer (especially for **Regression?** and **Risk**), draft "Unknown — please confirm" and ask the user, rather than guessing.
- **Privacy.** When summarizing customer impact, pull from the public PR/issue text only. Do not invent customer names or scenarios that aren't in the source material.

## Error handling

| Error | Action |
|-------|--------|
| `gh: command not found` | Tell the user to install `gh` from https://cli.github.com/. |
| `gh auth` not logged in | Tell the user to run `gh auth login`. |
| Source PR not merged | Warn per step 1 and ask whether to continue, abort, or wait. |
| Target branch missing | Surface the error and ask the user to confirm the branch name (typo? not snapped yet?). |
| `/backport` comment already posted, no PR yet | Skip posting; go straight to polling. |
| Bot reports `git am` conflict | Stop polling; show the workflow link; ask whether to fill template against a manually-created backport PR. |
| Polling timeout | Offer to keep waiting, point at the workflow run page, or accept a manually-supplied backport PR URL. |
| Backport PR body has unexpected shape | Append the four sections rather than overwriting. |

## What this skill does not do

- It does not **resolve merge conflicts** for you.
- It does not **approve or merge** the backport PR (that's shiproom's job).
- It does not **edit the source PR** beyond posting the trigger comment.
- It does not **decide whether a fix meets the bar** for backport — that's the user's call.
