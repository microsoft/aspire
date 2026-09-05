#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "Usage: $0 <analysis-file> <trusted-failed-jobs-file> <run-url>" >&2
  exit 1
fi

ANALYSIS_FILE="$1"
TRUSTED_FAILED_JOBS_FILE="$2"
RUN_URL="$3"

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
SANITIZED_ANALYSIS_FILE=$(mktemp)
SANITIZED_TRUSTED_FAILED_JOBS_FILE=$(mktemp)
trap 'rm -f "$SANITIZED_ANALYSIS_FILE" "$SANITIZED_TRUSTED_FAILED_JOBS_FILE"' EXIT
bash "$SCRIPT_DIR/analyze-ci-failure-persistence.sh" \
  sanitize-analysis "$ANALYSIS_FILE" "$SANITIZED_ANALYSIS_FILE"
ANALYSIS_FILE="$SANITIZED_ANALYSIS_FILE"
bash "$SCRIPT_DIR/analyze-ci-failure-persistence.sh" \
  sanitize-trusted-failed-jobs "$TRUSTED_FAILED_JOBS_FILE" "$SANITIZED_TRUSTED_FAILED_JOBS_FILE"
TRUSTED_FAILED_JOBS_FILE="$SANITIZED_TRUSTED_FAILED_JOBS_FILE"

jq -r --arg run_url "$RUN_URL" --slurpfile trusted_jobs "$TRUSTED_FAILED_JOBS_FILE" '
  ($trusted_jobs[0]) as $trusted_jobs |
  (.failed_jobs | map({key: (.id | tostring), value: .}) | from_entries) as $analysis_jobs |
  def code_span:
    gsub("[\r\n\t]+"; " ") as $value |
    (([ $value | scan("`+") | length ] | max // 0) + 1) as $delimiter_length |
    ("`" * $delimiter_length) + " " + $value + " " + ("`" * $delimiter_length);
  def indented_block($spaces):
    (" " * $spaces) as $indent |
    split("\n") | map($indent + .) | join("\n");
  def job_list:
    [$trusted_jobs[] |
      . as $trusted_job |
      ($analysis_jobs[($trusted_job.id | tostring)]) as $analysis_job |
      "- " + ($trusted_job.name | code_span) + " — " +
      (($analysis_job.reason // "") | code_span) +
      " (\($analysis_job.classification))"]
    | join("\n");
  def trusted_job_suffix($reported_name):
    ($trusted_jobs | map(select(.name == $reported_name)) | first) as $trusted_job |
    if $trusted_job == null then "" else " in job " + ($trusted_job.name | code_span) end;
  def test_list:
    [.failed_tests[] | select(.classification == "flaky") |
      "- " + (.name | code_span) + trusted_job_suffix(.job) +
      "\n  - **Error**:\n\n" + (.error | indented_block(8)) + "\n" +
      (if (.stack_trace // "") != "" then
        "  - **Stack Trace** (first frames):\n\n" +
        (.stack_trace | split("\n") | .[0:5] | join("\n") | indented_block(8)) + "\n"
      else "" end) +
      "  - **Why likely flaky**: " + (.reason | code_span)]
    | join("\n");
  def test_section:
    test_list as $tests |
    if $tests == "" then "" else "\n\n**Suspected flaky test(s):**\n" + $tests end;

  "<!-- analyze-ci-failure -->\n" +
  if .verdict == "transient-infra" then
    "🔍 **CI Failure Analysis: Transient Infrastructure Failure**\n\nThe CI build failed due to transient infrastructure issues.\n\n**Failed jobs:**\n" + job_list + "\n\nIf a rerun was not already requested automatically, visit the [workflow run page](" + $run_url + ") to rerun the failed jobs manually.\n"
  elif .verdict == "flaky-test" then
    "⚠️ **CI Failure Analysis: Possible Flaky Test(s)**\n\nThe CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.\n\n**Failed jobs:**\n" + job_list + test_section + "\n\n**Suggested actions:**\n- Re-run the failed CI jobs to confirm if the failure is intermittent\n- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using `/quarantine-test <test name> <issue URL>`\n- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky\n\nYou can re-run the failed jobs from the [workflow run page](" + $run_url + ").\n"
  elif .verdict == "code-issue" then
    "❌ **CI Failure Analysis: Code Issue Detected**\n\nThe CI build failed due to issue(s) caused by changes in this PR.\n\n**Failed jobs:**\n" + job_list + "\n\nThe CI will not be automatically rerun. Please fix the issue and push an updated commit.\n"
  else
    "⚠️ **CI Failure Analysis: Mixed Failures**\n\nThe CI build contains both transient and non-transient failures.\n\n**Failed jobs:**\n" + job_list + test_section + "\n\nThe CI will not be automatically rerun. Please review the failures above.\n"
  end
' "$ANALYSIS_FILE"
