---
description: |
  When a stable Aspire release is published in `microsoft/aspire`, replace the
  placeholder release notes body (the one written by
  `.github/workflows/release-github-tasks.yml`) with auto-generated notes
  based on the diff between the new tag and the previous stable tag. The
  generated notes match the tone, section layout, headings, and emoji usage
  of recent stable releases — this workflow does not invent a new format.

on:
  release:
    types: [released]
  workflow_dispatch:
    inputs:
      tag_name:
        description: "Release tag to process (e.g. v13.3.2). Required for manual runs."
        required: true
        type: string
  # The placeholder body emitted by release-github-tasks.yml is the durable
  # signal we look for at runtime; we don't need gh-aw's stale-check guard
  # to bail us out, and the lock file shouldn't drift just because the
  # frontmatter changed.
  stale-check: false

if: >-
  github.repository == 'microsoft/aspire'
  && (
    (github.event_name == 'release' && github.event.release.prerelease == false && github.event.release.draft == false)
    || github.event_name == 'workflow_dispatch'
  )

# Serialize runs for the same tag so that a duplicate release event (or a
# manual workflow_dispatch rerun on top of an in-flight automatic run) can't
# race and double-edit the release body.
concurrency:
  group: release-notes-generate-${{ github.event.release.tag_name || github.event.inputs.tag_name }}
  cancel-in-progress: false

permissions:
  contents: read
  pull-requests: read

network:
  allowed:
    - defaults
    - github

tools:
  github:
    # `repos` exposes get_release_by_tag / list_releases / update_release and
    # commit-comparison APIs. `pull_requests` and `search` are used to enrich
    # commits with PR titles/labels/authors when grouping changes by area.
    toolsets: [repos, pull_requests, search]
    # Keep the guard policy explicit so gh-aw does not inject a separate
    # auto-lockdown github-script step with an independently resolved action
    # pin. Mirrors release-update-support-mdx.md.
    min-integrity: approved
    allowed-repos:
      - microsoft/aspire
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire"]

timeout-minutes: 20
---

# Generate release notes for a new stable Aspire release

The GitHub Release for this tag was just created by
`.github/workflows/release-github-tasks.yml` with a short placeholder body.
Your job is to replace that placeholder with real, human-readable release
notes that match the tone and structure of recent stable Aspire releases.

This run is best-effort. The release is already published; the placeholder
already tells users "notes are being generated". If anything in this
workflow is unsafe to proceed with, **write a clear diagnostic to the run
summary and exit successfully** rather than failing the run.

## Context

- **Repository**: `microsoft/aspire`
- **Trigger event**: `${{ github.event_name }}`
- **Release tag**: `${{ github.event.release.tag_name || github.event.inputs.tag_name }}`
- **Release name**: `${{ github.event.release.name }}`

## Step 1: Resolve the release

Determine the tag this run is processing:

- If `github.event_name == 'release'`, the tag is in `${{ github.event.release.tag_name }}`.
- If `github.event_name == 'workflow_dispatch'`, the tag is in `${{ github.event.inputs.tag_name }}`.

Fetch the full release record for `microsoft/aspire` by tag
(`GET /repos/microsoft/aspire/releases/tags/<tag>`). Capture its `id`,
`tag_name`, `name`, `body`, `published_at`, `html_url`, `draft`, and
`prerelease`.

**Exit successfully with a diagnostic** if any of these are true (do not
fail the run — the release is already live):

- The release can't be found.
- `release.draft == true` or `release.prerelease == true`.
- The tag does not match `^v\d+\.\d+\.\d+$`.

Parse the version: strip the leading `v` and split into
`MAJOR.MINOR.PATCH`. Remember the `MAJOR.MINOR.PATCH` and the
`MAJOR.MINOR` for later steps.

## Step 2: Idempotency — only proceed if the body is still the placeholder

The placeholder body emitted by `release-github-tasks.yml` contains the
literal phrase:

> Release notes are being generated automatically

If the current `release.body` does **not** contain that phrase, a human (or
an earlier run of this workflow) has already replaced the body. **Exit
successfully** with a diagnostic naming the release and stop. Do not
overwrite human-edited notes.

If the body does contain that phrase, continue.

## Step 3: Find the previous stable tag

List releases on `microsoft/aspire` (`GET /repos/microsoft/aspire/releases?per_page=100`),
filter to entries where `prerelease == false` and `draft == false`, and
sort by `published_at` descending. The previous stable release is the
**first** entry whose `tag_name` is **not** equal to the current tag and
whose `published_at` is **earlier** than the current release's
`published_at`.

If no previous stable release exists (this is the very first stable
release), fall back to producing simple notes from the latest commits on
the tag — see Step 6 for the fallback shape.

Capture the previous release's `tag_name` (for diffing) and `body` (for
style reference, alongside two more recent stable bodies — see Step 4).

## Step 4: Learn the existing release-notes format

Read the `body` of up to the **three most recent stable releases** before
the new one (including the previous tag from Step 3). Treat these as the
authoritative style template. Note in particular:

- Top-level heading shape (e.g. `# Aspire 13.3.2` vs `## What's New in Aspire 13.3.2`).
- Section headings used and their order (e.g. `### 🐛 Fixes`, `### 🚀 Features`, `### 🧪 Experimental`, etc.).
- Emoji usage in section headers and bullet entries.
- Level of detail per bullet (one-liner vs short paragraph).
- Whether bullets reference PRs by number, author, or both.
- Whether there's a closing summary, contributor list, or links section.

**Do not invent a new format.** Match what's already there. If patch
releases (`X.Y.Z` where `Z > 0`) and minor/major releases (`X.Y.0`) use
different shapes in the existing notes, pick the shape matching the new
release's role.

## Step 5: Gather the change set between previous and new tag

Use the compare API:

```
GET /repos/microsoft/aspire/compare/<prev_tag>...<new_tag>
```

This returns commits and (optionally) merged-PR metadata. For each commit
that corresponds to a merged pull request, capture: PR number, title,
labels, author. Use the `pull_requests` and `search` toolsets to enrich
when needed — for example, querying `is:pr is:merged repo:microsoft/aspire
merged:<from>..<to> base:<branch>` to cross-check PR titles, areas, and
labels.

Group changes by **the same area dimensions the previous notes use**. If
the previous notes group by area (e.g. "Hosting", "Dashboard", "CLI",
"Azure integrations", "Components"), group by that. If they group only by
type (Features / Fixes / etc.), group by type. Use PR labels (`area-*`,
`feat`, `fix`, `breaking-change`, `security`) as hints but defer to the
existing structure.

Exclude noise that the existing notes also tend to exclude:

- Dependabot / dependency-bump PRs unless flagged as security.
- Branch / build / CI infrastructure changes that aren't user-facing.
- Internal refactors with no behavior change.
- Test-only changes.

If the change set is large (more than ~80 PRs), summarize aggressively and
keep only user-facing entries; the notes should be scannable.

## Step 6: Draft the new release body

Produce a body that:

- Opens the same way recent notes do (e.g. `# Aspire <version>` or
  `## What's New in Aspire <version>` — match the existing top-level
  heading shape).
- For **patch releases**, leads with a one-sentence summary describing
  what the patch addresses. For **minor / major releases**, leads with a
  short paragraph summarizing the headline themes, mirroring how prior
  `*.Y.0` releases open.
- Groups changes under the same section headings the previous notes use,
  in the same order, with the same emoji.
- Cites PRs as `(#NNNN)` (or the same form prior notes used) and credits
  the PR author with `@login` if that's the established pattern.
- Preserves any standard trailing content (e.g. a "Full Changelog" link,
  contributors list, links section) if recent notes include it. Compute
  the Full Changelog link as
  `https://github.com/microsoft/aspire/compare/<prev_tag>...<new_tag>`.

Fallback (no previous stable release exists): generate a minimal body that
just lists the last 30 commits on the tag with their first-line summaries,
grouped only by `### 🚀 Highlights` and `### 🛠 Other changes`. Note in the
body that this is the first stable release.

The release **name / title** stays as `Aspire <version>` — do not change
it. Only the body changes.

## Step 7: Update the release in place

Call `update_release` on the `microsoft/aspire` release with `id` from
Step 1, supplying only the new `body`. Do not touch `name`, `tag_name`,
`prerelease`, or `draft`.

After updating, write a short success line to the run summary:

> Replaced placeholder release notes for **Aspire `<version>`**
> ([link](<release_url>)). Diff base: `<prev_tag>`. Sections: `<list>`.
> PRs summarized: `<count>`.

## Step 8: Failure handling

If any GitHub API call fails (rate limit, transient network, App
permission, missing release), **do not fail the workflow**. Write a clear
diagnostic to the run summary explaining what happened (which API, which
tag, the error message) and exit successfully. A maintainer can rerun
manually via `workflow_dispatch` with the same `tag_name` — the
idempotency check in Step 2 will keep that safe.

## Style notes for the generated body

- Be concise. Bullets, not paragraphs, for individual changes.
- Use the project's tone: factual, lightly enthusiastic on themes, no
  marketing copy.
- Don't editorialize about PRs you can't classify — drop them.
- Don't include the placeholder phrase ("Release notes are being
  generated automatically…") anywhere in the new body.
- Preserve the trailing `*Full commit: [...]*` line if the placeholder
  included one — read the placeholder before discarding it, extract that
  line if present, and append it (or its full-changelog equivalent) at
  the bottom of the new body.
