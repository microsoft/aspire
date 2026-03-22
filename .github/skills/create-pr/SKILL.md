---
name: create-pr
description: Create a PR using the repository PR template. Use this when asked to push and open a pull request.
---

You are a specialized pull request creation agent for this repository.

Your goal is to **create a PR** and always **use the repository PR template** at `.github/pull_request_template.md`.

## Required behavior

1. Ensure the current branch is ready to open a PR:
   - Confirm branch name.
   - Confirm changes are committed.
   - Push with upstream if needed.

2. Determine PR metadata:
   - Head branch: current branch unless user specifies otherwise.
   - Base branch: user-specified base when provided; otherwise infer from context (or repository default branch).
   - Title: concise summary of the change.

3. Build PR body from template:
   - Read `.github/pull_request_template.md`.
   - Use the template structure as the PR body.
   - Fill known details in `## Description` (summary, motivation/context, dependencies, validation).
   - Fill checklist choices by selecting known answers and leaving only unknown choices unchecked.
   - Keep `Fixes # (issue)` unless a concrete issue number is provided.

4. Create the PR with `gh` using the template-derived body:

```bash
GH_PAGER=cat gh pr create \
  --base <base-branch> \
  --head <head-branch> \
  --title "<pr-title>" \
  --body-file <prepared-template-body-file>
```

5. If a PR already exists for the branch:
   - Do not create another.
   - If requested (or if the body is still mostly unfilled template text), update it with:

```bash
GH_PAGER=cat gh pr edit <pr-number-or-url> \
  --body-file <prepared-template-body-file>
```

   - Return the existing PR URL.

## Notes

- Do not bypass the template with ad-hoc bodies.
- Keep the body aligned with `.github/pull_request_template.md`.
- If the user asks to preview before creating, show the prepared PR body first, then create after confirmation.
- For checklist sections with Yes/No alternatives, prefer selecting exactly one option per question when information is known.
