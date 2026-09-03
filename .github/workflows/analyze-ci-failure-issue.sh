#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

if [ "$#" -ne 11 ]; then
  echo "Usage: $0 <cause-file> <run-context-file> <last-successful-run-file> <triggering-merge-file> <run-url> <run-scope> <pr-number> <first-job> <occurrence-row> <body-file> <metadata-file>" >&2
  exit 1
fi

CAUSE_FILE="$1"
RUN_CONTEXT_FILE="$2"
LAST_SUCCESSFUL_RUN_FILE="$3"
TRIGGERING_MERGE_FILE="$4"
RUN_URL="$5"
RUN_SCOPE="$6"
PR_NUMBER="$7"
FIRST_JOB="$8"
NEW_OCCURRENCE_ROW="$9"
BODY_FILE="${10}"
METADATA_FILE="${11}"

CAUSE_ID=$(jq -r '.id' "$CAUSE_FILE")
CAUSE_TYPE=$(jq -r '.type' "$CAUSE_FILE")
TEST_NAME=$(jq -r '.test_name // empty' "$CAUSE_FILE")
MARKER="<!-- ci-failure-cause:${CAUSE_ID} -->"
TYPE_MARKER="<!-- ci-failure-cause-type:${CAUSE_TYPE} -->"

if [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
  LAST_SUCCESSFUL_SHA=$(jq -r '.head_sha // "unknown"' "$LAST_SUCCESSFUL_RUN_FILE")
  FAILED_SHA=$(jq -r '.head_sha // "unknown"' "$RUN_CONTEXT_FILE")
  TRIGGERING_MERGE=$(jq -r 'if .number then "#\(.number) \(.title)" else "Not found" end' "$TRIGGERING_MERGE_FILE")
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
    echo "Triggering merge PR (context only, not necessarily causal): ${TRIGGERING_MERGE}"
  elif [ -n "$TEST_NAME" ]; then
    echo "Build error leg or test failing: ${FIRST_JOB} / \`${TEST_NAME}\`"
  else
    echo "Build error leg: ${FIRST_JOB}"
  fi
  if [ "$RUN_SCOPE" = "pull-request" ] && [ "$PR_NUMBER" != "0" ]; then
    echo "Pull request: #${PR_NUMBER}"
  fi
  echo ""
  echo "## Error Message"
  echo ""
  echo '```'
  jq -r '.error_pattern' "$CAUSE_FILE"
  echo '```'
  echo ""
  echo "## Description"
  echo ""
  jq -r '.title' "$CAUSE_FILE"
  echo ""
  echo "**Type**: ${CAUSE_TYPE}"
  echo ""
  echo "## Occurrences"
  echo ""
  echo "| Date | Build | Job | Context |"
  echo "|------|-------|-----|----|"
  echo "$NEW_OCCURRENCE_ROW"
} > "$BODY_FILE"

LABELS="ci-failure-cause"
TITLE_PREFIX="[CI Failure] "
if [ "$CAUSE_TYPE" = "flaky-test" ]; then
  LABELS="ci-failure-cause,test-failure"
elif [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
  LABELS="ci-failure-cause,main-ci-break"
  TITLE_PREFIX="[Main CI Failure] "
fi

ISSUE_TITLE=$(jq -r --arg prefix "$TITLE_PREFIX" '$prefix + .title' "$CAUSE_FILE")
jq -n --arg title "$ISSUE_TITLE" --arg labels "$LABELS" \
  '{title: $title, labels: $labels}' > "$METADATA_FILE"
