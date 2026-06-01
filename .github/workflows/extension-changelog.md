---
description: |
  When `.github/workflows/extension-release.yml` opens a VS Code extension
  release PR, it writes a placeholder entry into `extension/CHANGELOG.md` and
  applies the `vscode-extension-release` label. This agentic workflow detects
  that placeholder, generates polished, user-facing release notes from the
  extension commit range recorded in the placeholder marker, and replaces the
  placeholder on the PR branch via a safe output. The generated notes match the
  tone and structure of recent `extension/CHANGELOG.md` entries — this workflow
  does not invent a new format, and it never invents changes that aren't backed
  by commits in the range.

on:
  pull_request:
    # `labeled` is the only trigger: `.github/workflows/extension-release.yml`
    # applies the `vscode-extension-release` label after opening the PR, using a
    # GitHub App token so the event fires. Recovery (if a run fails or is missed)
    # is simply removing and re-adding the label. The native `names` label filter
    # below means only that label fires this workflow, and the placeholder-marker
    # check in the body makes every run idempotent. We deliberately avoid
    # `synchronize` so the safe-output push (which re-triggers `pull_request`)
    # can never form a loop.
    types: [labeled]
    # gh-aw native label filter (only applies to labeled/unlabeled events): fire
    # only when the triggering label matches. Unmatched label events are Skipped.
    names: vscode-extension-release
  # The placeholder marker in extension/CHANGELOG.md is the durable signal we
  # look for at runtime; we don't need gh-aw's stale-check guard, and the lock
  # file shouldn't drift just because the frontmatter changed.
  stale-check: false
  # The `labeled` event is fired by the `aspire-repo-bot` GitHub App (the release
  # bot mints its token in extension-release.yml to apply the label so the event
  # fires at all). gh-aw's activation gate checks the triggering actor's repo
  # permission and, finding the App is not a collaborator, would otherwise mark
  # the run `insufficient_permissions` and skip the agent. Allow-listing the bot
  # here compiles to `GH_AW_ALLOWED_BOTS`, so the membership check authorizes the
  # App (after confirming it is installed/active on the repo) instead of the
  # default human-role check. The slug must match the App that mints the label
  # token (secrets.ASPIRE_BOT_*); see docs/release-process.md for `aspire-repo-bot`.
  bots: [aspire-repo-bot]

# Only run for the canonical repo. Fork-sourced `pull_request` runs don't get
# secrets, so the GitHub App token mint (and therefore the agent and the
# safe-output push) cannot succeed from a fork; the branch-prefix validation in
# the body is a further guard.
if: github.repository == 'microsoft/aspire'

# Serialize runs for the same PR so a duplicate label event can't race and
# double-edit the changelog.
concurrency:
  group: extension-changelog-${{ github.event.pull_request.number }}
  cancel-in-progress: false

# Agent runs read-only; the changelog edit is pushed by a separate,
# permission-controlled job that gh-aw generates from the
# `safe-outputs: push-to-pull-request-branch` declaration below. Direct
# `contents: write` is disallowed by gh-aw strict mode.
permissions:
  contents: read
  pull-requests: read

network:
  allowed:
    - defaults
    - github

# Use the native `push-to-pull-request-branch` safe output instead of giving the
# agent write access. The agent edits extension/CHANGELOG.md in its checkout and
# emits a structured request; gh-aw runs a separate job (with the minimum
# required permissions, authenticated as the Aspire bot app) that commits and
# pushes the change to the triggering PR's head branch. Pushing with the app
# token (rather than GITHUB_TOKEN) lets the PR's required checks re-run on the
# new commit.
safe-outputs:
  push-to-pull-request-branch:
    max: 1
    # Skip gh-aw's branch-protection pre-flight check. By default this safe output
    # reads branch protection before pushing, which makes gh-aw request
    # `administration: read` on the minted aspire-repo-bot app token. That scope is
    # NOT granted to the App installation, so the `Generate GitHub App token` step
    # fails with a 422 ("The permissions requested are not granted to this
    # installation") and the changelog is never pushed. The PR head we push to is a
    # short-lived release branch created moments earlier by extension-release.yml and
    # is never protected, so the pre-flight check has no value here. Disabling it
    # drops `administration: read` from the token request, leaving only the already
    # granted `contents: write` + `pull-requests: write`.
    # See https://github.com/github/gh-aw push_to_pull_request_branch.go (check-branch-protection).
    check-branch-protection: false
    # Fail (don't just warn) if the agent emits a push with no diff. Every
    # legitimate run that gets this far edits extension/CHANGELOG.md (even the
    # "no user-facing changes" case rewrites the placeholder), and the benign
    # no-op paths (Step 1/Step 2) emit no safe output at all — so a push request
    # that produces no change indicates a bug and should surface loudly rather
    # than silently leaving the placeholder in place.
    if-no-changes: error
    # Restrict the push to exactly extension/CHANGELOG.md (matched by full path
    # via glob). Combined with the protected-files manifest, this means a
    # prompt-injected or misbehaving agent cannot push edits to any other file.
    allowed-files:
      - extension/CHANGELOG.md
    # gh-aw's safe-output push guard protects a manifest of well-known files by
    # basename (package.json, global.json, README.md, CHANGELOG.md, ...). This
    # workflow's entire purpose is to edit extension/CHANGELOG.md, so exclude that
    # one basename from the guard. Every other protected file stays protected, so
    # a misbehaving run still can't touch package.json, global.json, etc.
    protected-files:
      exclude:
        - CHANGELOG.md
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire"]

tools:
  github:
    # `repos` exposes the commit-comparison and file-content APIs used to gather
    # the extension change set. `pull_requests` and `search` enrich commits with
    # PR titles/labels/authors. The changelog write is performed by the
    # `push-to-pull-request-branch` safe output, not via the MCP, so no write
    # toolsets are needed here.
    toolsets: [repos, pull_requests, search]
    # The release bot legitimately authors merge commits on the branches we
    # compare. The default `approved` filter would drop those from the data set
    # the agent sees, hurting the generated notes. The MCP container is still
    # scoped to a single repo via `allowed-repos` and the github-app token.
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

# Generate the VS Code extension changelog for a release PR

`.github/workflows/extension-release.yml` just opened (or updated) a VS Code
extension release pull request. It bumped `extension/package.json` and wrote a
**placeholder** entry at the top of `extension/CHANGELOG.md`. Your job is to
replace that placeholder with real, user-facing release notes that match the
tone and structure of recent `extension/CHANGELOG.md` entries.

The repository is already checked out at the head of the triggering PR's
branch, so you can read `extension/CHANGELOG.md` directly from the workspace and
edit it in place. The edit is committed and pushed for you by the
`push-to-pull-request-branch` safe output — do **not** try to push or open a PR
yourself; you do not have write access.

## Context

- **Repository**: `microsoft/aspire`
- **Trigger event**: `${{ github.event_name }}`
- **PR number**: `${{ github.event.pull_request.number }}`

The PR head branch name is **not** provided here directly (it is attacker-influenced
data and is deliberately not interpolated into this prompt). Use the GitHub MCP
`get` pull request tool with the PR number above to read the PR's head branch
(`head.ref`) and other metadata.

## Step 1: Validate this is a real extension release PR

Using the GitHub MCP, fetch this pull request (PR number above) and read its head
branch name and base branch name. Confirm **both**:

- the PR head branch begins with `extension-release/`, and
- the PR base branch is exactly `main`.

If either check fails, this label was applied to a PR that is not a bot-created
extension release. Write a short diagnostic to the run summary and **exit
successfully without emitting any safe output** — do not edit anything.

## Step 2: Idempotency — only proceed if the placeholder is still present

Read `extension/CHANGELOG.md` from the workspace. The placeholder block written
by the release workflow looks like this (the SHAs and version vary):

```
## v1.10.1

<!-- aspire-ext-changelog from=<40-hex-sha> to=<40-hex-sha> base=<version-or-empty> -->
_Release notes are being generated automatically and will replace this placeholder shortly. ..._
```

The authoritative sentinel is the HTML marker comment that starts with
`<!-- aspire-ext-changelog`. Apply these rules:

- If the file contains **no** `aspire-ext-changelog` marker, a human or an
  earlier run already replaced the placeholder. Write a diagnostic to the run
  summary and **exit successfully without emitting any safe output**. (This can
  happen if the label is re-applied after the placeholder was already replaced —
  handle it cheaply and stop before doing any other work.)
- If the file contains **more than one** `aspire-ext-changelog` marker,
  something is wrong (a malformed or tampered changelog). **Fail the workflow**
  with a clear diagnostic; do not guess which one to replace.
- If there is **exactly one** marker, continue.

## Step 3: Parse and validate the marker

The single marker has the form:

```
<!-- aspire-ext-changelog from=<FROM_SHA> to=<TO_SHA> base=<BASE_VERSION> -->
```

Extract `from`, `to`, and `base`. Validate strictly — this is untrusted file
content:

- `from` and `to` must each be exactly 40 lowercase hex characters
  (`^[0-9a-f]{40}$`). If either is malformed, **fail the workflow** with a
  diagnostic.
- `base` may be empty (no Marketplace baseline was resolved) or a semantic
  version like `1.10.0`.
- Verify both `from` and `to` are real commits in `microsoft/aspire` (e.g. via
  `GET /repos/microsoft/aspire/commits/<sha>` or the compare API). If either
  commit cannot be resolved in `microsoft/aspire`, **fail the workflow** — do
  not fall back to an inferred range. The deterministic range recorded in the
  marker is the single source of truth.

Also capture the new version from the placeholder heading (`## v<version>`) so
your replacement keeps the same heading.

## Step 4: Gather the extension change set

Use the compare API to get the commits between the validated SHAs:

```
GET /repos/microsoft/aspire/compare/<from>...<to>
```

You only care about changes under the `extension/` directory. For each commit
that touches `extension/` and corresponds to a merged pull request, capture: PR
number, title, labels, author. Use the `pull_requests` and `search` toolsets to
enrich when helpful (for example `is:pr is:merged repo:microsoft/aspire` queries
to confirm PR titles and labels). You may also read the changed file list /
diff under `extension/` to understand what actually changed.

Exclude noise that a user-facing changelog should not mention:

- Dependency-bump / Dependabot PRs unless they fix a user-visible security issue.
- Build, CI, and test-only changes with no user-facing effect.
- Internal refactors with no behavior change.
- Version-bump and release-prep commits (including this PR's own commit).

## Step 5: Learn the existing changelog format

Read the existing entries already in `extension/CHANGELOG.md` (the ones below
the placeholder) and treat the most recent two or three as the authoritative
style template. Match:

- The heading shape (the placeholder already uses `## v<version>` — keep it).
- The section headings used and their order, if any (e.g. `### Features`,
  `### Fixes`).
- Bullet style and level of detail (one concise line per change).
- Whether bullets reference PRs by number (e.g. `(#NNNN)`) and/or credit authors.
- When a change has both a tracking issue and an implementation pull request, keep
  the references distinct and use the correct GitHub URL type for each
  (`/issues/` for issues, `/pull/` for pull requests). Do not replace a
  user-facing issue reference with only the implementation PR number if the PR
  title or body makes the issue the canonical tracking item.

**Do not invent a new format.** Match what is already there. Keep bullets
concise, factual, and user-facing — describe what changed for someone using the
extension, not the internal implementation.

## Step 6: Replace the placeholder block in place

Edit `extension/CHANGELOG.md` in the workspace so that:

- The `## v<version>` heading is preserved.
- The entire placeholder body — **including the `<!-- aspire-ext-changelog ... -->`
  marker comment and the italic "_Release notes are being generated…_" line** —
  is removed and replaced with your generated notes. The marker MUST NOT survive
  in the final file; if it did, a later run (e.g. from the label being
  re-applied) would have no reliable way to tell the work was already done.
- The final Markdown contains no multiple consecutive blank lines (`\n\n\n`),
  which would fail the repository's Markdownlint `MD012/no-multiple-blanks`
  required check.
- All other existing entries below are left untouched.

If, after excluding noise in Step 4, there are **no** user-facing changes,
replace the placeholder body with a single line such as
`- No user-facing changes in this release.` under the version heading (match how
prior maintenance-only entries are phrased if any exist).

## Step 7: Emit the safe output

This workflow has the `push-to-pull-request-branch` safe output configured. After
editing `extension/CHANGELOG.md`, emit a single push request as your final
output so gh-aw commits the change to the triggering PR's head branch with a
clear commit message (for example
`Generate extension changelog for v<version>`). Do not include any other file in
the change. Then write a short success line to the run summary:

> Replaced the placeholder `extension/CHANGELOG.md` entry for **v`<version>`**
> on PR #`${{ github.event.pull_request.number }}`. Range: `<from>`..`<to>`.
> Entries: `<count>`.

## Step 8: Failure handling

Two distinct outcomes — handle them differently:

**Benign no-op (exit successfully, emit NO safe output).**

- The PR head branch does not start with `extension-release/`, or the PR base
  branch is not `main` (Step 1).
- `extension/CHANGELOG.md` no longer contains the marker (Step 2) — already done.

For these, write a clear diagnostic to the run summary and exit 0 **without
emitting a `push-to-pull-request-branch` safe output**. The safe-output job is a
no-op when no request is emitted.

**Real error (FAIL the workflow).**

- More than one marker present (Step 2).
- Malformed `from`/`to` SHAs, or SHAs that don't resolve in `microsoft/aspire`
  (Step 3).
- The compare API or required searches fail outright so you cannot produce notes.

For these, exit non-zero so the run shows a red X, and write the failing API,
the marker contents, and the error to the run summary so a maintainer can
investigate. Do **not** fall back to opening an issue, posting a comment, or any
other write — this workflow has only `push-to-pull-request-branch` configured in
`safe-outputs:`; all other write surfaces are unavailable by design. The
deterministic commit list in the PR description remains as the human fallback,
and a maintainer can re-trigger this workflow by removing and re-adding the
`vscode-extension-release` label once the underlying issue is fixed.
