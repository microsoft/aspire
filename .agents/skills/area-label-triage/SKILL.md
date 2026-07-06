---
name: area-label-triage
description: "Use when asked to find, triage, label, or queue recent microsoft/aspire issues or pull requests for CLI, Dashboard, or VS Code extension ownership, including unlabeled items and already area-labeled items with fresh updates needing Adam action."
---

# Aspire Area Label Triage

Label recent `microsoft/aspire` issues and PRs that clearly belong to `area-cli`, `area-dashboard`, or `area-vscode-extension`. Also find already-labeled items in those areas with fresh updates that need Adam to respond, triage, or review. List both sets in chat.

## Core rule

Prefer false negatives over bad labels or noisy action items. Add one of the target labels only when the item has direct evidence for that ownership area. For already-labeled items, include them only when there is a substantive update after Adam's last relevant action and Adam now owes a response, triage decision, or review. Skip ambiguous AppHost, template, integration, docs, telemetry, deployment, or general Aspire items unless the issue/PR clearly routes through the CLI, dashboard, or VS Code extension surface.

## Workflow

1. Verify the target labels exist:

   ```bash
   GH_PAGER=cat PAGER=cat gh label list --repo microsoft/aspire --search 'area-' --limit 200 \
     | grep -E '^area-(cli|dashboard|vscode-extension)[[:space:]]'
   ```

2. Fetch recent open issues and PRs that are missing an area label. If the user explicitly asks for items with zero labels, add `--no-label`; otherwise, "unlabeled" means missing an `area-*` label.

   ```bash
   GH_PAGER=cat PAGER=cat gh search issues --repo microsoft/aspire --state open \
     --sort updated --order desc --limit 80 \
     --json number,title,body,url,labels,updatedAt \
     --jq 'map(select((.labels // [] | map(.name) | any(startswith("area-"))) | not))' \
     -- -label:area-cli -label:area-dashboard -label:area-vscode-extension

   GH_PAGER=cat PAGER=cat gh search prs --repo microsoft/aspire --state open \
     --sort updated --order desc --limit 80 \
     --json number,title,body,url,labels,updatedAt \
     --jq 'map(select((.labels // [] | map(.name) | any(startswith("area-"))) | not))' \
     -- -label:area-cli -label:area-dashboard -label:area-vscode-extension
   ```

3. Filter out anything that already has any `area-*` label, even if it is not one of the three target labels. The search excludes only the target labels; do not add a second area label over an existing ownership decision.

4. Inspect likely matches before mutating:

   ```bash
   GH_PAGER=cat PAGER=cat gh issue view <number> --repo microsoft/aspire \
     --json number,title,body,comments,labels,url

   GH_PAGER=cat PAGER=cat gh pr view <number> --repo microsoft/aspire \
     --json number,title,body,files,labels,url
   ```

5. Apply labels with the command matching the item type:

   ```bash
   GH_PAGER=cat PAGER=cat gh issue edit <number> --repo microsoft/aspire --add-label area-cli
   GH_PAGER=cat PAGER=cat gh pr edit <number> --repo microsoft/aspire --add-label area-dashboard
   GH_PAGER=cat PAGER=cat gh pr edit <number> --repo microsoft/aspire --add-label area-vscode-extension
   ```

   Always include `--repo microsoft/aspire`; do not rely on the current checkout or Git remote.

6. Re-read labeled items or capture command output so the final chat list reflects successful mutations, not intended changes.

7. Fetch already-labeled recent updates in the owned areas. Use a 14-day window unless the user asks for a different range. These are action-queue candidates only; do not mutate them unless they also need an explicitly requested missing label correction.

   ```bash
   since="$(date -u -v-14d +%Y-%m-%d 2>/dev/null || date -u -d '14 days ago' +%Y-%m-%d)"

   for label in area-cli area-dashboard area-vscode-extension; do
     GH_PAGER=cat PAGER=cat gh search issues --repo microsoft/aspire --state open \
       --label "$label" --updated ">=$since" --sort updated --order desc --limit 80 \
       --json number,title,body,url,labels,updatedAt,commentsCount

     GH_PAGER=cat PAGER=cat gh search prs --repo microsoft/aspire --state open \
       --label "$label" --updated ">=$since" --sort updated --order desc --limit 80 \
       --json number,title,body,url,labels,updatedAt,commentsCount
   done
   ```

8. Inspect likely action candidates before listing them:

   ```bash
   GH_PAGER=cat PAGER=cat gh issue view <number> --repo microsoft/aspire \
     --json number,title,body,author,assignees,comments,labels,state,updatedAt,url

   GH_PAGER=cat PAGER=cat gh pr view <number> --repo microsoft/aspire \
     --json number,title,body,author,assignees,comments,reviews,reviewRequests,commits,files,labels,statusCheckRollup,state,updatedAt,url
   ```

## Label decision table

| Label | High-confidence evidence | Skip when |
| --- | --- | --- |
| `area-cli` | `aspire` CLI commands, acquisition/install/update scripts, CLI terminal output, `src/Aspire.Cli/`, `tests/Aspire.Cli*`, CLI E2E behavior | It is only an AppHost/runtime bug seen while running a CLI command |
| `area-dashboard` | Aspire Dashboard UI/pages, resources/logs/traces/metrics views, browser dashboard behavior, dashboard auth/telemetry display, `src/Aspire.Dashboard/`, `tests/Aspire.Dashboard*` | It is only backend telemetry production with no dashboard display behavior, or "dashboard" means a GitHub/reporting dashboard rather than the Aspire Dashboard product |
| `area-vscode-extension` | VS Code extension UI, command palette, debug/F5 flows, extension settings, extension logs/RPC/DCP/MCP behavior, `extension/` paths | It is only CLI behavior observed from a VS Code terminal |

Use comments and linked context when title/body alone are not enough. For PRs, changed files are usually stronger evidence than wording in the title.

## Confidence rule

Add a label only when at least one of these is true:

- The title/body explicitly names the target surface, such as "CLI", "dashboard", "VS Code extension", "F5", "command palette", or a specific `aspire` command.
- A PR changes files owned by exactly one target area.
- Existing comments from maintainers route the item to that surface.

Skip the item when:

- More than one area is plausible and no evidence breaks the tie.
- The item already has any `area-*` label.
- The only evidence is a broad word like "Aspire", "AppHost", "template", "telemetry", or "debug" without a target-surface clue.
- Labeling would require reading private context, running a repro, or making a product decision.

## Adam action rule for already-labeled items

List an already-labeled item only when the latest substantive human update is after Adam's last relevant comment, review, approval, or triage action and one of these is true:

| Item | Include when | Ignore when |
| --- | --- | --- |
| Issue | Reporter answered Adam's question, Adam is assigned/mentioned, the item needs owned-area triage, or a maintainer explicitly asks for Adam/area-owner input | Bot-only updates, label/milestone churn, "waiting on author", no new information after Adam's last response |
| PR | Adam is an explicit requested reviewer, new commits arrived after Adam's prior review in the owned area, author replied to Adam's review, CI failed after Adam approved, or Adam-authored PR has new comments/check failures | Already approved with no later human comments/commits, bot-only CI progress, unrelated files changed outside Adam's reviewed area |

Do not perform the response, review, or fix as part of this skill unless the user explicitly asks for execution. The required output is the action queue: item, why Adam owes action, and the next action.

## Quick reference

| Need | Command or action |
| --- | --- |
| Recent issues missing target labels | `gh search issues ... -- -label:area-cli -label:area-dashboard -label:area-vscode-extension` |
| Recent PRs missing target labels | `gh search prs ... -- -label:area-cli -label:area-dashboard -label:area-vscode-extension` |
| Already-labeled issue updates | `gh search issues --label <area> --updated ">=<date>" ...` |
| Already-labeled PR updates | `gh search prs --label <area> --updated ">=<date>" ...` |
| Exclude already-owned items | Filter out any returned item with a label beginning `area-` |
| Inspect issue context | `gh issue view <n> --json number,title,body,comments,labels,url` |
| Inspect PR context | `gh pr view <n> --json number,title,body,files,comments,reviews,reviewRequests,commits,labels,url` |
| Mutate issue | `gh issue edit <n> --repo microsoft/aspire --add-label <label>` |
| Mutate PR | `gh pr edit <n> --repo microsoft/aspire --add-label <label>` |

## Final chat output

Lead with the outcome and list every item that was successfully labeled, followed by already-labeled items where Adam owes action:

```markdown
Labeled 3 recent unlabeled Aspire items:

| Item | Label | Why |
| --- | --- | --- |
| issue #12345: <title> | `area-cli` | Repro is for `aspire add` failing before package restore. |
| PR #12346: <title> | `area-dashboard` | Changes only `src/Aspire.Dashboard/**` resource grid behavior. |
| issue #12347: <title> | `area-vscode-extension` | Report is about F5 debug launch from the Aspire VS Code extension. |

Already-labeled items needing Adam action:

| Item | Label | Why Adam | Next action |
| --- | --- | --- | --- |
| PR #12348: <title> | `area-vscode-extension` | Author pushed commits after Adam's prior extension debugger review. | Re-review changed extension debugger files. |
| issue #12349: <title> | `area-dashboard` | Reporter replied with the requested Dashboard repro details after Adam asked for them. | Respond or route to a fix/repro lane. |
```

If nothing was labeled:

```markdown
I did not label any recent unlabeled Aspire issues or PRs, and I did not find already-labeled CLI/Dashboard/VS Code extension items with fresh updates needing Adam action. The likely matches were either already handled, bot-only updates, already area-labeled with no Adam-owned follow-up, or did not have enough evidence for `area-cli`, `area-dashboard`, or `area-vscode-extension`.
```

## Common mistakes

| Mistake | Fix |
| --- | --- |
| Treating `no:label` as the only unlabeled case | Default to missing `area-*` labels unless the user asks for zero-label items. |
| Labeling every item that mentions `aspire run` as CLI | Label CLI only when the CLI behavior itself is the issue. Runtime/AppHost bugs often surface through CLI commands. |
| Adding a target label to an item with another `area-*` label | Skip it unless the user explicitly asks to correct existing labels. |
| Listing every recently updated area item as Adam action | Include only substantive human updates after Adam's last relevant action where Adam now owes response, triage, or review. |
| Treating bot CI updates as Adam action | Ignore bot-only progress unless CI failed after Adam approved or after an Adam-authored PR changed. |
| Using `gh issue edit` for PRs | Use `gh pr edit` for PRs and `gh issue edit` for issues. |
| Omitting `--repo microsoft/aspire` because the local checkout looks right | Include `--repo microsoft/aspire` on every `gh` command so the skill is safe from any directory or fork. |
| Mutating before inspecting likely matches | Inspect the full issue/PR context first; titles are often too vague. |
| Reporting intended labels | Re-read or rely on successful command output before listing final labels. |

## Red flags

Stop and skip the item if you catch yourself thinking:

- "This is probably dashboard because telemetry is mentioned."
- "The CLI command appears in the repro, so this must be CLI."
- "It has an existing `area-*` label, but this label also seems relevant."
- "This item was updated recently, so Adam must need to act."
- "A bot updated checks, so I should list it as needing Adam."
- "I do not have time to inspect the PR files."
- "The current repo is probably microsoft/aspire, so I can omit `--repo`."
- "I can list it as labeled even though the edit command failed."
