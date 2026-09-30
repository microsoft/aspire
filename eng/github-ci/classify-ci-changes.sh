#!/usr/bin/env bash

set -euo pipefail

if [[ "$#" -ne 3 ]]; then
  echo "Usage: $0 <changed-files-json> <skippable-files-json> <semantic-files-json>" >&2
  exit 2
fi

changed_files="$1"
skippable_files="$2"
semantic_files="$3"

for value in "$changed_files" "$skippable_files" "$semantic_files"; do
  if ! jq -e 'type == "array" and all(.[]; type == "string")' <<< "$value" > /dev/null; then
    echo "All arguments must be JSON arrays of file paths." >&2
    exit 2
  fi
done

changed_files=$(jq -c 'unique' <<< "$changed_files")
skippable_files=$(jq -c 'unique' <<< "$skippable_files")

# Generated agentic workflows have their own path-triggered validation workflow.
# The broad handwritten-workflow pattern intentionally reaches this classifier,
# then these generated outputs are removed from normal CI ownership.
semantic_files=$(jq -c '
  [
    .[]
    | select(
        (
          (startswith(".github/workflows/") and endswith(".lock.yml"))
          or (startswith(".github/workflows/agentics-maintenance") and endswith(".yml"))
        )
        | not
      )
  ]
  | unique
' <<< "$semantic_files")

has_semantic_inputs=$(jq -nr --argjson files "$semantic_files" '$files | length > 0')
only_skippable=$(jq -nr \
  --argjson changed "$changed_files" \
  --argjson skippable "$skippable_files" \
  '$changed - $skippable | length == 0')
only_skippable_or_semantic=$(jq -nr \
  --argjson changed "$changed_files" \
  --argjson skippable "$skippable_files" \
  --argjson semantic "$semantic_files" \
  '$changed - (($skippable + $semantic) | unique) | length == 0')

skip_workflow=$(jq -nr \
  --argjson changed "$changed_files" \
  --argjson only_skippable "$only_skippable" \
  --argjson has_semantic_inputs "$has_semantic_inputs" \
  '($changed | length == 0) or ($only_skippable and ($has_semantic_inputs | not))')
stabilization_required=$(jq -nr \
  --argjson only_skippable_or_semantic "$only_skippable_or_semantic" \
  '$only_skippable_or_semantic | not')

echo "has_semantic_inputs=$has_semantic_inputs"
echo "skip_workflow=$skip_workflow"
echo "stabilization_required=$stabilization_required"
