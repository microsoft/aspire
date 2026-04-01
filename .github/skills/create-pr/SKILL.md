---
name: create-pr
description: 'Create a pull request using the repository PR template. Use when asked to: create PR, open PR, push and create PR, submit PR, open pull request, send changes for review.'
---

You are a specialized pull request creation agent for this repository.

Your goal is to **create a PR** and always **use the repository PR template** at `.github/pull_request_template.md`.

## Example requests

- "Create a PR for this change"
- "Push and open a pull request"
- "Submit a PR with these fixes"
- "Open a PR against the release branch"

## Prerequisites

Before starting, verify:
- `gh` CLI is available: run `gh --version`. If missing, tell the user to install it from https://cli.github.com/.
- Authentication is configured: run `gh auth status`. If not authenticated, tell the user to run `gh auth login`.

## Procedure

### 1. Prepare the branch

- Confirm the current branch name with `git branch --show-current`.
- Ensure changes are committed (`git status` should show a clean working tree or only untracked files).
- Push the branch with `git push -u origin <branch-name>`. If the push is rejected, inform the user (do not force-push without explicit permission).

### 2. Determine PR metadata

- **Head branch**: current branch unless the user specifies otherwise.
- **Base branch**: user-specified base when provided; otherwise infer from context (or use the repository default branch).
- **Title**: concise summary of the change.

### 3. Build PR body from template

- Read `.github/pull_request_template.md`.
- Use the template structure as the PR body.
- Fill known details in `## Description` (summary, motivation/context, dependencies, validation).
- Fill checklist choices by selecting known answers and leaving only unknown choices unchecked.
- Keep `Fixes # (issue)` unless a concrete issue number is provided.
- Write the body to a temporary file named `pr-body.md` in the repo root.

### 4. Create the PR

Set `GH_PAGER` to `cat` to prevent interactive paging, then create the PR. The syntax differs by shell:

**bash/Linux/macOS:**
```bash
GH_PAGER=cat gh pr create \
  --base <base-branch> \
  --head <head-branch> \
  --title "<pr-title>" \
  --body-file pr-body.md
```

**PowerShell/Windows:**
```powershell
$env:GH_PAGER = "cat"
gh pr create `
  --base <base-branch> `
  --head <head-branch> `
  --title "<pr-title>" `
  --body-file pr-body.md
```

> **Why `GH_PAGER=cat`?** The `gh` CLI pipes long output through a pager (like `less`) by default, which blocks in non-interactive terminals. Setting it to `cat` disables paging so output prints directly.

> **Shell differences:** `VAR=val command` is bash syntax for setting an env var for a single command. PowerShell requires a separate `$env:VAR = "val"` statement (persists for the session, which is harmless here).

### 5. Handle existing PRs

If a PR already exists for the branch:
- Do not create another.
- If requested (or if the body is still mostly unfilled template text), update it:

  **bash:** `GH_PAGER=cat gh pr edit <pr-number-or-url> --body-file pr-body.md`

  **PowerShell:** `$env:GH_PAGER = "cat"; gh pr edit <pr-number-or-url> --body-file pr-body.md`

- Return the existing PR URL.

### 6. Clean up

After you are completely finished creating or updating the PR (after step 4 and, if needed, step 5), delete the temporary body file:
- **bash:** `rm pr-body.md`
- **PowerShell:** `Remove-Item pr-body.md`

## Error handling

| Error | Action |
|-------|--------|
| `gh: command not found` | Tell the user to install `gh` from https://cli.github.com/ |
| `gh auth` not logged in | Tell the user to run `gh auth login` |
| `git push` rejected | Inform the user; do not force-push without explicit permission |
| PR already exists | Follow step 6 above |

## Notes

- Do not bypass the template with ad-hoc bodies.
- Keep the body aligned with `.github/pull_request_template.md`.
- If the user asks to preview before creating, show the prepared PR body first, then create after confirmation.
- For checklist sections with Yes/No alternatives, prefer selecting exactly one option per question when information is known.
