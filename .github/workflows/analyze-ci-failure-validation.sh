#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

ANALYSIS_FILE="$(dirname "$GH_AW_AGENT_OUTPUT")/agent/analysis-result.json"
CAUSES_DIR="$(dirname "$GH_AW_AGENT_OUTPUT")/agent/causes"
RUN_CONTEXT_FILE="ci-failure-data/run-context.json"
TRUSTED_FAILED_JOBS_FILE="ci-failure-data/failed-jobs.json"
if [ ! -f "$ANALYSIS_FILE" ] || [ ! -f "$RUN_CONTEXT_FILE" ] || [ ! -f "$TRUSTED_FAILED_JOBS_FILE" ]; then
  echo "::error::Analysis result or trusted run data not found"
  exit 1
fi

TRUSTED_RUN_ID=$(jq -r '.run_id' "$RUN_CONTEXT_FILE")
TRUSTED_RUN_SCOPE=$(jq -r '.run_scope' "$RUN_CONTEXT_FILE")
ANALYSIS_RUN_ID=$(jq -r '.run_id' "$ANALYSIS_FILE")
ANALYSIS_RUN_SCOPE=$(jq -r '.run_scope' "$ANALYSIS_FILE")
VERDICT=$(jq -r '.verdict' "$ANALYSIS_FILE")

if [ "$ANALYSIS_RUN_ID" != "$TRUSTED_RUN_ID" ] || [ "$ANALYSIS_RUN_SCOPE" != "$TRUSTED_RUN_SCOPE" ]; then
  echo "::error::Analysis result does not match trusted run context"
  exit 1
fi
if [ "$TRUSTED_RUN_SCOPE" = "main" ] && [ "$(jq -r '.pr // null' "$ANALYSIS_FILE")" != "null" ]; then
  echo "::error::Main run analysis must not identify a subject PR"
  exit 1
fi
if [ "$TRUSTED_RUN_SCOPE" = "pull-request" ]; then
  TRUSTED_PR_NUMBERS=$(jq -r '.pr_numbers // ""' "$RUN_CONTEXT_FILE")
  ANALYSIS_PR_NUMBER=$(jq -r '
    if ((.pr | type) == "object") and ((.pr.number | type) == "number")
    then (.pr.number | tostring)
    else ""
    end
  ' "$ANALYSIS_FILE")
  ANALYSIS_PR_IS_NULL=$(jq -r 'has("pr") and (.pr == null)' "$ANALYSIS_FILE")
  if [ "$ANALYSIS_PR_IS_NULL" = "true" ]; then
    :
  elif [ -z "$TRUSTED_PR_NUMBERS" ] || [ -z "$ANALYSIS_PR_NUMBER" ]; then
    echo "::error::Pull request analysis must identify a trusted subject PR"
    exit 1
  else
    case ",${TRUSTED_PR_NUMBERS}," in
      *",${ANALYSIS_PR_NUMBER},"*) ;;
      *)
        echo "::error::Pull request analysis must identify a trusted subject PR"
        exit 1
        ;;
    esac
  fi
fi
if ! jq -e '
  (.failed_jobs | type == "array") and
  all(.failed_jobs[]; (.id | type) == "number") and
  (.causes | type == "array") and
  all(.causes[]; type == "string")
' "$ANALYSIS_FILE" >/dev/null; then
  echo "::error::Analysis must contain numeric-ID failed_jobs and string-valued causes arrays"
  exit 1
fi
if ! jq -e '
  (.failed_tests | type == "array") and
  all(.failed_tests[];
    (type == "object") and
    ((.name | type) == "string") and
    ((.job | type) == "string" and (.job | length) > 0) and
    ((.error | type) == "string") and
    ((.stack_trace == null) or ((.stack_trace | type) == "string")) and
    (.classification == "flaky" or .classification == "code-issue") and
    ((.reason | type) == "string"))
' "$ANALYSIS_FILE" >/dev/null; then
  echo "::error::Analysis failed_tests must match the safe field schema"
  exit 1
fi
if ! jq -e '(type == "array") and all(.[]; (.id | type) == "number")' "$TRUSTED_FAILED_JOBS_FILE" >/dev/null; then
  echo "::error::Trusted failed jobs are invalid"
  exit 1
fi

case "${TRUSTED_RUN_SCOPE}:${VERDICT}" in
  main:transient-infra|main:flaky-test|main:main-repository-breakage|main:mixed|pull-request:transient-infra|pull-request:flaky-test|pull-request:code-issue|pull-request:mixed)
    ;;
  *)
    echo "::error::Verdict '${VERDICT}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}"
    exit 1
    ;;
esac

CAUSE_COUNT=0
INFRA_CAUSE_COUNT=0
FLAKY_CAUSE_COUNT=0
MAIN_BREAK_CAUSE_COUNT=0
INFRA_CAUSE_JOB_IDS='[]'
FLAKY_CAUSE_JOB_IDS='[]'
MAIN_BREAK_CAUSE_JOB_IDS='[]'
SUMMARY_CAUSE_COUNT=$(jq '.causes | length' "$ANALYSIS_FILE")
UNIQUE_SUMMARY_CAUSE_COUNT=$(jq '.causes | unique | length' "$ANALYSIS_FILE")
FAILED_JOB_COUNT=$(jq '[.failed_jobs[]?] | length' "$ANALYSIS_FILE")
INFRA_JOB_COUNT=$(jq '[.failed_jobs[]? | select(.classification == "transient-infra")] | length' "$ANALYSIS_FILE")
FLAKY_JOB_COUNT=$(jq '[.failed_jobs[]? | select(.classification == "flaky-test")] | length' "$ANALYSIS_FILE")
CODE_ISSUE_JOB_COUNT=$(jq '[.failed_jobs[]? | select(.classification == "code-issue")] | length' "$ANALYSIS_FILE")
MAIN_BREAK_JOB_COUNT=$(jq '[.failed_jobs[]? | select(.classification == "main-repository-breakage")] | length' "$ANALYSIS_FILE")
FAILED_TEST_COUNT=$(jq '[.failed_tests[]?] | length' "$ANALYSIS_FILE")
FLAKY_TEST_COUNT=$(jq '[.failed_tests[]? | select(.classification == "flaky")] | length' "$ANALYSIS_FILE")
CODE_ISSUE_TEST_COUNT=$(jq '[.failed_tests[]? | select(.classification == "code-issue")] | length' "$ANALYSIS_FILE")
KNOWN_JOB_COUNT=$((INFRA_JOB_COUNT + FLAKY_JOB_COUNT + CODE_ISSUE_JOB_COUNT + MAIN_BREAK_JOB_COUNT))
TRANSIENT_JOB_COUNT=$((INFRA_JOB_COUNT + FLAKY_JOB_COUNT))
UNIQUE_ANALYSIS_JOB_COUNT=$(jq '[.failed_jobs[].id] | unique | length' "$ANALYSIS_FILE")
ANALYSIS_JOB_IDS=$(jq -c '[.failed_jobs[].id] | sort' "$ANALYSIS_FILE")
TRUSTED_JOB_IDS=$(jq -c '[.[].id] | sort' "$TRUSTED_FAILED_JOBS_FILE")

if [ "$FAILED_JOB_COUNT" -eq 0 ] || [ "$KNOWN_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ]; then
  echo "::error::Analysis must classify every failed job with a recognized classification"
  exit 1
fi
if [ "$UNIQUE_ANALYSIS_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ] || [ "$ANALYSIS_JOB_IDS" != "$TRUSTED_JOB_IDS" ]; then
  echo "::error::Analysis failed-job IDs do not match the trusted failed jobs"
  exit 1
fi
if { [ "$TRUSTED_RUN_SCOPE" = "main" ] && [ "$CODE_ISSUE_JOB_COUNT" -ne 0 ]; } ||
   { [ "$TRUSTED_RUN_SCOPE" = "pull-request" ] && [ "$MAIN_BREAK_JOB_COUNT" -ne 0 ]; }; then
  echo "::error::Analysis contains a failed-job classification that is not permitted for run scope ${TRUSTED_RUN_SCOPE}"
  exit 1
fi

if [ -d "$CAUSES_DIR" ]; then
  for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
    [ -f "$CAUSE_FILE" ] || continue
    if ! jq empty "$CAUSE_FILE" 2>/dev/null; then
      echo "::error::Invalid JSON in cause file: $(basename "$CAUSE_FILE")"
      exit 1
    fi

    CAUSE_BASENAME=$(basename "$CAUSE_FILE")
    if ! jq -e '
      (type == "object") and
      ((keys - ["error_pattern", "id", "job_ids", "test_name", "title", "type"]) | length == 0) and
      ((.id | type) == "string") and
      ((.type | type) == "string") and
      ((.title | type) == "string") and
      ((.error_pattern | type) == "string") and
      ((.job_ids | type) == "array" and (.job_ids | length) > 0) and
      (all(.job_ids[]; type == "number" and . > 0 and . == floor)) and
      ((.job_ids | unique | length) == (.job_ids | length)) and
      ((.test_name // "") | type == "string")
    ' "$CAUSE_FILE" >/dev/null; then
      echo "::error::Cause ${CAUSE_BASENAME} contains unsupported or publisher-owned fields"
      exit 1
    fi
    CAUSE_ID=$(jq -r '.id // ""' "$CAUSE_FILE")
    CAUSE_TYPE=$(jq -r '.type // ""' "$CAUSE_FILE")
    if [[ ! "$CAUSE_ID" =~ ^[a-z0-9]+(-[a-z0-9]+)*$ ]] || [ "${CAUSE_ID}.json" != "$CAUSE_BASENAME" ]; then
      echo "::error::Cause ID must be a lowercase hyphenated slug matching its filename: ${CAUSE_BASENAME}"
      exit 1
    fi
    if ! jq -e --arg cause_id "$CAUSE_ID" '.causes | index($cause_id) != null' "$ANALYSIS_FILE" >/dev/null; then
      echo "::error::Cause ${CAUSE_BASENAME} is not referenced by the analysis summary"
      exit 1
    fi

    case "${TRUSTED_RUN_SCOPE}:${CAUSE_TYPE}" in
      main:flaky-test|main:infra-failure|main:main-repository-breakage|pull-request:flaky-test|pull-request:infra-failure)
        ;;
      *)
        echo "::error::Cause ${CAUSE_BASENAME} type '${CAUSE_TYPE}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}"
        exit 1
        ;;
    esac

    # A flaky test can share a failed job with deterministic failures. In that
    # case the job's primary classification is not flaky-test, so require
    # validated flaky-test evidence naming the same trusted job.
    if ! jq -e \
      --arg cause_type "$CAUSE_TYPE" \
      --slurpfile analysis "$ANALYSIS_FILE" \
      --slurpfile trusted_jobs "$TRUSTED_FAILED_JOBS_FILE" '
        all(.job_ids[]; . as $job_id |
          ([$analysis[0].failed_jobs[] | select(.id == $job_id)][0].classification // "") as $classification |
          ([$trusted_jobs[0][] | select(.id == $job_id)][0] // null) as $trusted_job |
          ($trusted_job != null) and
            (if $cause_type == "infra-failure" then
              $classification == "transient-infra"
            elif $cause_type == "main-repository-breakage" then
              $classification == "main-repository-breakage"
            else
              $classification == "flaky-test" or
                ((($classification == "code-issue") or ($classification == "main-repository-breakage")) and
                  any($analysis[0].failed_tests[];
                    .classification == "flaky" and .job == ($trusted_job.name // "")))
            end)
        )
      ' "$CAUSE_FILE" >/dev/null; then
      echo "::error::Cause ${CAUSE_BASENAME} references an unknown or incompatible failed job"
      exit 1
    fi

    PRIOR_CAUSE_FILE="ci-failure-data/prior-causes/${CAUSE_BASENAME}"
    if [ -f "$PRIOR_CAUSE_FILE" ]; then
      PRIOR_CAUSE_TYPE=$(jq -r '.type // ""' "$PRIOR_CAUSE_FILE")
      if [ "$PRIOR_CAUSE_TYPE" != "$CAUSE_TYPE" ]; then
        echo "::error::Cause ${CAUSE_BASENAME} cannot change type from '${PRIOR_CAUSE_TYPE}' to '${CAUSE_TYPE}'"
        exit 1
      fi
    fi

    CAUSE_COUNT=$((CAUSE_COUNT + 1))
    case "$CAUSE_TYPE" in
      infra-failure)
        INFRA_CAUSE_COUNT=$((INFRA_CAUSE_COUNT + 1))
        INFRA_CAUSE_JOB_IDS=$(jq -c --argjson covered "$INFRA_CAUSE_JOB_IDS" '$covered + .job_ids | unique' "$CAUSE_FILE")
        ;;
      flaky-test)
        FLAKY_CAUSE_COUNT=$((FLAKY_CAUSE_COUNT + 1))
        FLAKY_CAUSE_JOB_IDS=$(jq -c --argjson covered "$FLAKY_CAUSE_JOB_IDS" '$covered + .job_ids | unique' "$CAUSE_FILE")
        ;;
      main-repository-breakage)
        MAIN_BREAK_CAUSE_COUNT=$((MAIN_BREAK_CAUSE_COUNT + 1))
        MAIN_BREAK_CAUSE_JOB_IDS=$(jq -c --argjson covered "$MAIN_BREAK_CAUSE_JOB_IDS" '$covered + .job_ids | unique' "$CAUSE_FILE")
        ;;
    esac
  done
fi
if [ "$SUMMARY_CAUSE_COUNT" -ne "$UNIQUE_SUMMARY_CAUSE_COUNT" ] ||
   [ "$SUMMARY_CAUSE_COUNT" -ne "$CAUSE_COUNT" ]; then
  echo "::error::Analysis cause IDs must uniquely match the generated cause files"
  exit 1
fi
case "$VERDICT" in
  transient-infra)
    if [ "$FAILED_TEST_COUNT" -ne 0 ]; then
      echo "::error::Analysis failed_tests are incompatible with verdict transient-infra"
      exit 1
    fi
    if [ "$INFRA_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ] ||
       [ "$CAUSE_COUNT" -eq 0 ] || [ "$INFRA_CAUSE_COUNT" -ne "$CAUSE_COUNT" ]; then
      echo "::error::A transient-infra verdict requires every failed job and cause to be an infrastructure failure"
      exit 1
    fi
    ;;
  flaky-test)
    if [ "$CODE_ISSUE_TEST_COUNT" -ne 0 ]; then
      echo "::error::Analysis failed_tests are incompatible with verdict flaky-test"
      exit 1
    fi
    if [ "$FLAKY_JOB_COUNT" -eq 0 ] || [ "$TRANSIENT_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ] ||
       [ "$CAUSE_COUNT" -eq 0 ] || [ "$FLAKY_CAUSE_COUNT" -eq 0 ] || [ "$MAIN_BREAK_CAUSE_COUNT" -ne 0 ]; then
      echo "::error::A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes"
      exit 1
    fi
    ;;
  code-issue)
    if [ "$FLAKY_TEST_COUNT" -ne 0 ]; then
      echo "::error::Analysis failed_tests are incompatible with verdict code-issue"
      exit 1
    fi
    if [ "$CODE_ISSUE_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ] || [ "$CAUSE_COUNT" -ne 0 ]; then
      echo "::error::A code-issue verdict requires every failed job to be a code issue and must not include cause files"
      exit 1
    fi
    ;;
  main-repository-breakage)
    if [ "$FLAKY_TEST_COUNT" -ne 0 ]; then
      echo "::error::Analysis failed_tests are incompatible with verdict main-repository-breakage"
      exit 1
    fi
    if [ "$MAIN_BREAK_JOB_COUNT" -ne "$FAILED_JOB_COUNT" ] ||
       [ "$MAIN_BREAK_CAUSE_COUNT" -eq 0 ] || [ "$MAIN_BREAK_CAUSE_COUNT" -ne "$CAUSE_COUNT" ]; then
      echo "::error::A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage"
      exit 1
    fi
    ;;
  mixed)
    case "$TRUSTED_RUN_SCOPE" in
      main)
        if [ "$MAIN_BREAK_JOB_COUNT" -eq 0 ] ||
           { [ "$TRANSIENT_JOB_COUNT" -eq 0 ] && [ "$FLAKY_TEST_COUNT" -eq 0 ]; } ||
           [ "$MAIN_BREAK_CAUSE_COUNT" -eq 0 ] || [ "$MAIN_BREAK_CAUSE_COUNT" -eq "$CAUSE_COUNT" ]; then
          echo "::error::A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause"
          exit 1
        fi
        ;;
      pull-request)
        if [ "$CODE_ISSUE_JOB_COUNT" -eq 0 ] ||
           { [ "$TRANSIENT_JOB_COUNT" -eq 0 ] && [ "$FLAKY_TEST_COUNT" -eq 0 ]; } ||
           [ "$CAUSE_COUNT" -eq 0 ]; then
          echo "::error::A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause"
          exit 1
        fi
        ;;
    esac
    ;;
esac

if { [ "$INFRA_JOB_COUNT" -eq 0 ] && [ "$INFRA_CAUSE_COUNT" -ne 0 ]; } ||
   { [ "$INFRA_JOB_COUNT" -ne 0 ] && [ "$INFRA_CAUSE_COUNT" -eq 0 ]; } ||
   { [ "$FLAKY_JOB_COUNT" -eq 0 ] && [ "$FLAKY_TEST_COUNT" -eq 0 ] && [ "$FLAKY_CAUSE_COUNT" -ne 0 ]; } ||
   { { [ "$FLAKY_JOB_COUNT" -ne 0 ] || [ "$FLAKY_TEST_COUNT" -ne 0 ]; } && [ "$FLAKY_CAUSE_COUNT" -eq 0 ]; } ||
   { [ "$MAIN_BREAK_JOB_COUNT" -eq 0 ] && [ "$MAIN_BREAK_CAUSE_COUNT" -ne 0 ]; } ||
   { [ "$MAIN_BREAK_JOB_COUNT" -ne 0 ] && [ "$MAIN_BREAK_CAUSE_COUNT" -eq 0 ]; }; then
  echo "::error::Failed-job classifications and persisted cause types do not match"
  exit 1
fi

if ! jq -e \
  --argjson infra_job_ids "$INFRA_CAUSE_JOB_IDS" \
  --argjson flaky_job_ids "$FLAKY_CAUSE_JOB_IDS" \
  --argjson main_break_job_ids "$MAIN_BREAK_CAUSE_JOB_IDS" '
  all(.failed_jobs[];
    .id as $job_id |
    if .classification == "transient-infra" then
      ($infra_job_ids | index($job_id)) != null
    elif .classification == "flaky-test" then
      ($flaky_job_ids | index($job_id)) != null
    elif .classification == "main-repository-breakage" then
      ($main_break_job_ids | index($job_id)) != null
    else
      true
    end)
' "$ANALYSIS_FILE" >/dev/null; then
  echo "::error::Every transient, flaky, and main-breakage failed job must be covered by a matching cause"
  exit 1
fi
