---
description: |
  Daily "Repo Pulse" report for the Aspire repository. Updates a single
  pinned GitHub issue in place with a rolling 3-day view of recent repo
  activity: notable changes, recently merged PRs, recently opened PRs,
  recently filed issues, PRs awaiting review, and activity highlights.
  The goal is to give the team a live pulse of what's happening in the
  repo without creating a new issue every day.

on:
  schedule:
    - cron: "0 16 * * *"   # 16:00 UTC daily (08:00 PT in PST / 09:00 PT in PDT)
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read

network: defaults

tools:
  github:
    toolsets: [repos, issues, pull_requests, search]
    lockdown: false

safe-outputs:
  update-issue:
    body:
    title-prefix: "[repo-pulse]"
    target: "16404"
    max: 1
---

# Repo Pulse — Daily Report

Generate the daily **Repo Pulse** report for this repository by updating a
single, manually-created, pinned GitHub issue in place. The pinned issue is
identified by a hardcoded issue number below and is guarded by the
`[repo-pulse]` title prefix.

> **PINNED ISSUE NUMBER:** `16404`
>
> This is the hardcoded pinned issue number. The agent must emit exactly
> one `update_issue` output targeting this number.

## Primary goal

Update the pinned issue body with a fresh report covering the **last 3 days**
of activity across the whole repository, for **all contributors** (both team
members and community). This is a live dashboard, not a historical log — the
full body is regenerated every run.

## Process (two phases)

**Phase 1 — Gather data.** Run all the data-collection steps below *first*,
storing the results internally. Do not start composing the issue body until
every section's data has been gathered (or has explicitly failed).

**Phase 2 — Render the report.** Only after all data is gathered, compose
the issue body in the exact section order listed below and emit exactly one
`update_issue` output.

If any individual data-gathering step fails, continue with the remaining
steps. In the rendered body, the affected section should still appear with a
short note that the data could not be retrieved for this run.

**Always emit exactly one `update_issue` output**, even if some queries
failed. Use `operation: "replace"` — the full body is regenerated every run.
**Never emit** `missing_tool`, `missing_data`, or `noop`. If any query or
data-gathering step partially fails, report that only inside the regenerated
issue body using the per-section failure notes described above; do not use
any alternative safe-output to report partial failures.

## Data gathering (Phase 1)

All "last 3 days" queries mean the **last 72 hours** from the current run
time.

### 1. Recently merged PRs (last 3 days)

- Find pull requests in this repository that were **merged in the last 3
  days**, across all base branches.
- For each PR, capture: number, title, author, base branch, merged-at
  timestamp, linked issues, and labels.
- Sort by most-recently-merged first.
- **Cap at 25 items for display.** If the true count exceeds the cap, note
  the total at the bottom of the section and include a "See all" link to
  the equivalent GitHub search (URL-encoded), e.g.
  `https://github.com/${{ github.repository }}/pulls?q=is%3Apr+is%3Amerged+merged%3A%3E%3D<YYYY-MM-DD>`
  where `<YYYY-MM-DD>` is 3 days before the run date (UTC).

### 2. Recently opened PRs (last 3 days)

- Find pull requests **opened in the last 3 days** that are still **open**.
  Exclude draft PRs unless they have recent review activity.
- Capture: number, title, author, base branch, opened-at timestamp, labels,
  draft status.
- Sort by most-recently-opened first.
- **Cap at 25 items for display.** If truncated, include a "See all" link:
  `https://github.com/${{ github.repository }}/pulls?q=is%3Apr+is%3Aopen+created%3A%3E%3D<YYYY-MM-DD>`.

### 3. Recently filed issues (last 3 days)

- Find issues opened in this repository in the last 3 days (exclude pull
  requests).
- Capture: number, title, author, opened-at timestamp, labels.
- Highlight any labeled `bug` or `regression`.
- Sort by most-recently-opened first.
- **Cap at 25 items for display.** If truncated, include a "See all" link:
  `https://github.com/${{ github.repository }}/issues?q=is%3Aissue+created%3A%3E%3D<YYYY-MM-DD>`.

### 4. PRs awaiting review

- Find **open pull requests** (any age) that are **awaiting reviews** —
  either no approving reviews yet, or have requested reviewers who have not
  responded.
- Capture: number, title, author, base branch, opened-at timestamp, how long
  they've been open, requested reviewers (if any).
- Prioritize PRs that have been waiting longer.
- **Cap at 25 items for display.** If truncated, include a "See all" link:
  `https://github.com/${{ github.repository }}/pulls?q=is%3Apr+is%3Aopen+review%3Arequired`.

### 5. Activity highlights (issues / PRs with recent comment or review activity)

- Find issues or PRs that have received **at least 3 new comments or
  reviews in the last 3 days**, indicating active discussion.
- Capture: number, title, type (issue/PR), count of recent comments/reviews,
  latest activity timestamp.
- This section captures "where attention is going" regardless of when the
  item was originally opened.
- **Cap at 25 items for display.** If truncated, include a "See all" link
  to a GitHub search scoped to recently-updated items:
  `https://github.com/${{ github.repository }}/issues?q=updated%3A%3E%3D<YYYY-MM-DD>+sort%3Aupdated-desc`.

## Notable Changes (derived, Phase 2 only)

After all gathering is complete, select **3–5 items from the "Recently
Merged PRs" list ONLY** — do not introduce PRs that are not in that list —
that represent the most notable changes merged in the last 3 days.

Selection criteria (use objective signals, not title appeal):
- PRs that touch many files or cross multiple areas of the codebase.
- PRs linked to a tracked issue or a milestone.
- PRs labeled `breaking-change`, `feature`, `area-*` signaling meaningful
  work, or any priority label.
- PRs that fix regressions or high-impact bugs.
- Prefer **diversity of areas** — do not pick 5 PRs all in the same area.

For each selected PR, write a **one-sentence "why it matters"** explanation
grounded in the PR's own description, title, and linked issues. If you
cannot produce a confident one-liner, pick a different PR rather than
speculate. Never invent details not supported by the PR content.

If fewer than 3 merged PRs qualify, include what qualifies and note
explicitly that the list is shorter than usual.

## Report structure (Phase 2 — exact order)

Compose the pinned issue body with these **exact top-level headers in this
exact order**. Do not rename, reorder, or omit any section.

### Header block (top of body, before any section)

Include this freshness metadata block at the very top of the body, before
the first `##` heading:

```markdown
> 🤖 **Auto-generated by `.github/workflows/repo-pulse.md` — do not edit.**
> Your manual edits will be overwritten on the next run.
>
> - **Generated:** <UTC timestamp of this run, e.g. `2026-04-23 16:00 UTC`>
> - **Covers:** activity from the last 3 days
> - **Run:** [workflow run](https://github.com/${{ github.repository }}/actions/runs/${{ github.run_id }})
```

If any data-gathering step failed, add one more line immediately after the
"Run" line:

```markdown
> - ⚠️ **Partial run:** some sections could not be fully populated (see
>   section notes below).
```

### Sections

1. `## ⭐ Notable Changes`
2. `## 🔥 Recently Merged PRs`
3. `## 🚧 Recently Opened PRs`
4. `## 🐛 Recently Filed Issues`
5. `## 👀 PRs Awaiting Review`
6. `## 💬 Activity Highlights`

### Formatting rules

- Use concise Markdown tables for list sections (merged PRs, opened PRs,
  filed issues, awaiting review, activity highlights). Columns should
  typically be: `#`, `Title`, `Author`, `Age` (or `Merged`/`Opened`/`Latest
  activity`), and optional context like labels.
- Link PR/issue numbers (e.g. `[#12345](...)`).
- Prefer GitHub-relative `#12345` references so GitHub auto-links them where
  possible. When using full URLs, include them as Markdown links, not raw
  URLs.
- For `## ⭐ Notable Changes`, render each item as:
  - `### [#<PR number>](<url>) — <short title>`
  - one-sentence "why it matters" paragraph.
- **Every list section (🔥, 🚧, 🐛, 👀, 💬) must end with a "See all" link**
  to the equivalent GitHub search query, even when the list is not
  truncated. This gives readers a canonical query to explore the full set.
  When truncated, also include the raw count: e.g. *"Showing 25 of 47 —
  [see all](https://github.com/.../pulls?q=...)."*
- If a section has no data, keep the header and write a single italic line
  such as *"No activity in the last 3 days."*
- If a section failed to gather data, keep the header and write
  *"⚠️ Could not retrieve data for this section in this run."*
- Keep the overall body compact; the issue body is regenerated every day so
  brevity matters.

## Guardrails

- **Do not create any new issues, PRs, comments, or labels.** The only
  output for this workflow is a single `update_issue` against the hardcoded
  pinned issue number.
- **Do not target any other issue.** The `title-prefix: "[repo-pulse]"`
  safe-output guard will reject updates to issues whose titles do not start
  with `[repo-pulse]`, but the prompt-level rule is to emit the hardcoded
  number and nothing else.
- **Do not fabricate** PRs, issues, contributors, or statistics. All content
  must trace back to data gathered in Phase 1.
- **Do not rely on caches.** Re-gather every run; the pinned issue is the
  source of truth.

## Output format

Emit exactly one safe-output of type `update_issue`:

```json
{
  "type": "update_issue",
  "issue_number": 16404,
  "operation": "replace",
  "body": "<full regenerated issue body including the freshness header and all sections>"
}
```
