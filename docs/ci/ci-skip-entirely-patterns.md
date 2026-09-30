# CI Skip-Entirely Patterns

## Overview

The file `eng/github-ci/ci-skip-entirely-patterns.txt` lists glob patterns for files whose changes do **not** require the full CI to run. This is the top-level skip gate, not the selective-test router (see [`test-trigger-map.md`](test-trigger-map.md) for path → test/job routing).

When a pull request is opened or updated, the CI workflow (`ci.yml`) first checks whether **all** changed files match at least one skip pattern. It then checks `eng/github-ci/infrastructure-test-input-patterns.txt` for source-traced inputs to `Infrastructure.Tests`:

- changes with no semantic test consumer skip the workflow;
- semantic infrastructure changes run the normal test selector without stabilization;
- semantic infrastructure mixed with source/build changes runs stabilization normally.

This keeps documentation and unrelated infrastructure changes cheap without silently skipping semantic tests.

> **Note:** This mechanism applies only to **pull requests**. Pushes to `main` or `release/*` branches always run the full CI pipeline. The `check-changed-files` action explicitly rejects non-`pull_request` events.

## Why a Separate File?

Previously the patterns were inlined in `.github/workflows/ci.yml`. Any change to that file (even just adding a new pattern to skip CI) would trigger CI on itself. Moving the patterns to `eng/github-ci/ci-skip-entirely-patterns.txt` decouples pattern maintenance from the workflow definition.

## Pattern Syntax

Patterns use a simple **glob** style:

| Syntax | Meaning |
|--------|---------|
| `**`   | Matches any path including directory separators (recursive) |
| `*`    | Matches any characters except a directory separator        |
| `.`    | Treated as a literal dot — no backslash escaping needed    |

All other characters (letters, digits, `-`, `_`, `/`, etc.) are treated as literals.

Lines starting with `#` and blank lines are ignored.

### Examples

```text
# All Markdown files anywhere in the repo
**.md

# All files under eng/pipelines/ recursively
eng/pipelines/**

# A specific file
eng/test-configuration.json

# Workflow files matching a glob (e.g. labeler-promote.yml, labeler-train.yml)
.github/workflows/labeler-*.yml
```

## How to Add a New Pattern

To add files whose changes should not trigger CI:

1. Open `eng/github-ci/ci-skip-entirely-patterns.txt`.
2. Add one pattern per line, optionally preceded by a comment.
3. If a skipped path has a semantic `Infrastructure.Tests` consumer, add the narrow source-traced pattern to `eng/github-ci/infrastructure-test-input-patterns.txt` and route it in `eng/github-ci/test-trigger-map.yml`.
4. Submit a PR. Changes with no semantic consumer skip CI; semantic inputs run their selected tests.

## How It Works

The `.github/actions/check-changed-files` composite action:

1. Reads `eng/github-ci/ci-skip-entirely-patterns.txt` from the checked-out repository.
2. Converts each glob pattern to an anchored ERE (Extended Regular Expression) regex:
   - `**` → `.*`
   - `*` → `[^/]*`
   - `.` and other regex metacharacters (`+`, `?`, `[`, `]`, `(`, `)`, `|`) → escaped with `\`
3. For every file changed in the PR, checks whether the file path matches at least one of the converted regexes.
4. Outputs the changed, matched, and unmatched file sets.

`ci.yml` runs the action against both pattern files, then
`eng/github-ci/classify-ci-changes.sh` decides whether the workflow can be
skipped and whether stabilization is required. Push events do not use this PR
classifier and retain the existing full-CI behavior.

## Related Files

- `eng/github-ci/ci-skip-entirely-patterns.txt` — the patterns file described on this page
- `eng/github-ci/infrastructure-test-input-patterns.txt` — source-traced semantic inputs that override the skip decision
- `eng/github-ci/classify-ci-changes.sh` — computes the workflow and stabilization decisions
- `.github/actions/check-changed-files/action.yml` — the composite action that reads and evaluates the patterns
- `.github/workflows/ci.yml` — the CI workflow that calls the action
