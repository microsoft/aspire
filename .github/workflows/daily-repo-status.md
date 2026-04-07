---
description: |
  Daily burndown report for the Aspire 13.2 milestone. Tracks progress
  on issues closed, new bugs found, notable changes merged into the
  release/13.2 branch, pending PR reviews, and discussions. Generates
  a 7-day burndown chart using cached daily snapshots.

on:
  schedule: daily around 9am
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read
  discussions: read

network: defaults

tools:
  github:
    toolsets: [repos, issues, pull_requests, discussions, search]
    lockdown: false
  cache-memory:

safe-outputs:
  create-issue:
    title-prefix: "[13.2-burndown] "
    labels: [report, burndown]
    close-older-issues: true
---

# 13.2 Release Burndown Report

Create a daily burndown report for the **Aspire 13.2 milestone** as a GitHub issue.
The primary goal of this report is to help the team track progress towards the 13.2 release.

## Data gathering

Collect the following data using the GitHub tools. All time-based queries should look at the **last 24 hours** unless stated otherwise.

### 1. Milestone snapshot

- Find the milestone named **13.2** in this repository.
- Count the **total open issues** and **total closed issues** in the milestone, **excluding pull requests**. Use an issues-only filter (for example, a search query like `is:issue milestone:"13.2" state:open` / `state:closed`) so the counts are consistent across tools.
- Store today's snapshot (date, open count, closed count) using the **cache-memory** tool with the key `burndown-13.2-snapshot`.
  - The value for this key **must** be a JSON array of objects with the exact shape:
    `[{ "date": "YYYY-MM-DD", "open": <number>, "closed": <number> }, ...]`
  - When writing today's data:
    1. Read the existing cache value (if any) and parse it as JSON. If the cache is empty or invalid, start from an empty array.
    2. If an entry for today's date already exists, **replace** it instead of adding a duplicate.
    3. If no entry exists, append a new object.
    4. Sort by date ascending and trim to the **most recent 7 entries**.
    5. Serialize back to JSON and overwrite the cache value.

### 1b. Historical burndown data (fallback)

If the cache from step 1 contains **fewer than 7 data points** after adding today's entry, fill in the missing days by reading previous burndown report issues:

- Search for issues (open or closed) with `[13.2-burndown]` in the title, sorted by creation date descending, limit to the most recent 7.
- For each previous report issue, find the **Milestone Progress** section and extract the **Open Issues** count from the table row labeled "Open Issues".
- Extract the date from the issue title (e.g., "Daily Burndown Report - February 19, 2026" → "2026-02-19").
- Merge these data points into the cache array, keeping only dates that are not already present (cache entries take priority).
- Sort by date ascending, trim to the most recent 7 entries, and save back to cache-memory.

Skip this step entirely if the cache already has 7 data points.

### 2. Issues closed in the last 24 hours (13.2 milestone)

- Search for issues in this repository that were **closed in the last 24 hours** and belong to the **13.2 milestone**.
- For each issue, note the issue number, title, and who closed it.

### 3. New issues added to 13.2 milestone in the last 24 hours

- Search for issues in this repository that were **opened in the last 24 hours** and are assigned to the **13.2 milestone**.
- Highlight any that are labeled as `bug` — these are newly discovered bugs for the release.

### 4. Notable changes merged into release/13.2

- Look at pull requests **merged in the last 24 hours** whose **base branch is `release/13.2`**.
- Summarize the most impactful or interesting changes (group by area if possible).

### 5. PRs pending review targeting release/13.2

- Find **open pull requests** with base branch `release/13.2` that are **awaiting reviews** (have no approving reviews yet, or have review requests pending).
- List them with PR number, title, author, and how long they've been open.

### 6. Discussions related to 13.2

- Search discussions in this repository that mention "13.2" or the milestone, especially any **recent activity in the last 24 hours**.
- Briefly summarize any relevant discussion threads.

### 7. General triage needs (secondary)

- Briefly note any **new issues opened in the last 24 hours that have no milestone assigned** and may need triage.
- Keep this section short — the focus is on 13.2.

## Burndown chart

Using the data from **cache-memory** (enriched with previous issue data if needed from step 1b), generate a **Mermaid xychart** showing the number of **open issues** in the 13.2 milestone over the last 7 days (or however many data points are available).

Use this format so it renders natively in the GitHub issue:

~~~
```mermaid
xychart-beta
    title "13.2 Milestone Burndown (Open Issues)"
    x-axis [Feb 13, Feb 14, Feb 15, ...]
    y-axis "Open Issues" 0 --> MAX
    line [N1, N2, N3, ...]
```
~~~

If fewer than 2 data points are available, note that the chart will become richer over the coming days as more snapshots are collected, and still show whatever data is available.

## Report structure

Create a GitHub issue with the following sections. Use these **exact section headers** in this **exact order** every time — do not rename, reorder, or omit any section:

1. `## 📊 Burndown Chart`
2. `## 📈 Milestone Progress`
3. `## ✅ Issues Closed Today`
4. `## 🐛 New Bugs Found`
5. `## 🚀 Notable Changes Merged`
6. `## 👀 PRs Awaiting Review`
7. `## 💬 Discussions`
8. `## 📋 Triage Queue`

If there is no activity for a section, keep the header and note that there was no activity.

## Style

- Be concise and data-driven — this is a status report, not a blog post
- Use tables for lists of issues and PRs where appropriate
- Use emojis for section headers to make scanning easy
- If there was no activity in a section, say so briefly (e.g., "No new bugs found today 🎉")
- End with a one-line motivational note for the team

## Process

1. Gather all the data described above
2. Read historical burndown data from cache-memory and store today's snapshot
3. Generate the burndown chart
4. Create a new GitHub issue with all sections populated
