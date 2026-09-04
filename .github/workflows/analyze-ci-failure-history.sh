#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

REPO="${1:?repository is required}"
WORKFLOW_ID="${2:?workflow ID is required}"
FAILED_RUN_CREATED_AT="${3:?failed run creation time is required}"
FAILED_RUN_ID="${4:?failed run ID is required}"
OUTPUT_FILE="${5:?output file is required}"

if [[ ! "$FAILED_RUN_ID" =~ ^[0-9]+$ ]]; then
  echo "::error::Failed run ID must be numeric." >&2
  exit 1
fi

TEMP_DIRECTORY=$(mktemp -d)
trap 'rm -rf "$TEMP_DIRECTORY"' EXIT

format_epoch()
{
  jq -nr --argjson epoch "$1" '$epoch | strftime("%Y-%m-%dT%H:%M:%SZ")'
}

query_window()
{
  local start_epoch="$1"
  local end_epoch="$2"
  local result_file="$3"
  local start_time
  local end_time
  local first_page
  local total_count
  local page_size
  local received_run_count

  start_time=$(format_epoch "$start_epoch")
  end_time=$(format_epoch "$end_epoch")
  first_page="$TEMP_DIRECTORY/page-${start_epoch}-${end_epoch}-1.json"

  gh api --method GET "repos/${REPO}/actions/workflows/${WORKFLOW_ID}/runs" \
    -f branch=main \
    -f event=push \
    -f status=success \
    -f per_page=100 \
    -f page=1 \
    -f "created=${start_time}..${end_time}" > "$first_page"

  total_count=$(jq -r '.total_count // 0' "$first_page")
  if [[ ! "$total_count" =~ ^[0-9]+$ ]] ||
     ! jq -e '(.workflow_runs | type) == "array"' "$first_page" >/dev/null; then
    echo "::error::GitHub returned an invalid workflow-run count." >&2
    return 1
  fi

  # GitHub caps filtered workflow-run searches at 1,000 results. Search the
  # newer half first so a dense window can be subdivided without scanning
  # older history after the nearest successful run has been found.
  # https://docs.github.com/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow
  if [ "$total_count" -ge 1000 ]; then
    if [ $((end_epoch - start_epoch)) -le 1 ]; then
      echo "::error::A one-second workflow-run window reached GitHub's 1,000-result cap." >&2
      return 1
    fi

    local midpoint=$((start_epoch + (end_epoch - start_epoch) / 2))
    query_window "$midpoint" "$end_epoch" "$result_file"
    if [ "$(jq -r 'has("id")' "$result_file")" = "true" ]; then
      return 0
    fi

    query_window "$start_epoch" "$midpoint" "$result_file"
    return
  fi

  local runs_file="$TEMP_DIRECTORY/runs-${start_epoch}-${end_epoch}.jsonl"
  jq -c '.workflow_runs[]?' "$first_page" > "$runs_file"

  page_size=$(jq '.workflow_runs | length' "$first_page")
  local page=2
  while [ "$page_size" -eq 100 ]; do
    local page_file="$TEMP_DIRECTORY/page-${start_epoch}-${end_epoch}-${page}.json"
    gh api --method GET "repos/${REPO}/actions/workflows/${WORKFLOW_ID}/runs" \
      -f branch=main \
      -f event=push \
      -f status=success \
      -f per_page=100 \
      -f "page=${page}" \
      -f "created=${start_time}..${end_time}" > "$page_file"
    if ! jq -e '(.workflow_runs | type) == "array"' "$page_file" >/dev/null; then
      echo "::error::GitHub returned an invalid workflow-run page." >&2
      return 1
    fi
    jq -c '.workflow_runs[]?' "$page_file" >> "$runs_file"
    page_size=$(jq '.workflow_runs | length' "$page_file")
    page=$((page + 1))
  done

  received_run_count=$(jq -s '[.[] | select((.id | type) == "number") | .id] | unique | length' "$runs_file")
  if [ "$received_run_count" -lt "$total_count" ]; then
    echo "::error::GitHub returned only ${received_run_count} of ${total_count} unique workflow runs." >&2
    return 1
  fi

  # The API's range syntax includes both boundaries. Apply the intended
  # half-open [start, end) contract locally before selecting the newest run.
  jq -s \
    --arg start_time "$start_time" \
    --arg end_time "$end_time" \
    --arg failed_time "$FAILED_RUN_CREATED_AT" \
    --argjson failed_run_id "$FAILED_RUN_ID" \
    '
      map(select(
        (.id | type) == "number" and
        (.created_at | type) == "string" and
        .created_at >= $start_time and
        (
          .created_at < $end_time or
          ($end_time == $failed_time and
            .created_at == $failed_time and
            .id < $failed_run_id)
        )
      ))
      | sort_by([.id, .created_at])
      | unique_by(.id)
      | sort_by([.created_at, .id])
      | last // {}
    ' "$runs_file" > "$result_file"
}

FAILED_EPOCH=$(jq -nr --arg timestamp "$FAILED_RUN_CREATED_AT" '$timestamp | fromdateiso8601')
WINDOW_END="$FAILED_EPOCH"
WINDOW_SPAN=86400

while [ "$WINDOW_END" -gt 0 ]; do
  WINDOW_START=$((WINDOW_END - WINDOW_SPAN))
  if [ "$WINDOW_START" -lt 0 ]; then
    WINDOW_START=0
  fi

  query_window "$WINDOW_START" "$WINDOW_END" "$OUTPUT_FILE"
  if [ "$(jq -r 'has("id")' "$OUTPUT_FILE")" = "true" ]; then
    exit 0
  fi

  WINDOW_END="$WINDOW_START"
  WINDOW_SPAN=$((WINDOW_SPAN * 4))
done

echo "{}" > "$OUTPUT_FILE"
