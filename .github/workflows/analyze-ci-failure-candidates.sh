#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

if [ "$#" -ne 5 ]; then
  echo "Usage: $0 <repo> <last-successful-sha> <failed-sha> <candidates-file> <status-file>" >&2
  exit 1
fi

REPO="$1"
LAST_SUCCESSFUL_SHA="$2"
FAILED_SHA="$3"
CANDIDATES_FILE="$4"
STATUS_FILE="$5"

printf '%s\n' '[]' > "$CANDIDATES_FILE"
printf '%s\n' '{"state":"unavailable"}' > "$STATUS_FILE"

if [ -z "$LAST_SUCCESSFUL_SHA" ] || [ -z "$FAILED_SHA" ]; then
  exit 0
fi

COMPARISON_PAGES=$(mktemp)
COMPARISON=$(mktemp)
CANDIDATES_TMP="${CANDIDATES_FILE}.tmp"
trap 'rm -f "$COMPARISON_PAGES" "$COMPARISON" "$CANDIDATES_TMP"' EXIT

if ! gh api --paginate --slurp \
    "repos/${REPO}/compare/${LAST_SUCCESSFUL_SHA}...${FAILED_SHA}?per_page=100" \
    > "$COMPARISON_PAGES" 2>/dev/null; then
  echo "::warning::Unable to compare the last successful main commit with the failed commit."
  exit 0
fi

jq '{
  total_commits: (.[0].total_commits // 0),
  commits: [.[].commits[]?]
}' "$COMPARISON_PAGES" > "$COMPARISON"

RECEIVED_COMMIT_COUNT=$(jq '.commits | length' "$COMPARISON")
TOTAL_COMMIT_COUNT=$(jq '.total_commits' "$COMPARISON")
if [ "$RECEIVED_COMMIT_COUNT" -lt "$TOTAL_COMMIT_COUNT" ]; then
  echo "::warning::GitHub returned only ${RECEIVED_COMMIT_COUNT} of ${TOTAL_COMMIT_COUNT} commits in the comparison."
  printf '%s\n' '{"state":"incomplete"}' > "$STATUS_FILE"
else
  printf '%s\n' '{"state":"available"}' > "$STATUS_FILE"
fi

jq -c '.commits[]? | {sha, message: .commit.message, html_url}' "$COMPARISON" |
  while IFS= read -r COMMIT; do
    COMMIT_SHA=$(jq -r '.sha' <<< "${COMMIT}")
    if ! MERGE_PR=$(gh api "repos/${REPO}/commits/${COMMIT_SHA}/pulls" \
        --jq "[.[] | select(.base.repo.full_name == \"${REPO}\" and .base.ref == \"main\" and .merged_at != null)] | first // null" \
        2>/dev/null); then
      echo "::warning::Unable to associate commit ${COMMIT_SHA} with a merged pull request."
      printf '%s\n' '{"state":"incomplete"}' > "$STATUS_FILE"
      continue
    fi
    if [ "${MERGE_PR}" != "null" ]; then
      jq --argjson commit "${COMMIT}" --argjson pr "${MERGE_PR}" \
        '. + [$commit + {pull_request: {
          number: $pr.number,
          title: $pr.title,
          url: $pr.html_url,
          merged_at: $pr.merged_at
        }}]' "$CANDIDATES_FILE" > "$CANDIDATES_TMP"
      mv "$CANDIDATES_TMP" "$CANDIDATES_FILE"
    fi
  done
