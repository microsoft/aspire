---
description: |
  When a stable Aspire release is published in `microsoft/aspire`, draft a pull
  request in `microsoft/aspire.dev` that updates the support policy page
  (`src/frontend/src/content/docs/support.mdx`). Patch releases on the
  currently supported version update the row in the "Supported versions"
  table; major or minor releases demote the previously supported version into
  the "Out of support versions" table and replace the supported row; backport
  servicing patches on already-out-of-support versions update the matching
  out-of-support row. The "Last updated" date badge at the top of the file is
  refreshed in all three cases.

on:
  release:
    types: [released]
  workflow_dispatch:
    inputs:
      tag_name:
        description: "Release tag to process (e.g. v13.2.4). Required for manual runs."
        required: true
        type: string
  stale-check: false

if: >-
  github.repository == 'microsoft/aspire'
  && (
    (github.event_name == 'release' && github.event.release.prerelease == false && github.event.release.draft == false)
    || github.event_name == 'workflow_dispatch'
  )

# Serialize runs so two near-simultaneous releases (for example a patch followed
# minutes later by a minor) cannot open conflicting support-page PRs at once.
# The agent prompt also reconciles against any open `[support]` automation PR
# at runtime; concurrency alone is not sufficient.
concurrency:
  group: release-update-support-mdx
  cancel-in-progress: false

checkout:
  # Use aspire.dev as the current workspace because that is where the
  # support.mdx edit lives, and keep a mirrored checkout under _repos so the
  # safeoutputs create_pull_request handler can reliably rediscover the target
  # repo in multi-repo mode. Mirrors the pattern used by pr-docs-check.md.
  - repository: microsoft/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]
    current: true
  - repository: microsoft/aspire.dev
    path: _repos/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]

permissions:
  contents: read
  pull-requests: read

network:
  allowed:
    - defaults
    - github

tools:
  github:
    toolsets: [repos, pull_requests]
    # Keep the guard policy explicit so gh-aw does not inject a separate
    # auto-lockdown github-script step with an independently resolved action
    # pin. Mirrors pr-docs-check.md.
    min-integrity: approved
    allowed-repos:
      - microsoft/*
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev", "aspire"]

safe-outputs:
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire.dev", "aspire"]
  steps:
    - name: Mirror target repo checkout
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      uses: actions/checkout@v6.0.2
      with:
        repository: microsoft/aspire.dev
        ref: main
        token: ${{ steps.safe-outputs-app-token.outputs.token }}
        persist-credentials: false
        path: _repos/aspire.dev
        fetch-depth: 1
    - name: Configure mirrored target repo Git credentials
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      working-directory: _repos/aspire.dev
      env:
        REPO_NAME: "microsoft/aspire.dev"
        SERVER_URL: ${{ github.server_url }}
        GIT_TOKEN: ${{ steps.safe-outputs-app-token.outputs.token }}
      run: |
        git config --global user.email "github-actions[bot]@users.noreply.github.com"
        git config --global user.name "github-actions[bot]"
        git config --global am.keepcr true
        SERVER_URL_STRIPPED="${SERVER_URL#https://}"
        git remote set-url origin "https://x-access-token:${GIT_TOKEN}@${SERVER_URL_STRIPPED}/${REPO_NAME}.git"
        echo "Mirrored checkout configured with standard GitHub Actions identity"
  create-pull-request:
    title-prefix: "[support] "
    labels: [docs-from-code]
    reviewers: "IEvangelist"
    draft: true
    base-branch: main
    # support.mdx only lives on aspire.dev's main branch, so disallow
    # release/* base overrides.
    allowed-base-branches:
      - main
    target-repo: "microsoft/aspire.dev"
    protected-files: blocked
    # Keep failures quiet on aspire.dev. Diagnostics for fail-closed paths
    # surface in the workflow run summary; a maintainer can re-run via
    # workflow_dispatch with the tag.
    fallback-as-issue: false

timeout-minutes: 15
---

# Update aspire.dev support page for a new Aspire release

Update `src/frontend/src/content/docs/support.mdx` on `microsoft/aspire.dev`
to reflect the Aspire release that triggered this run, and open a draft
pull request with the change.

## Context

- **Source repository**: `microsoft/aspire`
- **Trigger event**: `${{ github.event_name }}`
- **Release tag**: `${{ github.event.release.tag_name || github.event.inputs.tag_name }}`
- **Release name**: `${{ github.event.release.name }}`

The release's `published_at` timestamp and `html_url` are not interpolated
into this prompt — fetch them in Step 1 using the GitHub API
(`GET /repos/microsoft/aspire/releases/tags/<tag>`).

> [!NOTE]
> The agent runs with `microsoft/aspire.dev` as the current workspace and also
> has a mirrored checkout at `_repos/aspire.dev`. Use GitHub tools for any
> cross-repo lookups (release metadata on `microsoft/aspire`, open PRs on
> `microsoft/aspire.dev`).
>
> For security, this workflow only auto-activates for stable, non-prerelease
> releases on `microsoft/aspire`. Manual `workflow_dispatch` runs require a
> `tag_name` input.

## Step 1: Resolve release context

Determine the tag this run is processing:

- If `github.event_name == 'release'`, the tag is in
  `${{ github.event.release.tag_name }}`.
- If `github.event_name == 'workflow_dispatch'`, the tag is in
  `${{ github.event.inputs.tag_name }}`.

Then call the GitHub API for `microsoft/aspire` to fetch the full release
record by tag (`GET /repos/microsoft/aspire/releases/tags/<tag>`). Read its
`tag_name`, `name`, `published_at` (ISO 8601 UTC), `html_url`, `draft`, and
`prerelease` fields from the API response — do **not** rely on event-payload
fields beyond `tag_name` and `name`.

**Fail closed** (stop and write a diagnostic to the run summary) if any of
these are true:

- The release is not found.
- `release.draft == true`.
- `release.prerelease == true`.

Validate the tag against the regex `^v\d+\.\d+\.\d+$`. If it doesn't match,
fail closed.

Parse the version: strip the leading `v` and split into `MAJOR.MINOR.PATCH`.

Convert `release.published_at` (ISO 8601 UTC) into a calendar date string in
the en-US format `Month DD, YYYY` (for example, `April 22, 2026`). Use the
**UTC** calendar date — not local or Pacific time. This is the codified
policy for determinism. Note: the day of the month must not be zero-padded
(use `April 9, 2026`, not `April 09, 2026`); month name is the full English
name.

## Step 2: Read and structurally validate `support.mdx`

In the current `microsoft/aspire.dev` workspace, open
`src/frontend/src/content/docs/support.mdx` and confirm all of the following:

1. There is exactly one `<Badge text="📆 Last updated: <date>" variant="tip" size="large" />` line.
2. There is a `## Supported versions` section heading followed by exactly one
   markdown table whose header row reads
   `| Version | Original release date | Latest patch version | Patch release date | End of support |`
   (column widths/padding may vary; the **column titles** must match exactly)
   and whose body has **exactly one** data row.
3. There is a `## Out of support versions` section heading followed by exactly
   one markdown table with the same five-column header.

If any of these structural assumptions fail, **fail closed**: write a clear
diagnostic to the run summary explaining what was found and what was expected,
and stop. Do not attempt to repair the file or guess; a human reviewer should
look.

## Step 3: Classify the release

From the single supported row, parse the `MAJOR.MINOR` of the currently
supported version (for example, `Aspire 13.2` → `13.2`).

Compare numerically against the new release's `MAJOR.MINOR`:

- **Same `MAJOR.MINOR`** → **patch-on-supported** path.
- **New `MAJOR.MINOR` is greater** (lexicographically by `(MAJOR, MINOR)`
  tuple) → **major/minor bump** path.
- **New `MAJOR.MINOR` is less** → **backport servicing patch** path.

For the **backport servicing patch** path, locate the matching `Aspire X.Y`
row in the Out of support versions table. If no matching row exists, **fail
closed** with a diagnostic.

## Step 4: Reconcile against any open automation PR (stale-state guard)

Before applying any edit, search `microsoft/aspire.dev` for **open** pull
requests whose title starts with `[support] ` and which modify
`src/frontend/src/content/docs/support.mdx`.

- If **zero** such PRs are open, proceed to Step 5.
- If **exactly one** such PR is open:
  - Fetch its head ref's version of `support.mdx`. If that file already
    reflects the target release state (badge date and table rows match what
    you would produce in Step 5), this run is a duplicate. **No-op**: write
    "support.mdx already reflects Aspire `<version>` on the open PR
    `<url>`" to the run summary and stop without emitting a safe output.
  - Otherwise the existing PR represents a different (likely earlier) release
    that has not been merged yet. **Fail closed**: write a diagnostic naming
    the open PR and the new release, and stop. A maintainer should reconcile
    manually — applying this run's edits on top of stale `main` could demote
    the wrong supported version.
- If **more than one** such PR is open, **fail closed** with a diagnostic
  listing all of them.

## Step 5: Apply the edit in the workspace

Apply exactly one of the three edit shapes below. In every case, also update
the `<Badge text="📆 Last updated: <pubdate>" .../>` line so the badge text
shows the new release date.

Preserve the existing column padding (number of spaces) in every table row
you touch so the diff stays minimal and the table stays visually aligned.

### 5a. Patch-on-supported path

Rewrite **only** the `Latest patch version` and `Patch release date` cells
of the single Supported row. Set `Latest patch version` to the new full
version (for example `13.2.4`) and `Patch release date` to the formatted
release date.

### 5b. Major/minor bump path

1. Read the current Supported row verbatim (including its `Latest patch
   version` and `Patch release date` cells, which represent the last patch
   shipped before this bump).
2. Insert that row at the **top** of the data rows in the Out of support
   versions table. Carry over its `Version`, `Original release date`,
   `Latest patch version`, and `Patch release date` cells unchanged. Set its
   `End of support` cell to the formatted release date of the new release.
3. Replace the Supported row with the new release. The cells are:
   - `Version`: `Aspire MAJOR.MINOR` (no patch)
   - `Original release date`: the formatted release date
   - `Latest patch version`: `MAJOR.MINOR.PATCH` (the full new version)
   - `Patch release date`: the formatted release date
   - `End of support`: `When next major or minor version is released.`
   (note the trailing period)

### 5c. Backport servicing patch path

Rewrite **only** the `Latest patch version` and `Patch release date` cells
of the matching `Aspire X.Y` row in the Out of support versions table. Set
`Latest patch version` to the new full version and `Patch release date` to
the formatted release date. Leave its `End of support` cell unchanged.

## Step 6: Idempotency check

After applying the edit, diff the workspace against the base branch (`main`).

- If there are **no** changes, write
  "support.mdx already reflects Aspire `<version>`; no PR" to the run summary
  and stop without emitting a safe output.
- Otherwise continue to Step 7.

## Step 7: Open the draft PR

Emit a `create_pull_request` safe output with:

- **Title** (the `[support] ` prefix is added automatically):
  `Update support page for Aspire <full-version> release`
  (for example, `Update support page for Aspire 13.2.4 release`).
- **Base branch**: `main`.
- **Body** with:
  - A prominent link to the source release:
    `Drafts the support-page update for [microsoft/aspire@<tag>](<release_url>).`
  - One line classifying the release: `Patch on supported version`,
    `Major/minor bump (demoting Aspire X.Y to out-of-support)`, or
    `Backport servicing patch (Aspire X.Y is already out of support)`.
  - The note: `Dates resolved from the GitHub release `published_at`
    timestamp interpreted in **UTC**.`
  - A bullet listing the modified file (`src/frontend/src/content/docs/support.mdx`).
  - A reviewer checklist:
    - Confirm the badge date matches the release date.
    - Confirm the table rows reflect the intended classification.
    - Confirm column alignment is preserved.

The `safe-outputs.create-pull-request` configuration sets `draft: true`,
`reviewers: IEvangelist`, the `[support] ` title prefix, the `docs-from-code`
label, and locks the base to `main`. You only need to supply the title and
body content.
