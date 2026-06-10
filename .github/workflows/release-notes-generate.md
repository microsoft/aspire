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
    types: [published]
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
  # Stable releases are published by the `aspire-repo-bot` GitHub App. gh-aw's
  # activation gate checks the triggering actor's repo permission and GitHub
  # Apps do not appear as collaborators, so allow-list the App the same way as
  # other bot-triggered gh-aw workflows in this repo.
  bots: [aspire-repo-bot]

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

# Agent runs read-only; the release-body update is performed by a separate,
# permission-controlled job that gh-aw generates from the `safe-outputs:
# update-release` declaration below. Direct `contents: write` is disallowed
# by gh-aw strict mode — safe-outputs are the supported escape hatch.
permissions:
  contents: read
  pull-requests: read

network:
  allowed:
    - defaults
    - github

# Use the native `update-release` safe output instead of giving the agent
# write access to the GitHub MCP. The agent emits a structured update
# request; gh-aw runs a separate job (with the minimum required permissions)
# that calls the Releases REST API.
safe-outputs:
  update-release:
    max: 1

tools:
  github:
    # `repos` exposes get_release_by_tag / list_releases and commit-comparison
    # APIs. `pull_requests` and `search` are used to enrich commits with PR
    # titles/labels/authors when grouping changes by area. The actual release
    # body update is performed by the `update-release` safe output, not via
    # the MCP — so we do not need write toolsets here.
    toolsets: [repos, pull_requests, search]
    # Explicitly set to `none`: the `aspire-repo-bot` legitimately authors
    # backport PRs and merge commits on release branches. The default
    # `approved` filter would drop those items from the data set the agent
    # sees (commit-compare results, PR searches), hurting the generated
    # notes. The MCP container is still scoped to a single repo via
    # `allowed-repos` and `github-app`.
    min-integrity: none
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

The release is already published, so for **benign no-op cases** (release
missing, prerelease/draft, tag doesn't match `vX.Y.Z`, body has already
been edited and no longer contains the placeholder phrase) write a clear
diagnostic to the run summary and **exit successfully**. But for **real
errors** when actually trying to update the release (API rejection,
permission denied on `update_release`, malformed payload, etc.) **fail the
workflow** — a maintainer needs to see a red X so they can investigate or
manually backfill. Do **not** open issues to "ask a maintainer to paste
notes" as a fallback; the workflow has no `create_issue` capability by
design.

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

Exclude anything that is not user-facing. A change is user-facing only when
someone using Aspire can observe it in product behavior, supported APIs, CLI
commands, dashboard/extension UX, templates, integrations, documented
configuration, security posture, or meaningful compatibility/performance
behavior. Do not mention issues or PRs that only affect repository operation or
the engineering process.

Exclude noise that the existing notes also tend to exclude:

- Dependabot / dependency-bump PRs unless flagged as security.
- Branch / build / CI infrastructure changes that aren't user-facing.
- Internal refactors with no behavior change.
- Test-only changes.
- Agentic workflow, automation, release-engineering, changelog-generation, and
  repository maintenance changes unless the change has a direct user-visible
  effect in a released Aspire product.

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

This workflow has the `update-release` safe output configured. Emit a
single structured request as the final agent output, with the release ID
(or tag name) from Step 1 and the new `body`. Do not include `name`,
`tag_name`, `prerelease`, or `draft` — only the body should change.

gh-aw runs a separate, permission-controlled job after the agent that
calls the GitHub Releases REST API with the requested payload. If that
API call fails (HTTP 4xx/5xx, permission denied, malformed payload), the
safe-output job exits non-zero and the overall workflow shows a red X —
exactly the loud-failure behavior we want. You do not need to call the
Releases API directly via MCP; in fact you do not have write access to it.

After emitting the update request, write a short success line to the run
summary:

> Replaced placeholder release notes for **Aspire `<version>`**
> ([link](<release_url>)). Diff base: `<prev_tag>`. Sections: `<list>`.
> PRs summarized: `<count>`.

## Step 8: Failure handling

Two distinct failure classes — handle them differently:

**Benign no-op (exit successfully with a diagnostic, emit NO safe output).**
These are the early-exit cases already covered by Steps 1 and 2:

- Release not found by tag.
- `release.draft == true` or `release.prerelease == true`.
- Tag does not match `^v\d+\.\d+\.\d+$`.
- Release body has already been edited (does not contain the placeholder
  phrase) — Step 2 idempotency check.

For these, write a clear diagnostic to the run summary naming the release
and the reason, then exit 0 **without emitting an `update_release` safe
output**. The release is already live and these states are expected. The
safe-output job is a no-op when no request is emitted.

**Real error before reaching Step 7 (FAIL the workflow).** If the agent
hits an unrecoverable problem before it can produce a final body (search
APIs all failing, no compare data at all, malformed tag, etc.) — exit
non-zero so the run shows a red X. Write the failing API, tag, and error
message to the run summary so a maintainer can investigate. Do **not**
fall back to creating an issue, opening a PR, or posting a comment with
the generated notes — this workflow has only `update-release` configured
in `safe-outputs:`, all other write surfaces are unavailable by design.

**Real error during release update (handled by gh-aw).** If the body looks
correct but the Releases API itself rejects the update, the gh-aw safe
output job that calls the API will fail the run for you — you do not
need to do anything special beyond emitting a well-formed `update_release`
request.

A maintainer can rerun manually via `workflow_dispatch` with the same
`tag_name` once the underlying issue is fixed. The Step 2 idempotency
check makes that safe: if the body has already been edited, the rerun
will exit cleanly without overwriting.

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
