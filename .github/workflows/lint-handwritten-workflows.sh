#!/usr/bin/env bash
# Lints the handwritten GitHub Actions workflows with the `actionlint` on PATH.
#
# Both ci.yml ("Lint handwritten workflows") and update-actionlint.yml (which
# proves a candidate release before proposing a bump) call this script, so the
# blocking gate and the updater always lint the same file set.
#
# Run from the repository root. Lints top-level .github/workflows/*.yml and
# *.yaml files and excludes gh-aw generated output:
#   - *.lock.yml                  compiled agentic workflows
#   - agentics-maintenance*.yml   gh-aw side-repo maintenance workflows
# Generated workflows are validated by `gh aw compile` instead.
#
# The shellcheck and pyflakes integrations are disabled so that results do not
# depend on whichever versions the runner image happens to ship.
#
# Exits non-zero when actionlint reports errors or when no files match, so a
# moved or renamed workflow directory cannot silently pass the gate.
set -euo pipefail

files=()
while IFS= read -r file; do
  files+=("$file")
done < <(
  find .github/workflows -maxdepth 1 -type f \
    \( -name '*.yml' -o -name '*.yaml' \) \
    ! -name '*.lock.yml' \
    ! -name 'agentics-maintenance*.yml' |
    LC_ALL=C sort
)

if [[ ${#files[@]} -eq 0 ]]; then
  echo "::error::No handwritten workflow files found under .github/workflows." >&2
  exit 1
fi

echo "Linting ${#files[@]} handwritten workflow files."
actionlint -shellcheck= -pyflakes= "${files[@]}"
