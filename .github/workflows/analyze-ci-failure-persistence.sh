#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

COMMAND="${1:?command is required}"
CI_FAILURE_DATA_DIR="${CI_FAILURE_DATA_DIR:-ci-failure-data}"
RUN_CONTEXT_FILE="$CI_FAILURE_DATA_DIR/run-context.json"

trusted_pr_number()
{
  local run_scope
  local pr_number

  run_scope=$(jq -r '.run_scope' "$RUN_CONTEXT_FILE")
  if [ "$run_scope" != "pull-request" ]; then
    echo 0
    return
  fi

  pr_number=$(jq -r '.pr_numbers // ""' "$RUN_CONTEXT_FILE" | cut -d',' -f1)
  if [[ "$pr_number" =~ ^[0-9]+$ ]]; then
    echo "$pr_number"
  else
    echo 0
  fi
}

case "$COMMAND" in
  pr-number)
    trusted_pr_number
    ;;
  cause-job-names)
    CAUSE_FILE="${2:?cause file is required}"
    TRUSTED_FAILED_JOBS_FILE="${3:?trusted failed jobs file is required}"
    FORMAT="${4:?format is required}"

    jq -er \
      --arg format "$FORMAT" \
      --slurpfile trusted_jobs "$TRUSTED_FAILED_JOBS_FILE" '
        .job_ids as $job_ids |
        [
          $job_ids[] as $job_id |
          [$trusted_jobs[0][] | select(.id == $job_id) | .name][0]
        ] as $job_names |
        if any($job_names[]; type != "string" or length == 0) then
          error("cause references an unknown trusted failed job")
        else
          $job_names
          | map(gsub("[\r\n]+"; " "))
          | join("<br>")
          | if $format == "display" then
              .
            elif $format == "table" then
              gsub("\\|"; "\\|")
            else
              error("unsupported cause job name format")
            end
        end
      ' "$CAUSE_FILE"
    ;;
  add-occurrence)
    CAUSE_FILE="${2:?cause file is required}"
    RUN_ID="${3:?run ID is required}"
    RUN_URL="${4:?run URL is required}"
    JOB_NAMES="${5:?job names are required}"
    ANALYZED_AT="${6:?analysis timestamp is required}"
    PR_NUMBER=$(trusted_pr_number)

    jq \
      --argjson run_id "$RUN_ID" \
      --arg run_url "$RUN_URL" \
      --arg job "$JOB_NAMES" \
      --argjson pr_number "$PR_NUMBER" \
      --arg observed_at "$ANALYZED_AT" \
      '. + {occurrences: [{run_id: $run_id, run_url: $run_url, job: $job, pr_number: $pr_number, observed_at: $observed_at}]}' \
      "$CAUSE_FILE"
    ;;
  write-run-summary)
    ANALYSIS_FILE="${2:?analysis file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    ANALYZED_AT="${4:?analysis timestamp is required}"
    PR_METADATA_FILE="$CI_FAILURE_DATA_DIR/pr-metadata.json"
    TRIGGERING_MERGE_FILE="$CI_FAILURE_DATA_DIR/triggering-merge-pr.json"
    LAST_SUCCESSFUL_RUN_FILE="$CI_FAILURE_DATA_DIR/last-successful-main-run.json"
    CANDIDATE_MERGES_FILE="$CI_FAILURE_DATA_DIR/candidate-merges.json"

    [ -f "$PR_METADATA_FILE" ] || PR_METADATA_FILE=/dev/null
    [ -f "$TRIGGERING_MERGE_FILE" ] || TRIGGERING_MERGE_FILE=/dev/null
    [ -f "$LAST_SUCCESSFUL_RUN_FILE" ] || LAST_SUCCESSFUL_RUN_FILE=/dev/null
    [ -f "$CANDIDATE_MERGES_FILE" ] || CANDIDATE_MERGES_FILE=/dev/null

    jq -n \
      --arg analyzed_at "$ANALYZED_AT" \
      --slurpfile analysis "$ANALYSIS_FILE" \
      --slurpfile run_context "$RUN_CONTEXT_FILE" \
      --slurpfile run "$CI_FAILURE_DATA_DIR/run.json" \
      --slurpfile trusted_jobs "$CI_FAILURE_DATA_DIR/failed-jobs.json" \
      --slurpfile pr_metadata "$PR_METADATA_FILE" \
      --slurpfile triggering_merge "$TRIGGERING_MERGE_FILE" \
      --slurpfile last_successful_run "$LAST_SUCCESSFUL_RUN_FILE" \
      --slurpfile candidate_merges "$CANDIDATE_MERGES_FILE" \
      '
        ($analysis[0]) as $analysis |
        ($run_context[0]) as $context |
        ($run[0]) as $run |
        ($trusted_jobs[0]) as $trusted_jobs |
        ($pr_metadata[0] // {}) as $pr |
        ($triggering_merge[0] // {}) as $triggering |
        ($last_successful_run[0] // {}) as $last_success |
        ($candidate_merges[0] // []) as $candidates |
        ($analysis.failed_jobs | map({key: (.id | tostring), value: .}) | from_entries) as $analysis_jobs |
        ($trusted_jobs | map(.name) | map(select(type == "string" and length > 0)) | unique) as $trusted_job_names |
        {
          run_id: $context.run_id,
          run_attempt: $context.run_attempt,
          run_url: ($run.html_url // ""),
          run_scope: $context.run_scope,
          analyzed_at: $analyzed_at,
          verdict: $analysis.verdict,
          pr: (
            if $context.run_scope == "pull-request" and ($pr.number | type) == "number" then
              {
                number: $pr.number,
                title: ($pr.title // ""),
                author: ($pr.user // ""),
                state: ($pr.state // ""),
                head_branch: ($pr.head_branch // ""),
                base_branch: ($pr.base_branch // ""),
                url: ($pr.html_url // "")
              }
            else
              null
            end
          ),
          triggering_merge_pr: (
            if $context.run_scope == "main" and ($triggering.number | type) == "number" then
              {
                number: $triggering.number,
                title: ($triggering.title // ""),
                author: ($triggering.user.login // ""),
                state: ($triggering.state // ""),
                head_branch: ($triggering.head.ref // ""),
                base_branch: ($triggering.base.ref // ""),
                url: ($triggering.html_url // ""),
                merged_at: ($triggering.merged_at // null)
              }
            else
              null
            end
          ),
          main_context: (
            if $context.run_scope == "main" then
              {
                last_successful_main_sha: ($last_success.head_sha // null),
                failed_sha: $context.head_sha,
                candidate_merges: [
                  $candidates[]? |
                  {
                    sha: .sha,
                    message: .message,
                    html_url: .html_url,
                    pull_request: {
                      number: .pull_request.number,
                      title: .pull_request.title,
                      url: .pull_request.url,
                      merged_at: .pull_request.merged_at
                    }
                  }
                ]
              }
            else
              null
            end
          ),
          failed_jobs: [
            $trusted_jobs[] as $job |
            ($analysis_jobs[($job.id | tostring)]) as $classification |
            {
              name: $job.name,
              id: $job.id,
              conclusion: $job.conclusion,
              url: ($job.html_url // ""),
              classification: $classification.classification,
              reason: (
                if ($classification.reason | type) == "string" then
                  $classification.reason
                else
                  ""
                end
              ),
              failed_steps: [
                $job.steps[]? |
                select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out") |
                .name
              ]
            }
          ],
          failed_tests: [
            $analysis.failed_tests[]? |
            select(type == "object") |
            {
              name: (.name // ""),
              job: (.job as $job | if ($trusted_job_names | index($job)) != null then $job else "" end),
              error: (.error // ""),
              stack_trace: (.stack_trace // ""),
              classification: (.classification // ""),
              reason: (.reason // "")
            }
          ],
          causes: $analysis.causes
        }
      ' > "$OUTPUT_FILE"
    ;;
  *)
    echo "::error::Unsupported persistence command: $COMMAND" >&2
    exit 1
    ;;
esac
