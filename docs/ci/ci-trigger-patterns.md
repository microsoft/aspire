# CI Trigger Patterns

## Overview

The file `eng/testing/github-ci-trigger-patterns.txt` lists glob patterns for files whose changes do **not** require the full CI to run.

When a pull request is opened or updated, the CI workflow (`ci.yml`) checks whether **all** changed files match at least one pattern in the file. If they do, the workflow is skipped (no build or test jobs run). This keeps CI fast for changes that only affect documentation, pipeline configuration, or unrelated workflow files.

> **Note:** This mechanism applies only to **pull requests**. Pushes to `main` or `release/*` branches always run the full CI pipeline. The `check-changed-files` action explicitly rejects non-`pull_request` events.

## Why a Separate File?

Previously the patterns were inlined in `.github/workflows/ci.yml`. Any change to that file (even just adding a new pattern to skip CI) would trigger CI on itself. Moving the patterns to `eng/testing/github-ci-trigger-patterns.txt` decouples pattern maintenance from the workflow definition.

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

1. Open `eng/testing/github-ci-trigger-patterns.txt`.
2. Add one pattern per line, optionally preceded by a comment.
3. Submit a PR — CI will not run for that PR if all changed files match the patterns.

> **Tip:** Changing the patterns file itself is listed as a skippable change (`eng/testing/github-ci-trigger-patterns.txt`), so a PR that only updates this file will not trigger CI.

## How It Works

The `.github/actions/check-changed-files` composite action:

1. Reads `eng/testing/github-ci-trigger-patterns.txt` from the checked-out repository.
2. Converts each glob pattern to an anchored ERE (Extended Regular Expression) regex:
   - `**` → `.*`
   - `*` → `[^/]*`
   - `.` and other regex metacharacters (`+`, `?`, `[`, `]`, `(`, `)`, `|`) → escaped with `\`
3. For every file changed in the PR, checks whether the file path matches at least one of the converted regexes.
4. Outputs `only_changed=true` when every changed file matched, allowing the calling workflow to skip further jobs.

## Related Files

- `eng/testing/github-ci-trigger-patterns.txt` — the patterns file described on this page
- `.github/actions/check-changed-files/action.yml` — the composite action that reads and evaluates the patterns
- `.github/workflows/ci.yml` — the CI workflow that calls the action
