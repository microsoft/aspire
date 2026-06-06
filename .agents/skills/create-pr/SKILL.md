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
- **Labels**: add labels only when they are clearly applicable. Use the `breaking-change` label when the PR breaks public APIs or fundamentally changes the behavior of an existing scenario. Do not use it for every behavior change, additive feature, routine bug fix, or implementation-only change. Examples from existing `breaking-change` issues include API shape/semantics changes, obsoleting a public API, changing endpoint allocation/wait behavior, changing Docker Compose publish behavior, and disabling local auth for Azure resources.

### 3. Detect non-trivial UI changes

Before building the PR body, check whether the diff includes **non-trivial UI changes** to any of these areas:

- **Dashboard** (`src/Aspire.Dashboard/`): changes to `.razor`, `.razor.cs`, `.css`, `.js`, or UI assets under `src/Aspire.Dashboard/wwwroot/` (e.g., `img/**`, `favicon.ico`) that alter layout, add/remove components, change interactive behavior, or modify visual appearance beyond minor text or spacing tweaks.
- **CLI** (`src/Aspire.Cli/`): changes to command output formatting, interactive prompts, table/list rendering, spinners, progress indicators, or colored output beyond simple message text changes.
- **VS Code Extension** (`extension/`): changes to webview panels, tree views, status bar items, quick pick UIs, editor decorations, contributed view configuration (`package.json`), or UI assets (`resources/**`) beyond minor label text changes.

A change is **non-trivial** if it does more than:
- Fix a typo or update a string literal without altering layout
- Adjust a single CSS property (e.g., margin, padding) without changing visual structure
- Change a tooltip or aria-label

If non-trivial UI changes are detected, add a prominent `### Screenshots / Recordings` subsection in the PR body (under `## Description`) with the following content:

```markdown
### Screenshots / Recordings

> **This PR includes UI changes.** Please add screenshots or screen recordings so reviewers can evaluate the visual changes without running locally.
>
> - For before/after comparisons, place them side-by-side or label them clearly.
> - For interactive changes (animations, transitions, new flows), prefer a short screen recording (GIF or video).
> - If you cannot capture visuals now, note what scenario to test and mark this section as TODO.

<!-- Add screenshots/recordings here -->
```

### 4. Build PR body from template

- Read `.github/pull_request_template.md`.
- Use the template structure as the PR body.
- Fill known details in `## Description` with reviewer- and user-facing context:
  - Lead with **why** the change matters: the user problem, scenario, or workflow it improves.
  - Summarize the user-visible behavior before implementation details: what users can now do, see, configure, or call.
  - Include implementation details only after the behavior summary, and keep them concise.
  - Evaluate whether the change makes security assumptions or guarantees, and include details only when security review may be needed.
  - Include relevant validation: tests, manual verification, screenshots, recordings, generated help, or sample output.
- For infra-only or internal-only changes, such as CI, build infrastructure, repository automation, tests, docs-only maintenance, or skill/workflow guidance, do not add user-facing usage artifacts, `### Breaking changes`, or `### Security considerations` unless the change also affects user-visible behavior, breaks a public API or established scenario, or requires security review.
- When the change affects user-facing behavior, add a subsection such as `### User-facing usage` or `### Examples` under `## Description` with concrete usage examples. Prefer examples from the diff, tests, docs, generated output, or commands you actually ran. Do not invent usage; if the usage cannot be determined confidently, ask the user or state that an example is not available.
- Include the most relevant user-facing artifacts by change type:
  - **Dashboard/UI changes**: include dashboard screenshots, preferably before/after when visual behavior changes.
  - **CLI changes**: include command-specific `--help` output, example invocations with named arguments/options, and an asciinema recording link when practical.
  - **Public API changes**: include a consumer-focused usage example for the new API.
  - **Integration changes**: include both C# and TypeScript usage examples when applicable.
  - **Configuration, template, or docs changes**: include before/after snippets, generated output, or the command a user runs.
- Include a `### Security considerations` subsection only when the security checklist would be marked as needing security review because the change makes security assumptions or guarantees. Call out any relevant implications, such as:
  - New network listeners, outbound connections, exposed ports, proxying, or service discovery behavior.
  - Files written to global, shared, profile, cache, or temporary directories.
  - Script execution, generated commands, shell escaping, or script/content injection risks.
  - Path construction, archive extraction, file uploads/downloads, or path traversal risks.
  - Untrusted user input, data deserialization, authentication/authorization changes, secrets, credentials, certificates, tokens, or environment variables.
  - Container execution, process spawning, permissions, or elevated privileges.
- Do not add a `### Security considerations` subsection for changes that do not need security review; instead, keep the checklist answer aligned with that assessment.
- If the PR uses the `breaking-change` label, include a short `### Breaking changes` subsection that explains who is affected, what existing API or scenario changes, and how users should update.
- Example PR body snippet shapes. Treat these as formats only; replace every placeholder with exact screenshots, commands, APIs, generated output, and security facts from the PR:
  - **Dashboard/UI changes**:
    ```markdown
    ### User-facing usage
    The dashboard now shows <new state or action> on the <page/panel>, so users can <outcome> without <old workaround>.
    Screenshot: ![Dashboard showing <feature>](<uploaded screenshot URL>)
    ```
  - **CLI changes**:
    ````markdown
    ### User-facing usage
    Users can run the command with named options:
    ```bash
    aspire <command> <resource> --name <command-name> --timeout 30s
    ```
    Command help:
    ```text
    Usage:
      aspire <command> <resource> [options]
    Options:
      --name <name>        <describe option>
      --timeout <value>    <describe option>
    ```
    Recording: <asciinema URL, if available>
    ````
  - **Public API changes**:
    ````markdown
    ### User-facing usage
    Consumers can configure <scenario> with the new API:
    ```csharp
    var resource = builder.Add<Integration>("resource")
                          .With<NewCapability>("<value>");
    ```
    ````
  - **Integration changes**:
    ````markdown
    ### User-facing usage
    C# AppHost:
    ```csharp
    var resource = builder.Add<Integration>("resource")
                          .With<NewCapability>("<value>");
    ```
    TypeScript AppHost:
    ```typescript
    const resource = builder.add<Integration>("resource")
      .with<NewCapability>("<value>");
    ```
    ````
  - **Configuration, template, or docs changes**:
    ````markdown
    ### User-facing usage
    Users enable the behavior with:
    ```json
    {
      "<settingName>": "<value>"
    }
    ```
    Generated output now includes `<observable output>`.
    ````
  - **Security-review changes**:
    ```markdown
    ### Security considerations
    This change <opens a listener/writes to a shared directory/executes generated commands/accepts untrusted input>. Security review is needed to confirm <specific concern>, such as host binding, path normalization, command escaping, or secret handling.
    ```
  - **Breaking changes**:
    ```markdown
    ### Breaking changes
    This changes <existing API or scenario>. Users who currently <old usage> should update to <new usage or migration guidance>.
    ```
- Fill checklist choices by selecting known answers and leaving only unknown choices unchecked.
- Keep `Fixes # (issue)` unless a concrete issue number is provided.
- Write the body to a temporary file named `pr-body.md` in the repo root.

### 5. Create the PR

Set `GH_PAGER` to `cat` to prevent interactive paging, then create the PR. The syntax differs by shell:

If the PR needs labels, add the matching label flags to the create command. For breaking public API changes or fundamental existing-scenario behavior changes, include `--label breaking-change`.

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

### 6. Handle existing PRs

If a PR already exists for the branch:
- Do not create another.
- If requested (or if the body is still mostly unfilled template text), update it:

  **bash:** `GH_PAGER=cat gh pr edit <pr-number-or-url> --body-file pr-body.md`

  **PowerShell:** `$env:GH_PAGER = "cat"; gh pr edit <pr-number-or-url> --body-file pr-body.md`

- If a label needs to be applied to an existing PR, use `gh pr edit <pr-number-or-url> --add-label <label-name>`.

- Return the existing PR URL.

### 7. Clean up

After you are completely finished creating or updating the PR (after step 5 and, if needed, step 6), delete the temporary body file:
- **bash:** `rm pr-body.md`
- **PowerShell:** `Remove-Item pr-body.md`

## Error handling

| Error | Action |
|-------|--------|
| `gh: command not found` | Tell the user to install `gh` from https://cli.github.com/ |
| `gh auth` not logged in | Tell the user to run `gh auth login` |
| `git push` rejected | Inform the user; do not force-push without explicit permission |
| PR already exists | Follow step 6 (Handle existing PRs) above |

## Notes

- Do not bypass the template with ad-hoc bodies.
- Keep the body aligned with `.github/pull_request_template.md`.
- If the user asks to preview before creating, show the prepared PR body first, then create after confirmation.
- For checklist sections with Yes/No alternatives, prefer selecting exactly one option per question when information is known.
- **After creating the PR**, if non-trivial UI changes were detected in step 3, alert the user with a message like: "This PR includes non-trivial UI changes to [Dashboard/CLI/Extension]. Please add screenshots or screen recordings to the PR description so reviewers can evaluate the visual changes without running locally." Include the PR URL so the user can edit it directly.
