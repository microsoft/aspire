#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

if [ "$#" -ne 12 ]; then
  echo "Usage: $0 <cause-file> <run-context-file> <last-successful-run-file> <triggering-merge-file> <candidate-history-status-file> <run-url> <run-scope> <pr-number> <cause-jobs> <occurrence-row> <body-file> <metadata-file>" >&2
  exit 1
fi

CAUSE_FILE="$1"
RUN_CONTEXT_FILE="$2"
LAST_SUCCESSFUL_RUN_FILE="$3"
TRIGGERING_MERGE_FILE="$4"
CANDIDATE_HISTORY_STATUS_FILE="$5"
RUN_URL="$6"
RUN_SCOPE="$7"
PR_NUMBER="$8"
CAUSE_JOBS="$9"
NEW_OCCURRENCE_ROW="${10}"
BODY_FILE="${11}"
METADATA_FILE="${12}"

SANITIZED_CAUSE_FILE=$(mktemp)
trap 'rm -f "$SANITIZED_CAUSE_FILE"' EXIT
bash "$SCRIPT_DIR/analyze-ci-failure-persistence.sh" \
  sanitize-cause "$CAUSE_FILE" "$SANITIZED_CAUSE_FILE"
CAUSE_FILE="$SANITIZED_CAUSE_FILE"

sanitize_single_line()
{
  local field="$1"
  local max_length="$2"

  jq -r --arg field "$field" --argjson max_length "$max_length" \
    '(.[$field] // "") | .[0:$max_length]' "$CAUSE_FILE"
}

render_code_span()
{
  jq -nr --arg value "$1" '
    (([ $value | scan("`+") | length ] | max // 0) + 1) as $delimiter_length |
    ("`" * $delimiter_length) + " " + $value + " " + ("`" * $delimiter_length)
  '
}

CAUSE_ID=$(jq -r '.id' "$CAUSE_FILE")
CAUSE_TYPE=$(jq -r '.type' "$CAUSE_FILE")
TITLE=$(sanitize_single_line title 238)
TEST_NAME=$(sanitize_single_line test_name 500)
TOTAL_OCCURRENCE_COUNT=$(jq -er '
  if has("occurrences") then
    if ((.occurrences | type) == "array") and ((.occurrences | length) > 0)
    then (.occurrences | length)
    else error("invalid stored occurrence history")
    end
  else
    1
  end
' "$CAUSE_FILE")
if ! jq -ne --arg title "$TITLE" '$title | test("[^[:space:]]")'; then
  TITLE="$CAUSE_ID"
fi
TITLE_CODE=$(render_code_span "$TITLE")
TEST_NAME_CODE=$(render_code_span "$TEST_NAME")
MARKER="<!-- ci-failure-cause:${CAUSE_ID} -->"
TYPE_MARKER="<!-- ci-failure-cause-type:${CAUSE_TYPE} -->"

if [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
  LAST_SUCCESSFUL_SHA=$(jq -r '.head_sha // "unknown"' "$LAST_SUCCESSFUL_RUN_FILE")
  FAILED_SHA=$(jq -r '.head_sha // "unknown"' "$RUN_CONTEXT_FILE")
  TITLE="Main branch CI failure at ${FAILED_SHA}"
  TITLE_CODE=$(render_code_span "$TITLE")
  MAIN_ERROR_MESSAGE="The main branch CI run failed. See the linked workflow run and trusted commit context above for diagnostics."
  CANDIDATE_HISTORY_STATE=$(
    jq -er '.state | select(. == "available" or . == "incomplete" or . == "unavailable")' \
      "$CANDIDATE_HISTORY_STATUS_FILE" 2>/dev/null || printf 'unavailable'
  )
  if [ "$CANDIDATE_HISTORY_STATE" = "available" ]; then
    TRIGGERING_MERGE_NUMBER=$(jq -r 'if (.number | type) == "number" then .number else empty end' "$TRIGGERING_MERGE_FILE")
  else
    TRIGGERING_MERGE_NUMBER=""
  fi
  if [ -n "$TRIGGERING_MERGE_NUMBER" ]; then
    TRIGGERING_MERGE_TITLE=$(bash "$SCRIPT_DIR/analyze-ci-failure-persistence.sh" \
      sanitize-json-field "$TRIGGERING_MERGE_FILE" title 238)
    TRIGGERING_MERGE_TITLE_CODE=$(render_code_span "$TRIGGERING_MERGE_TITLE")
    TRIGGERING_MERGE="#${TRIGGERING_MERGE_NUMBER} ${TRIGGERING_MERGE_TITLE_CODE}"
  else
    TRIGGERING_MERGE=""
  fi
fi

{
  echo "${MARKER}"
  echo "${TYPE_MARKER}"
  echo ""
  echo "## Build Information"
  echo ""
  echo "Build: ${RUN_URL}"
  if [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
    echo "Affected branch: \`main\`"
    echo "Last successful main SHA: \`${LAST_SUCCESSFUL_SHA}\`"
    echo "Failed main SHA: \`${FAILED_SHA}\`"
    if [ -n "$TRIGGERING_MERGE" ]; then
      echo "Triggering merge PR (context only, not necessarily causal): ${TRIGGERING_MERGE}"
    fi
  elif [ -n "$TEST_NAME" ]; then
    echo "Build error leg or test failing: ${CAUSE_JOBS} / ${TEST_NAME_CODE}"
  else
    echo "Build error leg: ${CAUSE_JOBS}"
  fi
  if [ "$RUN_SCOPE" = "pull-request" ] && [ "$PR_NUMBER" != "0" ]; then
    echo "Pull request: #${PR_NUMBER}"
  fi
  echo ""
  echo "## Error Message"
  echo ""
  if [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
    echo "    ${MAIN_ERROR_MESSAGE}"
  else
    jq -r '
      (.error_pattern // "") as $pattern |
      (if ($pattern | test("[^[:space:]]")) then $pattern else "No diagnostic pattern recorded." end) |
      .[0:500] |
      split("\n")[] |
      "    " + .
    ' "$CAUSE_FILE"
  fi
  echo ""
  echo "## Description"
  echo ""
  echo "$TITLE_CODE"
  echo ""
  echo "**Type**: ${CAUSE_TYPE}"
  echo ""
  echo "<!-- ci-failure-occurrences:start -->"
  echo "## Occurrences"
  echo ""
  echo "Showing 1 most recent of ${TOTAL_OCCURRENCE_COUNT} occurrences."
  echo ""
  echo "| Date | Build | Job | Context |"
  echo "|------|-------|-----|----|"
  echo "$NEW_OCCURRENCE_ROW"
  echo "<!-- ci-failure-occurrences:end -->"
} > "$BODY_FILE"

BODY_BYTES=$(wc -c < "$BODY_FILE" | tr -d '[:space:]')
if [ "$BODY_BYTES" -gt 65000 ]; then
  echo "::warning::Rendered cause issue exceeds the 65000-byte publication budget" >&2
  exit 2
fi

LABELS="ci-failure-cause"
TITLE_PREFIX="[CI Failure] "
if [ "$CAUSE_TYPE" = "flaky-test" ]; then
  LABELS="ci-failure-cause,test-failure"
elif [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
  LABELS="ci-failure-cause,main-ci-break"
  TITLE_PREFIX="[Main CI Failure] "
fi

ISSUE_TITLE="${TITLE_PREFIX}${TITLE}"
if [ "$(jq -nr --arg title "$ISSUE_TITLE" '$title | length')" -gt 256 ]; then
  echo "::error::Issue title exceeds GitHub's 256-character limit" >&2
  exit 1
fi
jq -n --arg title "$ISSUE_TITLE" --arg labels "$LABELS" \
  '{title: $title, labels: $labels}' > "$METADATA_FILE"
