#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "Usage: apply_extension_release_trigger_label.sh <pr-number>" >&2
  exit 1
fi

pr_number="$1"
trigger_label="vscode-extension-release"

labels="$(gh pr view "$pr_number" --json labels --jq '.labels[].name')"
label_present=false
if printf '%s\n' "$labels" | grep -Fxq "$trigger_label"; then
  label_present=true
fi

if [[ "$label_present" == "true" ]]; then
  echo "Removing existing '$trigger_label' label from PR #$pr_number so re-adding it emits a fresh labeled event."
  if ! gh pr edit "$pr_number" --remove-label "$trigger_label"; then
    echo "::error::Failed to remove existing '$trigger_label' label from PR #$pr_number. The workflow must re-add the label to emit a fresh labeled event, so it cannot continue with a stale label."
    exit 1
  fi
fi

gh pr edit "$pr_number" --add-label "$trigger_label"
echo "Applied '$trigger_label' label to PR #$pr_number"
