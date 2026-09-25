#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

COMMAND="${1:?command is required}"
CI_FAILURE_DATA_DIR="${CI_FAILURE_DATA_DIR:-ci-failure-data}"
RUN_CONTEXT_FILE="$CI_FAILURE_DATA_DIR/run-context.json"

JQ_SANITIZE_DEFS=$(cat <<'JQ'
  def sensitive_name:
    "(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+)?|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)";
  def display_sensitive_name:
    "(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)";
  def connection_name:
    "(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+)?)";
  def bare_connection_name:
    "(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?)";
  def display_connection_name:
    "(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+))";
  def option_name:
    "(?i:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|connection[_-]?strings?|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)";
  def redact_sensitive_with($field_names; $connection_names; $bare_connection_equals_only):
    gsub("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)"; "[REDACTED]") |
    gsub("(?<prefix>\\b(?i:authorization|proxy-authorization)\\s*:\\s*(?i:basic|bearer)\\s+)[^\\s,;]+"; "\(.prefix)[REDACTED]") |
    gsub("(?<prefix>\\b(?i:x-api-key|api-key|access-token|client-secret)\\s*:\\s*)[^\\s,;]+"; "\(.prefix)[REDACTED]") |
    gsub("(?<scheme>\\b[A-Za-z][A-Za-z0-9+.-]*://)[^\\s/:@]*:[^\\s/@]+@"; "\(.scheme)[REDACTED]:[REDACTED]@") |
    gsub("(?<scheme>\\b[A-Za-z][A-Za-z0-9+.-]*://)[^\\s/:@]+@"; "\(.scheme)[REDACTED]@") |
    gsub("\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b"; "[REDACTED]") |
    gsub("\\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|(?:npm|pypi)-[A-Za-z0-9_-]{20,})\\b"; "[REDACTED]") |
    gsub("(?<prefix>[?&](?i:sig|signature|token|access_token|api[_-]?key|password|secret|client_secret)=)[^&\\s]+"; "\(.prefix)[REDACTED]") |
    gsub("(?<prefix>\"" + sensitive_name + "\"\\s*:\\s*\")(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*(?<suffix>\")"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>'" + sensitive_name + "'\\s*:\\s*')(?:\\\\[^\\r\\n]|[^'\\\\\\r\\n])*(?<suffix>')"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>(^|\\s)--" + option_name + "\\s+\")(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*(?<suffix>\")"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>(^|\\s)--" + option_name + "\\s+')(?:\\\\[^\\r\\n]|[^'\\\\\\r\\n])*(?<suffix>')"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>(^|[^\\S\\r\\n])--" + option_name + "[^\\S\\r\\n]+)(?![\"'])[^\\s]+"; "\(.prefix)[REDACTED]") |
    (if $bare_connection_equals_only then
       gsub("(?<prefix>\\b" + bare_connection_name + "\\s*=\\s*\")(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*(?<suffix>\")"; "\(.prefix)[REDACTED]\(.suffix)") |
       gsub("(?<prefix>\\b" + bare_connection_name + "\\s*=\\s*')(?:\\\\[^\\r\\n]|[^'\\\\\\r\\n])*(?<suffix>')"; "\(.prefix)[REDACTED]\(.suffix)") |
       gsub("(?<prefix>\\b" + $connection_names + "\\s*[:=]\\s*\")(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*(?<suffix>\")"; "\(.prefix)[REDACTED]\(.suffix)") |
       gsub("(?<prefix>\\b" + $connection_names + "\\s*[:=]\\s*')(?:\\\\[^\\r\\n]|[^'\\\\\\r\\n])*(?<suffix>')"; "\(.prefix)[REDACTED]\(.suffix)") |
       gsub("(?<prefix>\\b" + bare_connection_name + "\\s*=\\s*)(?![\"'])[^\\r\\n]+"; "\(.prefix)[REDACTED]") |
       gsub("(?<prefix>\\b" + $connection_names + "\\s*[:=]\\s*)(?![\"'])[^\\r\\n]+"; "\(.prefix)[REDACTED]")
     else
       gsub("(?<prefix>\\b" + $connection_names + "\\s*[:=]\\s*)(?![\"'])[^\\r\\n]+"; "\(.prefix)[REDACTED]")
     end) |
    gsub("(?<prefix>\\b" + $field_names + "\\s*[:=]\\s*\")(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*(?<suffix>\")"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>\\b" + $field_names + "\\s*[:=]\\s*')(?:\\\\[^\\r\\n]|[^'\\\\\\r\\n])*(?<suffix>')"; "\(.prefix)[REDACTED]\(.suffix)") |
    gsub("(?<prefix>\\b" + $field_names + "\\s*[:=]\\s*)(?![\"'])[^;&\\r\\n]+"; "\(.prefix)[REDACTED]");
  def redact_sensitive:
    redact_sensitive_with(sensitive_name; connection_name; false);
  def redact_display_metadata:
    redact_sensitive_with(display_sensitive_name; display_connection_name; true);
  def strip_unsafe:
    gsub("\u001b\\[[0-9;?]*[ -/]*[@-~]"; "") |
    gsub("\\p{Cf}|\\p{Zl}|\\p{Zp}|[\uFE00-\uFE0F]"; "") |
    gsub("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]"; "") |
    [explode[] | select((. < 917760 or . > 917999))] |
    implode;
  def sanitize_single_line:
    redact_sensitive |
    gsub("[\r\n\t]+"; " ") |
    strip_unsafe;
  def sanitize_multiline:
    redact_sensitive |
    gsub("\r\n?"; "\n") |
    strip_unsafe;
JQ
)

sanitize_document()
{
  local document_type="$1"
  local input_file="$2"
  local output_file="$3"

  # Redact before truncating so credentials spanning a field boundary cannot
  # evade detection. Then remove controls that can alter prompts or Markdown.
  jq --arg document_type "$document_type" "$JQ_SANITIZE_DEFS"'
    if $document_type == "cause" then
      if (.title | type) == "string" then .title |= sanitize_single_line else . end |
      if (.test_name | type) == "string" then .test_name |= sanitize_single_line else . end |
      # Current-run test names originate in analyzer-authored cause files.
      # Make them safe to validate, log, and render without changing their identity.
      if (.tests | type) == "array" then
        .tests |= map(if (.name | type) == "string" then .name |= sanitize_single_line else . end)
      else . end |
      # Historical occurrences can predate the current multi-test validation rules.
      # Reapply the persisted name bound before replay can render those records.
      if (.occurrences | type) == "array" then
        .occurrences |= map(
          if (.tests | type) == "array" then
            .tests |= map(if (.name | type) == "string" then
              .name |= (sanitize_single_line | .[0:500])
            else . end)
          else . end)
      else . end |
      if (.error_pattern | type) == "string" then .error_pattern |= sanitize_multiline else . end
    elif $document_type == "analysis" then
      if (.failed_jobs | type) == "array" then
        .failed_jobs |= map(
          if (type == "object") and ((.reason | type) == "string") then
            .reason |= (sanitize_single_line | .[0:500])
          else
            .
          end)
      else
        .
      end |
      if (.failed_tests | type) == "array" then
        .failed_tests |= map(
          if type == "object" then
            if (.name | type) == "string" then .name |= (sanitize_single_line | .[0:500]) else . end |
            if (.job | type) == "string" then .job |= (sanitize_single_line | .[0:500]) else . end |
            if (.error | type) == "string" then .error |= (sanitize_multiline | .[0:1000]) else . end |
            if (.stack_trace | type) == "string" then .stack_trace |= (sanitize_multiline | .[0:2000]) else . end |
            if (.standard_output | type) == "string" then .standard_output |= (sanitize_multiline | .[0:4000]) else . end |
            if (.standard_error | type) == "string" then .standard_error |= (sanitize_multiline | .[0:4000]) else . end |
            if (.reason | type) == "string" then .reason |= (sanitize_single_line | .[0:500]) else . end
          else
            .
          end)
      else
        .
      end
    else
      error("unsupported document type")
    end
  ' "$input_file" > "$output_file"
}

sanitize_trusted_failed_jobs()
{
  local input_file="$1"
  local output_file="$2"

  jq "$JQ_SANITIZE_DEFS"'
    if type != "array" then
      error("trusted failed jobs must be an array")
    else
      map(
        if type == "object" and (.id | type) == "number" and (.name | type) == "string" then
          .name |= (sanitize_single_line | .[0:500])
        else
          error("trusted failed job has an invalid shape")
        end)
    end
  ' "$input_file" > "$output_file"
}

sanitize_trusted_test_failures()
{
  local input_file="$1"
  local output_file="$2"

  jq "$JQ_SANITIZE_DEFS"'
    if type != "array" then
      error("trusted test failures must be an array")
    else
      map(
        if type == "object" and
           (.test | type) == "string" and
           ((.test | sanitize_single_line | length) > 0) and
           (.job | type) == "string" and
           ((.job | sanitize_single_line | length) > 0) and
           (.error | type) == "string" and
           ((.stack_trace == null) or (.stack_trace | type) == "string") and
           ((.standard_output == null) or (.standard_output | type) == "string") and
           ((.standard_error == null) or (.standard_error | type) == "string") then
          {
            test: (.test | sanitize_single_line | .[0:500]),
            job: (.job | sanitize_single_line | .[0:500]),
            error: (.error | sanitize_multiline | .[0:1000]),
            stack_trace: ((.stack_trace // "") | sanitize_multiline | .[0:2000]),
            standard_output: ((.standard_output // "") | sanitize_multiline | .[0:4000]),
            standard_error: ((.standard_error // "") | sanitize_multiline | .[0:4000])
          }
        else
          error("trusted test failure has an invalid shape")
        end) |
      unique_by([.test, .job, .error, .stack_trace, .standard_output, .standard_error])
    end
  ' "$input_file" > "$output_file"
}

collect_test_failures()
{
  local test_results_directory="$1"
  local job_name="$2"
  local failed_jobs_file="$3"
  local output_file="$4"
  local result_format="${5:-trx}"
  local json_lines
  local parse_failed=false

  if [ ! -d "$test_results_directory" ] ||
     [ -z "$job_name" ] ||
     ! jq -e '
       type == "array" and
       all(.[]; type == "object" and (.name | type) == "string")
     ' "$failed_jobs_file" >/dev/null ||
     ! jq -e --arg job "$job_name" \
       '[.[] | select(.name == $job)] | length == 1' \
       "$failed_jobs_file" >/dev/null; then
    echo "::error::Trusted test result provenance is invalid" >&2
    return 1
  fi

  case "$result_format" in
    trx|mocha)
      ;;
    *)
      echo "::error::Trusted test result format is invalid" >&2
      return 1
      ;;
  esac

  rm -f "$output_file"
  json_lines=$(mktemp)
  local result_count=0
  if [ "$result_format" = "trx" ]; then
    while IFS= read -r -d '' extracted_path; do
      result_count=$((result_count + 1))
      local parsed_lines
      parsed_lines=$(mktemp)
      if ! yq -p xml -o json '.' "$extracted_path" 2>/dev/null |
        jq -cr --arg job "$job_name" "$JQ_SANITIZE_DEFS"'
        # TRX represents one result as an object and multiple results as an array:
        #   <UnitTestResult testName="Tests.Failed" outcome="Failed">...</UnitTestResult>
        if type != "object" or (.TestRun | type) != "object" then
          error("test result does not have a TRX TestRun root")
        else
          .TestRun.Results.UnitTestResult // []
        end |
        (if type == "array" then . else [.] end) |
        map(select(.["+@outcome"] == "Failed")) |
        .[] |
        {
          test: (.["+@testName"] // ""),
          job: $job,
          error: ((.Output.ErrorInfo.Message // "") | if type == "object" then (.["+content"] // "") else tostring end | sanitize_multiline | .[0:1000]),
          stack_trace: ((.Output.ErrorInfo.StackTrace // "") | if type == "object" then (.["+content"] // "") else tostring end | sanitize_multiline | .[0:2000]),
          standard_output: ((.Output.StdOut // "") | if type == "object" then (.["+content"] // "") else tostring end | sanitize_multiline | .[0:4000]),
          standard_error: ((.Output.StdErr // "") | if type == "object" then (.["+content"] // "") else tostring end | sanitize_multiline | .[0:4000])
        }
      ' > "$parsed_lines"; then
        echo "::error::Unable to parse extracted test result $(basename "$extracted_path")" >&2
        parse_failed=true
      else
        cat "$parsed_lines" >> "$json_lines"
      fi
      rm -f "$parsed_lines"
    done < <(find "$test_results_directory" -maxdepth 1 -type f -name "*.trx" -print0)
  else
    while IFS= read -r -d '' extracted_path; do
      result_count=$((result_count + 1))
      local parsed_lines
      parsed_lines=$(mktemp)
      if ! jq -cr --arg job "$job_name" "$JQ_SANITIZE_DEFS"'
        def optional_string:
          if . == null then ""
          elif type == "string" then .
          else error("Mocha diagnostic field must be a string")
          end;
        def blocking_harness_error:
          (.name // "") as $name |
          (.message // "") as $message |
          ($name == "InvalidSessionIdError" or
           $name == "NoSuchSessionError" or
           $name == "NoSuchWindowError" or
           $name == "SessionNotCreatedError" or
           ($name == "WebDriverError" and
            ($message | ascii_downcase | test(
              "session deleted because of page crash|disconnected: not connected to devtools|chrome not reachable"))));
        # e2e-mocha-reporter.cjs writes:
        #   {"tests":[{"fullTitle":"suite test"}],
        #    "failures":[{"fullTitle":"suite test","err":{"name":"AssertionError","message":"...","stack":"..."}}]}
        if type != "object" or
           (.tests | type) != "array" or
           (.failures | type) != "array" then
          error("test result does not have the expected Mocha reporter shape")
        else
          [.tests[] |
            if type == "object" and
               (((.fullTitle // .title) | type) == "string") then
              .fullTitle // .title
            else
              error("Mocha completed test has an invalid shape")
            end] as $completed_tests |
          (.failures |
          map(
            (.fullTitle // .title) as $test |
            if type == "object" and
               (($test | type) == "string") and
               ($test | length) > 0 and
               (.err | type) == "object" then
              . + { normalized_test: $test }
            else
              error("Mocha failure has an invalid shape")
            end)) as $failures |
          if all(
            $failures[];
            . as $failure |
            (($completed_tests | index($failure.normalized_test)) != null) and
            (($failure.err | blocking_harness_error) | not)) then
            $failures
          else
            []
          end |
          .[] |
          {
            test: (.normalized_test | sanitize_single_line | .[0:500]),
            job: $job,
            error: ((.err.message | optional_string) | sanitize_multiline | .[0:1000]),
            stack_trace: ((.err.stack | optional_string) | sanitize_multiline | .[0:2000]),
            standard_output: "",
            standard_error: ""
          }
        end
      ' "$extracted_path" > "$parsed_lines"; then
        echo "::error::Unable to parse extracted test result $(basename "$extracted_path")" >&2
        parse_failed=true
      else
        cat "$parsed_lines" >> "$json_lines"
      fi
      rm -f "$parsed_lines"
    done < <(find "$test_results_directory" -maxdepth 1 -type f -name "*.json" -print0)
  fi

  if [ "$result_count" -eq 0 ]; then
    if [ "$result_format" = "trx" ]; then
      echo "::error::Selected test result artifact does not contain any TRX files" >&2
      rm -f "$json_lines"
      return 1
    else
      printf '[]\n' > "$output_file"
      rm -f "$json_lines"
      return 0
    fi
  fi
  if [ "$result_format" = "mocha" ] && [ "$result_count" -ne 1 ]; then
    echo "::error::Selected extension test artifact must contain exactly one Mocha result" >&2
    rm -f "$json_lines"
    return 1
  fi

  if [ "$parse_failed" = "true" ]; then
    rm -f "$json_lines"
    return 1
  fi

  jq -sc '.' "$json_lines" > "$output_file"
  rm -f "$json_lines"
}

sanitize_json_field()
{
  local input_file="$1"
  local field="$2"
  local max_length="$3"

  jq -er --arg field "$field" --argjson max_length "$max_length" "$JQ_SANITIZE_DEFS"'
    (.[$field] // "") |
    if type == "string" then
      sanitize_single_line | .[0:$max_length]
    else
      error("field must be a string")
    end
  ' "$input_file"
}

render_untrusted_json()
{
  local input_file="$1"
  local max_length="${2:-500}"
  local string_format="${3:-single-line}"

  jq -cer --argjson max_length "$max_length" --arg string_format "$string_format" "$JQ_SANITIZE_DEFS"'
    def sanitize_json:
      if type == "object" then
        with_entries(.value |= sanitize_json)
      elif type == "array" then
        map(sanitize_json)
      elif type == "string" then
        if $string_format == "single-line" then
          sanitize_single_line | .[0:$max_length]
        elif $string_format == "multiline" then
          sanitize_multiline | .[0:$max_length]
        else
          error("unsupported string format")
        end
      else
        .
      end;
    sanitize_json
  ' "$input_file" | sed 's/^/    /'
}

render_untrusted_text()
{
  local input_file="$1"
  local max_length="${2:-65536}"

  # A log line can terminate a fixed Markdown fence. Bound the sanitized text
  # before adding indentation so truncation can never remove the literal-data prefix.
  jq -Rrs --argjson max_length "$max_length" "$JQ_SANITIZE_DEFS"'
    sanitize_multiline |
    .[0:$max_length] |
    split("\n")[] |
    "    " + .
  ' "$input_file"
}

sanitize_untrusted_text()
{
  local input_file="$1"
  local output_file="$2"
  local max_length="${3:-10485760}"

  jq -Rrs --argjson max_length "$max_length" "$JQ_SANITIZE_DEFS"'
    sanitize_multiline |
    .[0:$max_length]
  ' "$input_file" > "$output_file"
}

# run-tests.yml names test jobs and their artifacts as:
#   Tests / No-package tests / Infrastructure (8-core-ubuntu-latest)
#   logs-Infrastructure-8-core-ubuntu-latest
# extension-e2e-tests.yml names test jobs and their diagnostic artifacts as:
#   Tests / VS Code extension E2E tests / VS Code extension E2E (Linux, debug)
#   extension-e2e-diagnostics-linux-x64-debug-attempt1
select_test_result_artifacts()
{
  local artifacts_file="$1"
  local started_at="$2"
  local updated_at="$3"
  local failed_jobs_file="$4"
  local max_artifacts="${5:-20}"
  local max_total_bytes="${6:-1073741824}"
  local max_artifact_bytes="${7:-104857600}"
  local run_attempt="${8:-1}"

  jq -cer \
    --arg started_at "$started_at" \
    --arg updated_at "$updated_at" \
    --argjson max_artifacts "$max_artifacts" \
    --argjson max_total_bytes "$max_total_bytes" \
    --argjson max_artifact_bytes "$max_artifact_bytes" \
    --argjson run_attempt "$run_attempt" \
    --slurpfile artifacts "$artifacts_file" '
      def run_tests_artifact_name:
        .name |
        capture("(^| / )(?<short>[^/]+) \\((?<runner>[^()]*)\\)$") |
        "logs-\(.short)-\(.runner)";

      def is_run_tests_job:
        # Keep these caller prefixes aligned with the run-tests.yml jobs in tests.yml.
        # The step check covers completed jobs; the prefixes cover force-killed jobs.
        any(.steps[]?; .name == "Upload logs, and test results") or
        (.name | test(
          "^Tests / (No-package tests|Package tests - (Linux|Windows|macOS)|CLI archive tests)( \\(| / )"));

      def is_extension_e2e_job:
        .name | test("(^| / )VS Code extension E2E( \\(|$)");

      def extension_e2e_artifact_name:
        .name |
        capture("(^| / )VS Code extension E2E \\((?<os>Windows|Linux), (?<shard>[^()]+)\\)$") |
        "extension-e2e-diagnostics-\(if .os == "Windows" then "win-x64" else "linux-x64" end)-\(.shard)-attempt\($run_attempt)";

      def artifact_contract:
        if is_run_tests_job then
          if (.name | test("(^| / )[^/]+ \\([^()]*\\)$")) then
            { name: run_tests_artifact_name, format: "trx" }
          else
            error("failed test job name does not match the artifact naming contract")
          end
        elif is_extension_e2e_job then
          if (.name | test("(^| / )VS Code extension E2E \\((Windows|Linux), [^()]+\\)$")) then
            { name: extension_e2e_artifact_name, format: "mocha" }
          else
            error("failed test job name does not match the artifact naming contract")
          end
        else
          empty
        end;

      [
        .[] |
        select(type == "object" and (.name | type) == "string") |
        . as $job |
        ($job | artifact_contract) as $contract |
        [
          $artifacts[0][] |
          select(
            type == "object" and
            .expired == false and
            .name == $contract.name and
            (.created_at | type) == "string" and
            (.created_at > $started_at and .created_at <= $updated_at))
        ] as $matches |
        if $matches | length == 0 then
          if $contract.format == "mocha" then
            empty
          else
            error("test result artifact is missing for a failed test job")
          end
        elif $matches | length == 1 then
          $matches[0] |
          {
            id,
            name,
            size_in_bytes,
            job: $job.name,
            format: $contract.format
          }
        else
          error("test result artifact does not identify exactly one failed job")
        end
      ] as $selected |
      ($selected | map(select(.format == "trx"))) as $required |
      ($selected | map(select(
        .format == "mocha" and
        ((.id | type) == "number" and
         (.id | floor) == .id and
         .id >= 1 and
         (.size_in_bytes | type) == "number" and
         (.size_in_bytes | floor) == .size_in_bytes and
         .size_in_bytes >= 0 and
         .size_in_bytes <= $max_artifact_bytes))) |
        sort_by([.job, .id])) as $optional |
      if ($required | length) > $max_artifacts then
        error("test result artifact count exceeds the download budget")
      elif any(
        $required[];
        (.id | type) != "number" or
        (.id | floor) != .id or
        .id < 1 or
        (.size_in_bytes | type) != "number" or
        (.size_in_bytes | floor) != .size_in_bytes or
        .size_in_bytes < 0 or
        .size_in_bytes > $max_artifact_bytes
      ) then
        error("test result artifact has invalid or excessive size metadata")
      elif ($required | map(.id) | unique | length) != ($required | length) then
        error("test result artifact does not identify exactly one failed job")
      elif ($required | map(.size_in_bytes) | add // 0) > $max_total_bytes then
        error("test result artifacts exceed the cumulative download budget")
      else
        ($max_artifacts - ($required | length)) as $remaining_count |
        ($max_total_bytes - ($required | map(.size_in_bytes) | add // 0)) as $remaining_bytes |
        (reduce $optional[] as $artifact (
          {artifacts: [], bytes: 0};
          if (.artifacts | length) < $remaining_count and
             (.bytes + $artifact.size_in_bytes) <= $remaining_bytes then
            {
              artifacts: (.artifacts + [$artifact]),
              bytes: (.bytes + $artifact.size_in_bytes)
            }
          else
            .
          end
        ) | .artifacts) as $selected_optional |
        ($required + $selected_optional) as $bounded_selected |
        if ($bounded_selected | map(.id) | unique | length) != ($bounded_selected | length) then
          error("test result artifact does not identify exactly one failed job")
        else
          $bounded_selected
        end
      end
    ' "$failed_jobs_file"
}

extract_test_results_artifact()
{
  local archive_file="$1"
  local output_directory="$2"
  local max_entries="${3:-10000}"
  local max_uncompressed_bytes="${4:-1073741824}"
  local max_archive_bytes="${5:-104857600}"
  local expected_archive_bytes="${6:-}"
  local result_format="${7:-trx}"

  if [[ ! "$max_entries" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_uncompressed_bytes" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_archive_bytes" =~ ^[1-9][0-9]*$ ]] ||
     { [ -n "$expected_archive_bytes" ] && [[ ! "$expected_archive_bytes" =~ ^[0-9]+$ ]]; } ||
     [[ ! "$result_format" =~ ^(trx|mocha)$ ]]; then
    echo "::error::Invalid test results extraction budget" >&2
    return 1
  fi

  python3 - "$archive_file" "$output_directory" \
    "$max_entries" "$max_uncompressed_bytes" "$max_archive_bytes" "$expected_archive_bytes" \
    "$result_format" <<'PY'
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import struct
import sys
import tempfile
import zipfile
import zlib

archive_path = Path(sys.argv[1])
output_path = Path(sys.argv[2])
max_entries = int(sys.argv[3])
max_uncompressed_bytes = int(sys.argv[4])
max_archive_bytes = int(sys.argv[5])
expected_archive_bytes = int(sys.argv[6]) if sys.argv[6] else None
result_format = sys.argv[7]
temporary_path = None

def read_entry_count(path, archive_size, maximum_entries):
    end_record_size = 22
    maximum_comment_size = 65535
    with path.open("rb") as archive:
        tail_size = min(archive_size, end_record_size + maximum_comment_size)
        archive.seek(archive_size - tail_size)
        tail = archive.read(tail_size)

    signature = b"PK\x05\x06"
    position = tail.rfind(signature)
    while position >= 0:
        if len(tail) - position >= end_record_size:
            fields = struct.unpack_from("<4s4H2LH", tail, position)
            comment_length = fields[7]
            if position + end_record_size + comment_length == len(tail):
                break
        position = tail.rfind(signature, 0, position)
    if position < 0:
        raise ValueError("archive has no valid end-of-central-directory record")

    _, disk_number, directory_disk, disk_entries, total_entries, directory_size, directory_offset, _ = fields
    if disk_number != 0 or directory_disk != 0 or disk_entries != total_entries:
        raise ValueError("multi-disk archives are unsupported")
    if position >= 20 and tail[position - 20:position - 16] == b"PK\x06\x07":
        raise ValueError("ZIP64 archives exceed the supported extraction limits")
    if (
        total_entries == 0xFFFF
        or directory_size == 0xFFFFFFFF
        or directory_offset == 0xFFFFFFFF
    ):
        raise ValueError("ZIP64 archives exceed the supported extraction limits")

    end_record_offset = archive_size - tail_size + position
    if directory_offset + directory_size != end_record_offset:
        raise ValueError("archive central directory bounds are invalid")

    central_header = struct.Struct("<4s6H3L5H2L")
    actual_entries = 0
    consumed_bytes = 0
    with path.open("rb") as archive:
        archive.seek(directory_offset)
        while consumed_bytes < directory_size:
            header = archive.read(central_header.size)
            if len(header) != central_header.size:
                raise ValueError("archive central directory is truncated")
            fields = central_header.unpack(header)
            if fields[0] != b"PK\x01\x02":
                raise ValueError("archive central directory contains an invalid record")

            variable_size = fields[10] + fields[11] + fields[12]
            record_size = central_header.size + variable_size
            consumed_bytes += record_size
            if consumed_bytes > directory_size:
                raise ValueError("archive central directory record exceeds its bounds")

            actual_entries += 1
            if actual_entries > maximum_entries:
                raise ValueError(
                    f"archive contains more than the {maximum_entries}-entry budget"
                )
            archive.seek(variable_size, os.SEEK_CUR)

    if actual_entries != total_entries:
        raise ValueError("archive entry count does not match its central directory")

    return actual_entries

try:
    archive_size = archive_path.stat().st_size
    if archive_size > max_archive_bytes:
        raise ValueError(
            f"downloaded archive exceeds the {max_archive_bytes}-byte budget"
        )
    if expected_archive_bytes is not None and archive_size != expected_archive_bytes:
        raise ValueError(
            "downloaded archive size does not match artifact metadata "
            f"({archive_size} != {expected_archive_bytes})"
        )
    entry_count = read_entry_count(archive_path, archive_size, max_entries)
    if output_path.exists():
        raise ValueError("test results output directory already exists")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = Path(
        tempfile.mkdtemp(prefix=f".{output_path.name}-", dir=output_path.parent)
    )

    with zipfile.ZipFile(archive_path) as archive:
        entries = archive.infolist()
        if len(entries) != entry_count:
            raise ValueError("archive entry count does not match its central directory")

        written_bytes = 0
        result_index = 0
        for entry in entries:
            raw_name = entry.filename
            normalized_name = raw_name.rstrip("/")
            path = PurePosixPath(normalized_name)
            if (
                not normalized_name
                or raw_name.startswith(("/", "\\"))
                or "\\" in raw_name
                or any(part in ("", ".", "..") for part in path.parts)
            ):
                raise ValueError("archive contains an unsafe path")

            file_type = stat.S_IFMT(entry.external_attr >> 16)
            if entry.is_dir():
                if file_type not in (0, stat.S_IFDIR):
                    raise ValueError("archive contains an unsupported file type")
                continue
            if file_type not in (0, stat.S_IFREG):
                raise ValueError("archive contains an unsupported file type")
            if entry.flag_bits & 0x1:
                raise ValueError("archive contains an encrypted entry")
            if result_format == "trx":
                is_result = raw_name.endswith(".trx")
                result_extension = "trx"
            else:
                is_result = path.name == "mocha.json"
                result_extension = "json"
            if not is_result:
                continue

            result_index += 1
            destination = temporary_path / f"{result_index:05d}.{result_extension}"
            with archive.open(entry, "r") as source, destination.open("xb") as target:
                while chunk := source.read(1024 * 1024):
                    written_bytes += len(chunk)
                    if written_bytes > max_uncompressed_bytes:
                        raise ValueError(
                            "uncompressed data exceeds the "
                            f"{max_uncompressed_bytes}-byte budget"
                        )
                    target.write(chunk)

    os.replace(temporary_path, output_path)
    temporary_path = None
except (EOFError, OSError, OverflowError, RuntimeError, ValueError, zipfile.BadZipFile, zlib.error) as error:
    print(f"::error::Unable to extract test results artifact: {error}", file=sys.stderr)
    sys.exit(1)
finally:
    if temporary_path is not None:
        shutil.rmtree(temporary_path, ignore_errors=True)
PY
}

# Persist the display-only fields needed to recreate the current occurrence row
# after merging has settled which stored evidence is authoritative.
backfill_occurrence_publication()
{
  local cause_file="$1"
  local trusted_failed_jobs_file="$2"
  local run_id="$3"
  local run_attempt="$4"
  local run_scope="$5"
  local output_file="$6"
  local occurrence_file
  local publication_cause_file
  local jobs_table
  local issue_context

  if [ ! -f "$cause_file" ] ||
     [ ! -f "$trusted_failed_jobs_file" ] ||
     [[ ! "$run_id" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$run_attempt" =~ ^[1-9][0-9]*$ ]] ||
     { [ "$run_scope" != "main" ] && [ "$run_scope" != "pull-request" ]; }; then
    echo "::error::Invalid occurrence publication input" >&2
    return 1
  fi

  occurrence_file=$(mktemp)
  publication_cause_file=$(mktemp)
  jq \
    --argjson run_id "$run_id" \
    --argjson run_attempt "$run_attempt" '
      first(
        .occurrences[]? |
        select(
          .run_id == $run_id and
          (if has("run_attempt") then .run_attempt else 1 end) == $run_attempt
        )
      ) // empty
    ' "$cause_file" > "$occurrence_file"
  if [ ! -s "$occurrence_file" ]; then
    echo "::error::Stored cause is missing the current occurrence" >&2
    rm -f "$occurrence_file" "$publication_cause_file"
    return 1
  fi

  if jq -e '
      (.issue_jobs_table | type) == "string" and
      (.issue_jobs_table | length) > 0 and
      (.issue_context | type) == "string" and
      (.issue_context | test("^(main|unavailable|#[1-9][0-9]*)$")) and
      (.issue_row_needs_refresh != true)
    ' "$occurrence_file" >/dev/null; then
    cp "$cause_file" "$output_file"
    rm -f "$occurrence_file" "$publication_cause_file"
    return
  fi

  jq -n \
    --arg id "$(jq -r '.id' "$cause_file")" \
    --arg type "$(jq -r '.type' "$cause_file")" \
    --slurpfile causes "$cause_file" \
    --slurpfile occurrences "$occurrence_file" '
      ($occurrences[0]) as $occurrence |
      {
        id: $id,
        type: $type
      } +
      # Legacy flaky records can predate structured per-job tests. Preserve
      # their scalar label for issue display without promoting it into identity.
      (if $type == "flaky-test" and
          (($occurrence.tests | type) != "array") and
          ((($occurrence.job_ids | type) == "array") or
           ((($occurrence.job | type) == "string") and
            (($occurrence.job | length) > 0))) and
          (($causes[0].test_name | type) == "string") and
          (($causes[0].test_name | length) > 0) then
        {test_name: $causes[0].test_name}
      else
        {}
      end) +
      (if ($occurrence.job_ids | type) == "array" then
        {job_ids: $occurrence.job_ids}
      elif ($occurrence.tests | type) == "array" then
        {
          job_ids: (
            reduce $occurrence.tests[] as $test
              ([]; if index($test.job_id) == null then
                . + [$test.job_id]
              else
                .
              end)
          )
        }
      else
        {}
      end) +
      (if ($occurrence.tests | type) == "array" then
        {tests: $occurrence.tests}
      else
        {}
      end) +
      (if (($occurrence.job | type) == "string") and
          (($occurrence.job | length) > 0) and
          (($occurrence.issue_row_needs_refresh == true) or
           ((($occurrence.job_ids | type) != "array") and
            (($occurrence.tests | type) != "array"))) then
        {job_names: [$occurrence.job]}
      else
        {}
      end)
    ' > "$publication_cause_file"

  jobs_table=$(bash "$0" cause-job-names \
    "$publication_cause_file" "$trusted_failed_jobs_file" table)
  # Persisted history can predate current validation. Only a positive JSON
  # integer is trusted as PR context; malformed legacy metadata falls through
  # to the authenticated run-scope fallback.
  issue_context=$(jq -r \
    --arg run_scope "$run_scope" '
      if $run_scope == "main" then
        "main"
      elif ((.pr_number | type) == "number") and
         (.pr_number > 0) and
         (.pr_number == (.pr_number | floor)) then
        "#" + (.pr_number | floor | tostring)
      else
        "unavailable"
      end
    ' "$occurrence_file")

  jq \
    --argjson run_id "$run_id" \
    --argjson run_attempt "$run_attempt" \
    --arg jobs_table "$jobs_table" \
    --arg issue_context "$issue_context" \
    --arg run_scope "$run_scope" '
      .occurrences |= map(
        if .run_id == $run_id and
           (if has("run_attempt") then .run_attempt else 1 end) == $run_attempt then
          .issue_jobs_table = $jobs_table |
          .issue_context = $issue_context |
          .run_scope = $run_scope |
          if .issue_uses_stored_job_label == true or
             ((.job | type) == "string" and
              (.job | length) > 0 and
              ((.issue_row_needs_refresh == true) or
               (((.job_ids | type) != "array") and
                ((.tests | type) != "array")))) then
            .issue_uses_stored_job_label = true
          else
            .
          end
        else
          .
        end
      )
    ' "$cause_file" > "$output_file"
  rm -f "$occurrence_file" "$publication_cause_file"
}

# Render the complete ordered managed-row projection solely from stored memory.
stored_occurrence_rows()
{
  local cause_file="$1"
  local runs_directory="${2:-}"
  local expected_repository="${GITHUB_REPOSITORY:-microsoft/aspire}"
  local trusted_main_runs='{}'

  if [ ! -f "$cause_file" ]; then
    echo "::error::Stored cause file is required" >&2
    return 1
  fi

  if [ -n "$runs_directory" ] && [ -d "$runs_directory" ]; then
    local run_summary
    for run_summary in "$runs_directory"/*.json; do
      [ -f "$run_summary" ] || continue
      local run_file_name
      run_file_name=$(basename "$run_summary")
      if [[ "$run_file_name" =~ ^([1-9][0-9]*)\.json$ ]]; then
        local file_run_id="${BASH_REMATCH[1]}"
        if jq -e --argjson file_run_id "$file_run_id" '
            (.run_id | type) == "number" and
            .run_id == $file_run_id and
            .run_scope == "main"
          ' "$run_summary" >/dev/null 2>&1; then
          trusted_main_runs=$(jq -c \
            --arg run_id "$file_run_id" \
            '. + {($run_id): "main"}' <<< "$trusted_main_runs")
        fi
      fi
    done
  fi

  jq \
    --arg repository "$expected_repository" \
    --argjson trusted_main_runs "$trusted_main_runs" \
    "$JQ_SANITIZE_DEFS"'
    def render_code_span:
      (([scan("`+") | length] | max // 0) + 1) as $delimiter_length |
      ("`" * $delimiter_length) + " " + . + " " + ("`" * $delimiter_length);
    def table_text:
      sanitize_single_line |
      .[0:120] |
      gsub("\\|"; "\\|") |
      render_code_span;
    . as $cause |
    [(.occurrences // [] | sort_by(.observed_at // .occurred_at))[] |
      . as $occurrence |
      ($occurrence.observed_at // $occurrence.occurred_at // "") as $observed_at |
      (if ($occurrence | has("run_scope")) then
         (if $occurrence.run_scope == "main" or
             $occurrence.run_scope == "pull-request" then
            $occurrence.run_scope
          else
            ""
          end)
       else
         ($trusted_main_runs[($occurrence.run_id | tostring)] // "")
       end) as $effective_run_scope |
      ($occurrence | has("run_attempt")) as $has_run_attempt |
      ($occurrence | if has("run_attempt") then .run_attempt else null end) as $run_attempt |
      # Modern metadata must identify one exact run attempt:
      #   attempt 1 -> https://github.com/microsoft/aspire/actions/runs/123/attempts/1
      #   attempt 2 -> https://github.com/microsoft/aspire/actions/runs/123/attempts/2
      # Legacy records without run_attempt retain their historical run-only URL.
      (if (($occurrence.run_id | type) == "number") and
          ($occurrence.run_id > 0) and
          ($occurrence.run_id == ($occurrence.run_id | floor)) and
          (($has_run_attempt | not) or
           ((($run_attempt | type) == "number") and
            ($run_attempt > 0) and
            ($run_attempt == ($run_attempt | floor)))) then
        "https://github.com/" + $repository + "/actions/runs/" +
        ($occurrence.run_id | tostring)
      else
        ""
      end) as $base_run_url |
      ($base_run_url +
        (if $has_run_attempt then
          "/attempts/" + ($run_attempt | tostring)
        else
          ""
        end)) as $expected_run_url |
      if (($occurrence.run_id | type) != "number") or
         ($occurrence.run_id <= 0) or
         ($occurrence.run_id != ($occurrence.run_id | floor)) or
         ($has_run_attempt and
          ((($run_attempt | type) != "number") or
           ($run_attempt <= 0) or
           ($run_attempt != ($run_attempt | floor)))) or
         (($occurrence.run_url | type) != "string") or
         (($occurrence.run_url != $expected_run_url) and
          (($has_run_attempt and
            $run_attempt == 1 and
            $occurrence.run_url == $base_run_url) | not)) or
         (($observed_at | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T")) | not)
      then
        error("stored occurrence cannot be rendered")
      else
        ($observed_at | split("T")[0]) as $date |
        (if (($occurrence.issue_jobs_table | type) == "string") and
            (($occurrence.issue_jobs_table | length) > 0) then
          $occurrence.issue_jobs_table
        elif (($occurrence.job_names | type) == "array") and
             (($occurrence.job_names | length) > 0) then
          ([$occurrence.job_names[] | table_text] +
            (if ($occurrence.tests | type) == "array" then
              [$occurrence.tests[0:20][] | .name | table_text]
            elif ($cause.type == "flaky-test") and
                 (($cause.test_name | type) == "string") and
                 (($cause.test_name | length) > 0) then
              [$cause.test_name | table_text]
            else
              []
            end) |
            join("<br>"))
        elif (($occurrence.job | type) == "string") and
             (($occurrence.job | length) > 0) then
          (($occurrence.job | table_text) +
            (if ($occurrence.tests | type) == "array" then
              ([$occurrence.tests[0:20][] | .name | table_text] |
                if length == 0 then "" else "<br>" + join("<br>") end) +
              (if ($occurrence.tests | length) > 20 then
                "<br>` \(($occurrence.tests | length) - 20) more tests in the linked run `"
              else
                ""
              end)
            elif ($cause.type == "flaky-test") and
                 (($cause.test_name | type) == "string") and
                 (($cause.test_name | length) > 0) then
              "<br>" + ($cause.test_name | table_text)
            else
              ""
            end))
        else
          error("stored occurrence is missing displayable jobs")
        end) as $jobs_table |
        (if $effective_run_scope == "main" or
            $cause.type == "main-repository-breakage" then
          "main"
        elif $effective_run_scope == "pull-request" then
          (if (($occurrence.issue_context | type) == "string") and
              ($occurrence.issue_context | test("^#[1-9][0-9]*$")) then
            $occurrence.issue_context
          elif (($occurrence.pr_number | type) == "number") and
               ($occurrence.pr_number > 0) and
               ($occurrence.pr_number == ($occurrence.pr_number | floor)) then
            "#" + ($occurrence.pr_number | floor | tostring)
          else
            "unavailable"
          end)
        elif (($occurrence.issue_context | type) == "string") and
             ($occurrence.issue_context | test("^(main|unavailable|#[1-9][0-9]*)$")) then
          $occurrence.issue_context
        elif (($occurrence.pr_number | type) == "number") and
             ($occurrence.pr_number > 0) and
             ($occurrence.pr_number == ($occurrence.pr_number | floor)) then
          "#" + ($occurrence.pr_number | floor | tostring)
        else
          "unavailable"
        end) as $context |
        "| \($date) | [\($occurrence.run_id)](\($expected_run_url)) | \($jobs_table) | \($context) |"
      end
    ]
  ' "$cause_file"
}

render_issue_occurrences()
{
  # Rebuild only the publisher-managed occurrence section while preserving
  # operator-authored issue text outside it.
  #
  # The current body supplies only the operator-authored prefix and suffix.
  # occurrence_rows_file supplies every canonical row from persisted history,
  # including the attempt being published or replayed.
  #
  # The stored row sequence is authoritative: it repairs stale or missing issue
  # rows, keeps delayed replay from reordering history, and ensures body trimming
  # retains the newest evidence. max_bytes is the final issue-body budget, and
  # output_file receives the rebuilt body.
  local current_body_file="$1"
  local new_occurrence_row="$2"
  local total_occurrence_count="$3"
  local output_file="$4"
  local occurrence_rows_file="$5"
  local max_bytes="$6"
  local expected_repository="${GITHUB_REPOSITORY:-microsoft/aspire}"
  local output_temp

  if [ ! -f "$current_body_file" ] ||
     [ ! -f "$occurrence_rows_file" ] ||
     [[ ! "$total_occurrence_count" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_bytes" =~ ^[1-9][0-9]*$ ]]; then
    echo "::error::Invalid occurrence renderer input" >&2
    return 1
  fi

  output_temp=$(mktemp)
  if ! jq -nj \
      --rawfile body "$current_body_file" \
      --arg new_row "$new_occurrence_row" \
      --arg repository "$expected_repository" \
      --argjson total "$total_occurrence_count" \
      --argjson max_bytes "$max_bytes" \
      --slurpfile occurrence_rows "$occurrence_rows_file" '
      def normalized_body:
        $body | gsub("\r\n"; "\n");
      # Accept only workflow run links, with an optional explicit retry:
      #   https://github.com/microsoft/aspire/actions/runs/123
      #   https://github.com/microsoft/aspire/actions/runs/123/attempts/2
      # Reject zero or noncanonical IDs, job links, query strings, fragments,
      # and malformed repository paths.
      def is_occurrence_url:
        startswith("https://github.com/" + $repository + "/actions/runs/") and
        test(
          "^https://github\\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/" +
          "[1-9][0-9]*(/attempts/[1-9][0-9]*)?$"
        );
      # A managed row has the exact Date | Build | Job | Context table shape:
      #   | 2026-09-23 | [123](https://github.com/microsoft/aspire/actions/runs/123) | Ubuntu | #42 |
      # The visible build ID must identify the linked run; reject misleading
      # pairs such as [999](.../actions/runs/123), along with noncanonical URLs
      # and rows with another context shape.
      def occurrence_parts:
        (capture(
          "^\\| [0-9]{4}-[0-9]{2}-[0-9]{2} \\| " +
          "\\[(?<label>[1-9][0-9]*)\\]\\(" +
          "(?<url>https://github\\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/" +
          "(?<run>[1-9][0-9]*)(/attempts/[1-9][0-9]*)?)" +
          "\\) \\| (?:[^|\\n]|\\\\\\|)+ \\| (main|unavailable|#[1-9][0-9]*) \\|$"
        ) // null);
      def is_occurrence_row:
        occurrence_parts as $parts |
        if $parts == null then false
        else $parts.label == $parts.run and ($parts.url | is_occurrence_url)
        end;
      # An unmarked legacy table can contain an old or edited repository link.
      # Recognize its generated row shape only to find the replaceable table
      # boundary; the replacement rows still come exclusively from validated
      # stored memory. A mismatched label/link pair remains ambiguous.
      def is_replaceable_legacy_occurrence_row:
        occurrence_parts as $parts |
        $parts != null and $parts.label == $parts.run;
      def resembles_occurrence_row:
        test("^\\| [0-9]{4}-[0-9]{2}-[0-9]{2} \\|");
      # Reuse the validated row parse so identity, ordering, and display cannot
      # disagree about which run an occurrence represents.
      def occurrence_url:
        occurrence_parts as $parts |
        if $parts != null and
           $parts.label == $parts.run and
           ($parts.url | is_occurrence_url) then
          $parts.url
        else
          error("invalid occurrence row")
        end;
      def section($rows):
        "<!-- ci-failure-occurrences:start -->\n" +
        "## Occurrences\n\n" +
        "Showing \($rows | length) most recent of \($total) occurrences.\n\n" +
        "| Date | Build | Job | Context |\n" +
        "|------|-------|-----|----|\n" +
        ($rows | join("\n")) + "\n" +
        "<!-- ci-failure-occurrences:end -->";
      def render($prefix; $suffix; $rows):
        ($prefix | sub("\n+$"; "")) + "\n\n" + section($rows) + $suffix;
      def fit($prefix; $suffix; $rows):
        render($prefix; $suffix; $rows) as $rendered |
        if ($rendered | utf8bytelength) <= $max_bytes then
          $rendered
        elif ($rows | length) > 1 then
          fit($prefix; $suffix; $rows[1:])
        else
          error("occurrence section cannot fit within the publication budget")
        end;
      def managed_parts:
        (normalized_body | split("<!-- ci-failure-occurrences:start -->")) as $start_parts |
        if ($start_parts | length) != 2 then
          error("ambiguous managed occurrence section")
        else
          ($start_parts[1] | split("<!-- ci-failure-occurrences:end -->")) as $end_parts |
          if ($end_parts | length) != 2 then
            error("ambiguous managed occurrence section")
          else
            # The explicit end marker separates workflow-owned rows from later
            # operator notes. Preserve that suffix exactly and include it in the
            # issue-body budget instead of treating it as malformed history.
            {
              prefix: $start_parts[0],
              managed: $end_parts[0],
              suffix: (if $end_parts[1] == "" then "\n" else $end_parts[1] end),
              legacy: false
            }
          end
        end;
      def legacy_parts:
        (normalized_body | split("\n## Occurrences\n")) as $parts |
        if ($parts | length) != 2 then
          error("unsupported legacy occurrence section")
        else
          ("## Occurrences\n" + $parts[1]) as $legacy |
          ($legacy | split("\n")) as $lines |
          ([range(0; ($lines | length)) |
            select(
              $lines[.] == "| Date | Build | Job | Context |" or
              $lines[.] == "| Date | Build | Job | PR |"
            )]) as $header_indexes |
          if ($lines[0] != "## Occurrences") or
             ($header_indexes | length) != 1
          then
            error("unsupported legacy occurrence section")
          else
            $header_indexes[0] as $header_index |
            if $header_index == 0 or
               ($header_index + 1) >= ($lines | length) or
               $lines[$header_index + 1] != "|------|-------|-----|----|" or
               any($lines[1:$header_index][];
                 length > 0 and
                 (test("^Showing [0-9]+ most recent of [0-9]+ occurrences\\.$") | not)) or
               ([$lines[1:$header_index][] |
                 select(test("^Showing [0-9]+ most recent of [0-9]+ occurrences\\.$"))] | length) > 1
            then
              error("unsupported legacy occurrence section")
            else
              ($header_index + 2) as $row_start |
              ([range($row_start; ($lines | length)) |
                select(($lines[.] | is_replaceable_legacy_occurrence_row) | not)] |
                if length == 0 then ($lines | length) else .[0] end) as $suffix_start |
              ($lines[$suffix_start:]) as $suffix_lines |
              # Legacy issues have no end marker. Treat only the contiguous
              # generated table as workflow-owned, preserve later operator
              # notes, and reject rows interleaved with that suffix.
              if any($suffix_lines[]; resembles_occurrence_row) then
                error("ambiguous legacy occurrence section")
              else
                {
                  prefix: $parts[0],
                  managed: ($lines[0:$suffix_start] | join("\n")),
                  suffix: (
                    if ($suffix_lines | length) == 0
                    then "\n"
                    else "\n" + ($suffix_lines | join("\n"))
                    end
                  ),
                  legacy: true
                }
              end
            end
          end
        end;
      # Rendering must be based on the complete persisted history. Reject
      # partial, duplicated, or malformed canonical rows rather than retaining
      # editable issue-body content as if it were stored evidence.
      if ($occurrence_rows | length) != 1 or
         (($occurrence_rows[0] | type) != "array") or
         (($occurrence_rows[0] | length) != $total) or
         (all($occurrence_rows[0][]; type == "string" and is_occurrence_row) | not) or
         (([$occurrence_rows[0][] | occurrence_url] | unique | length) != $total)
      then
        error("invalid stored occurrence rows")
      elif ($new_row | is_occurrence_row | not) then
        error("invalid occurrence row")
      elif ($occurrence_rows[0] | index($new_row)) == null then
        error("new occurrence row is absent from stored history")
      else
        (if (normalized_body | contains("<!-- ci-failure-occurrences:start -->")) or
            (normalized_body | contains("<!-- ci-failure-occurrences:end -->")) then
          managed_parts
        else
          legacy_parts
        end) as $parts |
        ($parts.managed | split("\n")) as $lines |
        if any($lines[];
          length > 0 and
          . != "## Occurrences" and
          . != "| Date | Build | Job | Context |" and
          ($parts.legacy == false or . != "| Date | Build | Job | PR |") and
          . != "|------|-------|-----|----|" and
          (test("^Showing [0-9]+ most recent of [0-9]+ occurrences\\.$") | not) and
          (resembles_occurrence_row | not))
        then
          error("unsupported occurrence section contents")
        else
          # Issue rows are a projection of memory, not another history store.
          # Replace the complete managed section so edits, duplicates, missing
          # rows, and previously trimmed rows are repaired from canonical data.
          fit($parts.prefix; $parts.suffix; $occurrence_rows[0]) as $rendered |
          # Avoid a no-op issue edit when replay only differs in trailing
          # newlines; this keeps publication idempotent.
          if (normalized_body | sub("\n+$"; "")) == ($rendered | sub("\n+$"; "")) then
            $body
          else
            $rendered
          end
        end
      end
    ' > "$output_temp"; then
    rm -f "$output_temp"
    return 2
  fi

  mv "$output_temp" "$output_file"
}

migrate_main_issue_body()
{
  local current_body_file="$1"
  local canonical_body_file="$2"
  local output_file="$3"
  local max_bytes="$4"
  local output_temp

  if [ ! -f "$current_body_file" ] ||
     [ ! -f "$canonical_body_file" ] ||
     [[ ! "$max_bytes" =~ ^[1-9][0-9]*$ ]]; then
    echo "::error::Invalid main issue migration input" >&2
    return 1
  fi

  output_temp=$(mktemp)
  if ! jq -nj \
      --rawfile current "$current_body_file" \
      --rawfile canonical "$canonical_body_file" \
      --argjson max_bytes "$max_bytes" '
      def normalized:
        gsub("\r\n"; "\n");
      def managed_parts:
        (normalized | split("<!-- ci-failure-occurrences:start -->")) as $start_parts |
        if ($start_parts | length) != 2 then
          error("ambiguous managed occurrence section")
        else
          ($start_parts[1] | split("<!-- ci-failure-occurrences:end -->")) as $end_parts |
          if ($end_parts | length) != 2 then
            error("ambiguous managed occurrence section")
          else
            {
              prefix: $start_parts[0],
              occurrences: (
                "<!-- ci-failure-occurrences:start -->" +
                $end_parts[0] +
                "<!-- ci-failure-occurrences:end -->"
              ),
              suffix: (if $end_parts[1] == "" then "\n" else $end_parts[1] end)
            }
          end
        end;
      def legacy_parts:
        (normalized | split("\n## Occurrences\n")) as $parts |
        if ($parts | length) != 2 then
          error("unsupported legacy occurrence section")
        else
          {prefix: $parts[0], occurrences: ("## Occurrences\n" + $parts[1]), suffix: ""}
        end;
      def parts:
        if (normalized | contains("<!-- ci-failure-occurrences:start -->")) or
           (normalized | contains("<!-- ci-failure-occurrences:end -->")) then
          managed_parts
        else
          legacy_parts
        end;
      def main_prefix:
        (. | sub("\n+$"; "") | split("\n")) as $lines |
        ([range(0; $lines | length) |
          select($lines[.] == "**Type**: main-repository-breakage")]) as $type_lines |
        if ($type_lines | length) != 1 then
          error("ambiguous main issue type")
        else
          ($type_lines[0]) as $type_line |
          {
            generated: ($lines[0:($type_line + 1)] | join("\n")),
            suffix: ($lines[($type_line + 1):] | join("\n") | sub("^\n+"; "") | sub("\n+$"; ""))
          }
        end;
      ($current | parts) as $current_parts |
      ($canonical | managed_parts) as $canonical_parts |
      if (($current_parts.prefix | normalized | split("\n") | .[0]) !=
          ($canonical_parts.prefix | normalized | split("\n") | .[0])) then
        error("issue identity marker does not match")
      else
        ($current_parts.prefix | normalized | main_prefix) as $current_prefix |
        ($canonical_parts.prefix | normalized | main_prefix) as $canonical_prefix |
        (
          $canonical_prefix.generated +
          (if ($current_prefix.suffix | length) > 0
           then "\n\n" + $current_prefix.suffix
           else ""
           end) +
          "\n\n" +
          $current_parts.occurrences +
          $current_parts.suffix
        ) as $output |
        if ($output | utf8bytelength) <= $max_bytes then
          $output
        else
          error("migrated issue body exceeds the publication budget")
        end
      end
    ' > "$output_temp"; then
    rm -f "$output_temp"
    return 2
  fi

  mv "$output_temp" "$output_file"
}

cache_cause_issues()
{
  local repo="$1"
  local open_issues_file="$2"
  local closed_issues_file="$3"
  local open_issues_temp
  local closed_issues_temp
  open_issues_temp=$(mktemp)
  closed_issues_temp=$(mktemp)
  rm -f "$open_issues_file" "$closed_issues_file"

  if ! gh api --method GET --paginate --slurp "repos/${repo}/issues" \
      -f state=open \
      -f labels=ci-failure-cause \
      -f per_page=100 |
      jq -c '[.[][] | select(has("pull_request") | not) | select((.number | type) == "number") | {number, body: (.body // "")}]' \
        > "$open_issues_temp"; then
    echo "::error::Failed to load open cause issues" >&2
    rm -f "$open_issues_temp" "$closed_issues_temp" "$open_issues_file" "$closed_issues_file"
    return 1
  fi
  if ! gh api --method GET --paginate --slurp "repos/${repo}/issues" \
      -f state=closed \
      -f labels=ci-failure-cause \
      -f per_page=100 |
      jq -c '[.[][] | select(has("pull_request") | not) | select((.number | type) == "number") | {number, body: (.body // "")}]' \
        > "$closed_issues_temp"; then
    echo "::error::Failed to load closed cause issues" >&2
    rm -f "$open_issues_temp" "$closed_issues_temp" "$open_issues_file" "$closed_issues_file"
    return 1
  fi

  mv "$open_issues_temp" "$open_issues_file"
  mv "$closed_issues_temp" "$closed_issues_file"
}

pr_actionable()
{
  local repo="$1"
  local pr_number="$2"
  local pr_json
  local actionable

  if ! pr_json=$(gh api "repos/${repo}/pulls/${pr_number}"); then
    echo "::warning::Unable to determine whether PR #${pr_number} is actionable" >&2
    return 1
  fi
  if ! actionable=$(jq -r '
      if
        (.state | type) == "string" and
        (.state == "open" or .state == "closed") and
        (.locked | type) == "boolean"
      then
        (.state == "open" and (.locked | not)) | tostring
      else
        error("state and locked must describe a pull request")
      end
    ' <<< "$pr_json"); then
    echo "::warning::Unable to determine whether PR #${pr_number} is actionable" >&2
    return 1
  fi
  if [ "$actionable" != "true" ] && [ "$actionable" != "false" ]; then
    echo "::warning::Unable to determine whether PR #${pr_number} is actionable" >&2
    return 1
  fi

  printf '%s\n' "$actionable"
}

find_analysis_comment()
{
  local repo="$1"
  local pr_number="$2"
  local comment_ids

  if ! comment_ids=$(gh api "repos/${repo}/issues/${pr_number}/comments" --paginate \
      --jq '.[] | select(.user.login == "github-actions[bot]" and ((.body // "") | startswith("<!-- analyze-ci-failure -->\n"))) | .id'); then
    echo "::warning::Failed to list existing analysis comments for PR #${pr_number}" >&2
    return 1
  fi

  head -n 1 <<< "$comment_ids"
}

trusted_pr_number()
{
  local run_scope
  local pr_number

  run_scope=$(jq -r '.run_scope' "$RUN_CONTEXT_FILE")
  if [ "$run_scope" != "pull-request" ]; then
    echo 0
    return
  fi

  pr_number=$(jq -r '.pr_numbers // ""' "$RUN_CONTEXT_FILE")
  if [[ "$pr_number" =~ ^[0-9]+$ ]]; then
    echo "$pr_number"
  else
    echo 0
  fi
}

case "$COMMAND" in
  sanitize-cause)
    INPUT_FILE="${2:?input file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    sanitize_document cause "$INPUT_FILE" "$OUTPUT_FILE"
    ;;
  sanitize-analysis)
    INPUT_FILE="${2:?input file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    sanitize_document analysis "$INPUT_FILE" "$OUTPUT_FILE"
    ;;
  sanitize-json-field)
    INPUT_FILE="${2:?input file is required}"
    FIELD="${3:?field is required}"
    MAX_LENGTH="${4:?maximum length is required}"
    sanitize_json_field "$INPUT_FILE" "$FIELD" "$MAX_LENGTH"
    ;;
  render-untrusted-json)
    INPUT_FILE="${2:?input file is required}"
    MAX_LENGTH="${3:-500}"
    STRING_FORMAT="${4:-single-line}"
    render_untrusted_json "$INPUT_FILE" "$MAX_LENGTH" "$STRING_FORMAT"
    ;;
  render-untrusted-text)
    INPUT_FILE="${2:?input file is required}"
    MAX_LENGTH="${3:-65536}"
    render_untrusted_text "$INPUT_FILE" "$MAX_LENGTH"
    ;;
  sanitize-untrusted-text)
    INPUT_FILE="${2:?input file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    MAX_LENGTH="${4:-10485760}"
    sanitize_untrusted_text "$INPUT_FILE" "$OUTPUT_FILE" "$MAX_LENGTH"
    ;;
  select-test-result-artifacts)
    ARTIFACTS_FILE="${2:?artifacts file is required}"
    STARTED_AT="${3:?start time is required}"
    UPDATED_AT="${4:?update time is required}"
    FAILED_JOBS_FILE="${5:?failed jobs file is required}"
    select_test_result_artifacts \
      "$ARTIFACTS_FILE" "$STARTED_AT" "$UPDATED_AT" "$FAILED_JOBS_FILE" \
      "${6:-20}" "${7:-1073741824}" "${8:-104857600}" "${9:-1}"
    ;;
  extract-test-results-artifact)
    ARTIFACT_FILE="${2:?artifact file is required}"
    OUTPUT_DIRECTORY="${3:?output directory is required}"
    extract_test_results_artifact \
      "$ARTIFACT_FILE" "$OUTPUT_DIRECTORY" \
      "${4:-10000}" "${5:-1073741824}" "${6:-104857600}" "${7:-}" "${8:-trx}"
    ;;
  backfill-occurrence-publication)
    CAUSE_FILE="${2:?cause file is required}"
    TRUSTED_FAILED_JOBS_FILE="${3:?trusted failed jobs file is required}"
    RUN_ID="${4:?run ID is required}"
    RUN_ATTEMPT="${5:?run attempt is required}"
    RUN_SCOPE="${6:?run scope is required}"
    OUTPUT_FILE="${7:?output file is required}"
    backfill_occurrence_publication \
      "$CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" "$RUN_ID" "$RUN_ATTEMPT" \
      "$RUN_SCOPE" "$OUTPUT_FILE"
    ;;
  stored-occurrence-rows)
    CAUSE_FILE="${2:?cause file is required}"
    stored_occurrence_rows "$CAUSE_FILE" "${3:-}"
    ;;
  select-occurrence-row)
    OCCURRENCE_ROWS_FILE="${2:?occurrence rows file is required}"
    RUN_ID="${3:?run ID is required}"
    OCCURRENCE_URL="${4:?occurrence URL is required}"

    # In "| date | [123](url) | jobs | context |", the second field is the
    # publisher-owned Build column. Labels may legitimately repeat the URL.
    jq -er \
      --arg build_column "[${RUN_ID}](${OCCURRENCE_URL})" '
        [.[] | select((split(" | ")[1] // "") == $build_column)] |
        if length == 1 then .[0]
        else error("current occurrence row is missing or ambiguous")
        end
      ' "$OCCURRENCE_ROWS_FILE"
    ;;
  render-issue-occurrences)
    CURRENT_BODY_FILE="${2:?current issue body file is required}"
    NEW_OCCURRENCE_ROW="${3:?new occurrence row is required}"
    TOTAL_OCCURRENCE_COUNT="${4:?total occurrence count is required}"
    OUTPUT_FILE="${5:?output file is required}"
    OCCURRENCE_ROWS_FILE="${6:?occurrence rows file is required}"
    MAX_BYTES="${7:-65000}"
    render_issue_occurrences \
      "$CURRENT_BODY_FILE" "$NEW_OCCURRENCE_ROW" "$TOTAL_OCCURRENCE_COUNT" \
      "$OUTPUT_FILE" "$OCCURRENCE_ROWS_FILE" "$MAX_BYTES"
    ;;
  migrate-main-issue-body)
    CURRENT_BODY_FILE="${2:?current issue body file is required}"
    CANONICAL_BODY_FILE="${3:?canonical issue body file is required}"
    OUTPUT_FILE="${4:?output file is required}"
    MAX_BYTES="${5:-65000}"
    migrate_main_issue_body "$CURRENT_BODY_FILE" "$CANONICAL_BODY_FILE" "$OUTPUT_FILE" "$MAX_BYTES"
    ;;
  cache-cause-issues)
    REPO="${2:?repository is required}"
    OPEN_ISSUES_FILE="${3:?open issues file is required}"
    CLOSED_ISSUES_FILE="${4:?closed issues file is required}"
    cache_cause_issues "$REPO" "$OPEN_ISSUES_FILE" "$CLOSED_ISSUES_FILE"
    ;;
  pr-actionable)
    REPO="${2:?repository is required}"
    PR_NUMBER="${3:?pull request number is required}"
    pr_actionable "$REPO" "$PR_NUMBER"
    ;;
  find-analysis-comment)
    REPO="${2:?repository is required}"
    PR_NUMBER="${3:?pull request number is required}"
    find_analysis_comment "$REPO" "$PR_NUMBER"
    ;;
  pr-number)
    trusted_pr_number
    ;;
  sanitize-trusted-failed-jobs)
    INPUT_FILE="${2:?input file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    sanitize_trusted_failed_jobs "$INPUT_FILE" "$OUTPUT_FILE"
    ;;
  sanitize-trusted-test-failures)
    INPUT_FILE="${2:?input file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    sanitize_trusted_test_failures "$INPUT_FILE" "$OUTPUT_FILE"
    ;;
  collect-test-failures)
    TEST_RESULTS_DIRECTORY="${2:?test results directory is required}"
    JOB_NAME="${3:?job name is required}"
    FAILED_JOBS_FILE="${4:?failed jobs file is required}"
    OUTPUT_FILE="${5:?output file is required}"
    collect_test_failures \
      "$TEST_RESULTS_DIRECTORY" "$JOB_NAME" "$FAILED_JOBS_FILE" "$OUTPUT_FILE" "${6:-trx}"
    ;;
  cause-job-names)
    CAUSE_FILE="${2:?cause file is required}"
    TRUSTED_FAILED_JOBS_FILE="${3:?trusted failed jobs file is required}"
    FORMAT="${4:?format is required}"

    # Derive every human-facing job/test label from the same trusted
    # associations so issue headings and occurrence rows cannot disagree.
    jq -er \
      --arg format "$FORMAT" \
      --slurpfile trusted_jobs "$TRUSTED_FAILED_JOBS_FILE" "$JQ_SANITIZE_DEFS"'
        def render_code_span:
          (([scan("`+") | length] | max // 0) + 1) as $delimiter_length |
          ("`" * $delimiter_length) + " " + . + " " + ("`" * $delimiter_length);
        . as $cause |
        # Modern causes preserve exact test/job observations. The scalar
        # fallback exists only to render older records and does not promote a
        # historical display label into cause identity.
        ((.tests | type) == "array") as $has_explicit_tests |
        (if $has_explicit_tests then .tests
         elif .type == "flaky-test" and
              (.test_name | type) == "string" and
              (.job_ids | type) == "array" then
           [.job_ids[] | {name: $cause.test_name, job_id: .}]
         else [] end) as $tests |
        ((.job_names | type) == "array") as $has_explicit_job_names |
        (if (.job_ids | type) == "array" then .job_ids else [] end) as $job_ids |
        (if $has_explicit_job_names then
          .job_names
        else
          [
            $job_ids[] as $job_id |
            [$trusted_jobs[0][] | select(.id == $job_id) | .name][0]
          ]
        end) as $job_names |
        # A job-only legacy occurrence has no numeric association to promote,
        # but its scalar test label remains useful descriptive history.
        ($has_explicit_job_names and
         ($job_names | length) == 1 and
         ($has_explicit_tests | not) and
         .type == "flaky-test" and
         (.test_name | type) == "string" and
         (.test_name | length) > 0) as $has_legacy_scalar_job_name |
        if any($job_names[]; type != "string" or length == 0) then
          error("cause references an unknown trusted failed job")
        else
          # Apply the job display budget first, then show only tests belonging
          # to visible jobs. Hidden jobs must not consume the test budget or
          # leave the heading and occurrence row showing different evidence.
          $job_names
          | map(sanitize_single_line | .[0:120]) as $all_job_names
          | ($job_ids[0:20]) as $visible_job_ids
          | ($all_job_names[0:20]) as $visible_job_names
          | (($all_job_names | length) - ($visible_job_names | length)) as $remaining_job_count
          | ([$tests[] | . as $test |
              select(($visible_job_ids | index($test.job_id)) != null)][0:20]) as $visible_tests
          | if $format == "plain" then
              ($visible_job_names | join(", ")) +
              (if $remaining_job_count > 0 then
                ", \($remaining_job_count) more jobs"
              else "" end)
            elif $format == "display" then
              ([$visible_job_names[] | render_code_span] +
                (if $remaining_job_count > 0 then
                  ["` \($remaining_job_count) more jobs `"]
                else [] end)) |
              join("<br>")
            elif $format == "table" then
              # A literal pipe inside a job or test name would otherwise start a
              # new Markdown table column. For example, "A|B" renders as "A\|B".
              ($visible_job_names | to_entries | map(
                  . as $entry |
                  ($entry.value | gsub("\\|"; "\\|") | render_code_span) +
                  ((if $has_explicit_job_names and
                       ($visible_job_names | length) == 1 then
                      $visible_tests
                    else
                      [$visible_tests[] |
                        select(.job_id == $visible_job_ids[$entry.key])]
                    end) |
                    map(.name | sanitize_single_line | .[0:120] |
                      gsub("\\|"; "\\|") | render_code_span) |
                    if length == 0 then "" else "<br>" + join("<br>") end) +
                  (if $has_legacy_scalar_job_name and $entry.key == 0 then
                    "<br>" + ($cause.test_name | sanitize_single_line | .[0:120] |
                      gsub("\\|"; "\\|") | render_code_span)
                  else
                    ""
                  end)
                ) +
                  (if $remaining_job_count > 0 then
                    ["` \($remaining_job_count) more jobs `"]
                  else [] end) |
                join("<br>")) +
                (if ($tests | length) > ($visible_tests | length) then
                  "<br>` \(($tests | length) - ($visible_tests | length)) more tests in the linked run `"
                else "" end)
            elif $format == "tests-display" then
              if $has_explicit_tests then
                ([$visible_tests[] |
                    .name | sanitize_single_line | .[0:120] | render_code_span] +
                  (if ($tests | length) > ($visible_tests | length) then
                    ["` \(($tests | length) - ($visible_tests | length)) more tests in the linked run `"]
                  else [] end)) |
                join("<br>")
              else
                ""
              end
            else
              error("unsupported cause job name format")
            end
        end
      ' "$CAUSE_FILE"
    ;;
  # Produce the issue-facing view of one occurrence. First publication uses the
  # validated current evidence; replay uses the immutable stored occurrence so
  # a partial issue failure cannot rewrite history. The workflow consumes the
  # returned display labels, date, URL, context, and refresh state when building
  # the managed occurrence row and issue heading.
  publication-occurrence)
    CAUSE_FILE="${2:?cause file is required}"
    STORED_CAUSE_FILE="${3:?stored cause file is required}"
    TRUSTED_FAILED_JOBS_FILE="${4:?trusted failed jobs file is required}"
    RUN_ID="${5:?run ID is required}"
    RUN_ATTEMPT="${6:?run attempt is required}"
    RUN_URL="${7:?run URL is required}"
    ANALYZED_AT="${8:?analysis timestamp is required}"
    RUN_SCOPE="${9:?run scope is required}"
    PR_NUMBER="${10:?PR number is required}"

    SOURCE_CAUSE_FILE="$CAUSE_FILE"
    TEMP_CAUSE_FILE=""
    STORED_OCCURRENCE_FILE=""
    REFRESH_REQUIRED="false"
    OCCURRENCE_URL="${RUN_URL}/attempts/${RUN_ATTEMPT}"
    OCCURRENCE_DATE="${ANALYZED_AT%%T*}"
    if [ "$RUN_SCOPE" = "main" ]; then
      OCCURRENCE_CONTEXT="main"
    elif [ "$PR_NUMBER" = "0" ]; then
      OCCURRENCE_CONTEXT="unavailable"
    else
      OCCURRENCE_CONTEXT="#${PR_NUMBER}"
    fi

    # Memory is pushed before issue side effects. A replay must therefore render
    # from the immutable stored occurrence instead of changed analyzer output.
    # Historical occurrences can lack numeric job IDs, so stored tests provide
    # the flaky fallback while older non-flaky records keep current job rendering.
    if [ -f "$STORED_CAUSE_FILE" ]; then
      STORED_OCCURRENCE_FILE=$(mktemp)
      jq \
        --argjson run_id "$RUN_ID" \
        --argjson run_attempt "$RUN_ATTEMPT" '
          first(
            .occurrences[]? |
            select(
              .run_id == $run_id and
              (if has("run_attempt") then .run_attempt else 1 end) == $run_attempt
            )
          ) // empty
        ' "$STORED_CAUSE_FILE" > "$STORED_OCCURRENCE_FILE"
      # This branch is replay recovery: preserve the published date, link,
      # context, jobs, and tests from the matching stored attempt. It adapts
      # both current structured evidence and legacy job labels to the shared
      # renderer without letting a later analyzer run revise occurrence history.
      if [ -s "$STORED_OCCURRENCE_FILE" ]; then
        REFRESH_REQUIRED=$(jq -r '.issue_row_needs_refresh == true' "$STORED_OCCURRENCE_FILE")
        OCCURRENCE_DATE=$(jq -er '(.observed_at // .occurred_at) | split("T")[0]' "$STORED_OCCURRENCE_FILE")
        STORED_RUN_URL=$(jq -er '.run_url' "$STORED_OCCURRENCE_FILE")
        STORED_RUN_BASE_URL="https://github.com/${GITHUB_REPOSITORY:-microsoft/aspire}/actions/runs/${RUN_ID}"
        STORED_RUN_ATTEMPT_URL="${STORED_RUN_BASE_URL}/attempts/${RUN_ATTEMPT}"
        if [ "$STORED_RUN_URL" = "$STORED_RUN_ATTEMPT_URL" ] ||
           { [ "$RUN_ATTEMPT" = "1" ] && [ "$STORED_RUN_URL" = "$STORED_RUN_BASE_URL" ]; }; then
          OCCURRENCE_URL="$STORED_RUN_ATTEMPT_URL"
        else
          echo "::error::Stored occurrence URL does not identify the selected run attempt" >&2
          rm -f "$STORED_OCCURRENCE_FILE"
          return 1
        fi
        STORED_PR_NUMBER=$(jq -r '
          if ((.pr_number | type) == "number") and
             (.pr_number > 0) and
             (.pr_number == (.pr_number | floor)) then
            .pr_number | floor
          else
            0
          end
        ' "$STORED_OCCURRENCE_FILE")
        if [ "$RUN_SCOPE" = "main" ]; then
          OCCURRENCE_CONTEXT="main"
        elif [ "$STORED_PR_NUMBER" -gt 0 ]; then
          OCCURRENCE_CONTEXT="#${STORED_PR_NUMBER}"
        else
          OCCURRENCE_CONTEXT="unavailable"
        fi
        if jq -e '
            ((.job_ids | type) == "array") or
            ((.tests | type) == "array")
          ' "$STORED_OCCURRENCE_FILE" >/dev/null; then
          # Reconstruct the minimum cause-shaped input needed by the shared
          # renderer. This keeps all publication surfaces on one formatting
          # path without copying mutable cause-level metadata into the replay.
          TEMP_CAUSE_FILE=$(mktemp)
          jq -n \
            --arg id "$(jq -r '.id' "$STORED_CAUSE_FILE")" \
            --arg type "$(jq -r '.type' "$STORED_CAUSE_FILE")" \
            --slurpfile occurrences "$STORED_OCCURRENCE_FILE" '
              ($occurrences[0]) as $occurrence |
              {
                id: $id,
                type: $type,
                job_ids: (
                  if ($occurrence.job_ids | type) == "array" then
                    $occurrence.job_ids
                  else
                    reduce $occurrence.tests[] as $test
                      ([]; if index($test.job_id) == null then
                        . + [$test.job_id]
                      else
                        .
                      end)
                  end
                )
              } +
              (if ($occurrence.tests | type) == "array" then
                {tests: $occurrence.tests}
              else
                {}
              end) +
              # Legacy enrichment keeps a historical grouped label separate
              # from the authenticated job/test evidence. Persisting this
              # choice lets later issue recreation reproduce the same display.
              (if (($occurrence.issue_uses_stored_job_label == true) or
                   ($occurrence.issue_row_needs_refresh == true)) and
                  (($occurrence.job | type) == "string") and
                  (($occurrence.job | length) > 0) then
                {job_names: [$occurrence.job]}
              else
                {}
              end)
            ' > "$TEMP_CAUSE_FILE"
          SOURCE_CAUSE_FILE="$TEMP_CAUSE_FILE"
        elif jq -e '(.job | type) == "string" and (.job | length) > 0' \
            "$STORED_OCCURRENCE_FILE" >/dev/null; then
          TEMP_CAUSE_FILE=$(mktemp)
          jq -n \
            --arg id "$(jq -r '.id' "$STORED_CAUSE_FILE")" \
            --arg type "$(jq -r '.type' "$STORED_CAUSE_FILE")" \
            --arg job "$(jq -r '.job' "$STORED_OCCURRENCE_FILE")" '
              {
                id: $id,
                type: $type,
                # Legacy job labels can contain comma-joined display names.
                # Keep the stored label intact rather than inventing job IDs.
                job_names: [$job]
              }
            ' > "$TEMP_CAUSE_FILE"
          SOURCE_CAUSE_FILE="$TEMP_CAUSE_FILE"
        fi
      fi
    fi

    JOBS_DISPLAY=$(bash "$0" cause-job-names \
      "$SOURCE_CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" display)
    JOBS_TABLE=$(bash "$0" cause-job-names \
      "$SOURCE_CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" table)
    TESTS_DISPLAY=$(bash "$0" cause-job-names \
      "$SOURCE_CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" tests-display)
    rm -f "${TEMP_CAUSE_FILE:-}" "${STORED_OCCURRENCE_FILE:-}"
    jq -n \
      --arg jobs_display "$JOBS_DISPLAY" \
      --arg jobs_table "$JOBS_TABLE" \
      --arg tests_display "$TESTS_DISPLAY" \
      --arg occurrence_date "$OCCURRENCE_DATE" \
      --arg occurrence_url "$OCCURRENCE_URL" \
      --arg occurrence_context "$OCCURRENCE_CONTEXT" \
      --argjson refresh_required "$REFRESH_REQUIRED" '
        {
          jobs_display: $jobs_display,
          jobs_table: $jobs_table,
          tests_display: $tests_display,
          occurrence_date: $occurrence_date,
          occurrence_url: $occurrence_url,
          occurrence_context: $occurrence_context,
          refresh_required: $refresh_required
        }
      '
    ;;
  # Remove issue_row_needs_refresh from the matching stored occurrence after
  # its managed issue row has been updated. If publication fails, leaving the
  # marker tells replay to retry; markers for other run attempts remain.
  clear-occurrence-refresh)
    CAUSE_FILE="${2:?cause file is required}"
    RUN_ID="${3:?run ID is required}"
    RUN_ATTEMPT="${4:?run attempt is required}"
    OUTPUT_FILE="${5:?output file is required}"
    jq \
      --argjson run_id "$RUN_ID" \
      --argjson run_attempt "$RUN_ATTEMPT" '
        .occurrences |= map(
          if .run_id == $run_id and
             (if has("run_attempt") then .run_attempt else 1 end) == $run_attempt then
            del(.issue_row_needs_refresh)
          else
            .
          end
        )
      ' "$CAUSE_FILE" > "$OUTPUT_FILE"
    ;;
  add-occurrence)
    CAUSE_FILE="${2:?cause file is required}"
    RUN_ID="${3:?run ID is required}"
    RUN_URL="${4:?run URL is required}"
    JOB_NAMES="${5:?job names are required}"
    ANALYZED_AT="${6:?analysis timestamp is required}"
    RUN_SCOPE="${7:?run scope is required}"
    PR_NUMBER=$(trusted_pr_number)
    RUN_ATTEMPT=$(jq -er '
      (if has("run_attempt") then .run_attempt else 1 end) |
      select(type == "number" and . > 0 and . == floor)
    ' "$RUN_CONTEXT_FILE")

    # Store current job/test observations on the occurrence, not on the stable
    # cause identity. The run attempt keeps retries of the same run distinct.
    jq \
      --argjson run_id "$RUN_ID" \
      --argjson run_attempt "$RUN_ATTEMPT" \
      --arg run_url "$RUN_URL" \
      --arg job "$JOB_NAMES" \
      --argjson pr_number "$PR_NUMBER" \
      --arg observed_at "$ANALYZED_AT" \
      --arg run_scope "$RUN_SCOPE" \
      '. as $cause |
       (if (.job_ids | type) == "array" then
          {job_ids: .job_ids}
        else {} end) as $job_details |
       (if .type == "flaky-test" then
          {tests: (if (.tests | type) == "array" then .tests
                   elif (.test_name | type) == "string" and (.job_ids | type) == "array" then
                     [.job_ids[] | {name: $cause.test_name, job_id: .}]
                   else [] end)}
        else {} end) as $test_details |
       . + {occurrences: ([
         {run_id: $run_id, run_attempt: $run_attempt,
           run_url: ($run_url + "/attempts/" + ($run_attempt | tostring)),
           job: $job, pr_number: $pr_number, observed_at: $observed_at,
           run_scope: $run_scope} + $job_details + $test_details
       ])}' \
      "$CAUSE_FILE"
    ;;
  merge-cause)
    NEW_CAUSE_FILE="${2:?new cause file is required}"
    EXISTING_CAUSE_FILE="${3:?existing cause file is required}"
    OUTPUT_FILE="${4:?output file is required}"

    # Occurrences are immutable once they have an explicit run attempt. Only a
    # legacy run-only record may be enriched with newly authenticated attempt-1
    # evidence, while preserving its original observation time and descriptive
    # fields so historical replay remains faithful.
    jq -s '
      def occurrence_attempt:
        if has("run_attempt") then
          if ((.run_attempt | type) == "number") and
             (.run_attempt > 0) and
             (.run_attempt == (.run_attempt | floor)) then
            .run_attempt
          else
            error("stored occurrence has invalid run attempt")
          end
        else
          1
        end;
      .[0] as $new | .[1] as $existing |
      (($existing.occurrences // []) | map(
        . as $occurrence |
        ($occurrence | occurrence_attempt) as $validated_attempt |
        .
      )) as $existing_occurrences |
      (($new.occurrences // []) | map(
        . as $occurrence |
        ($occurrence | occurrence_attempt) as $validated_attempt |
        .
      )) as $incoming_occurrences |
      ($existing | del(.job_ids, .job_names, .tests)) * {
        occurrences: (
          reduce ($incoming_occurrences[]) as $incoming
            ($existing_occurrences;
              ([.[] |
                (.run_id == $incoming.run_id) and
                ((. | occurrence_attempt) == ($incoming | occurrence_attempt))
              ] | index(true)) as $index |
              if $index == null then
                . + [$incoming]
              elif ((.[$index].run_attempt | type) == "number") and
                   (.[$index].run_attempt > 0) and
                   (.[$index].run_attempt == (.[$index].run_attempt | floor)) then
                .
              else
                .[$index] = (
                  .[$index] as $stored |
                  ($stored * $incoming) |
                  .observed_at = ($stored.observed_at // $stored.occurred_at // $incoming.observed_at) |
                  .pr_number = (
                    if (($stored.pr_number | type) == "number") and
                       ($stored.pr_number >= 0) and
                       ($stored.pr_number == ($stored.pr_number | floor)) then
                      $stored.pr_number
                    else
                      .pr_number
                    end
                  ) |
                  .run_scope = (
                    if $stored.run_scope == "main" or
                       $stored.run_scope == "pull-request" then
                      $stored.run_scope
                    else
                      .run_scope
                    end
                  ) |
                  if (($stored.job | type) == "string") and
                     (($stored.job | length) > 0) then
                    .job = $stored.job |
                    if $existing.type != "flaky-test" then
                      del(.job_ids)
                    else
                      .
                    end
                  else
                    .
                  end |
                  if (($incoming.tests | type) == "array") then
                    .issue_row_needs_refresh = true
                  else
                    .
                  end
                )
              end)
          | sort_by(.observed_at // .occurred_at)
        )
      }
    ' "$NEW_CAUSE_FILE" "$EXISTING_CAUSE_FILE" > "$OUTPUT_FILE"
    ;;
  render-prior-cause)
    CAUSE_FILE="${2:?cause file is required}"

    # Recent verified tests help the analyzer recognize a recurring mechanism,
    # but remain bounded occurrence evidence rather than stable cause identity.
    sanitize_document cause "$CAUSE_FILE" /dev/stdout | jq -c "$JQ_SANITIZE_DEFS"'
    {
      id,
      type,
      title: ((.title // .id // "") | .[0:238]),
      test_name: (if .test_name then .test_name[0:500] else null end),
      recent_tests: [(.occurrences // [] | sort_by(.observed_at // .occurred_at) | last | .tests // [])[0:20][] |
        {name: (.name | sanitize_single_line | .[0:500]), job_id}],
      issue_url: (.issue_url // null),
      error_pattern: ((.error_pattern // "") | .[0:500]),
      occurrence_count: ((.occurrences // []) | length),
      last_seen: ((.occurrences // [] |
        sort_by(.observed_at // .occurred_at) |
        last |
        (.observed_at // .occurred_at)) // null)
    }' | sed 's/^/    /'
    ;;
  write-run-summary)
    ANALYSIS_FILE="${2:?analysis file is required}"
    OUTPUT_FILE="${3:?output file is required}"
    ANALYZED_AT="${4:?analysis timestamp is required}"
    PR_METADATA_FILE="$CI_FAILURE_DATA_DIR/pr-metadata.json"
    TRIGGERING_MERGE_FILE="$CI_FAILURE_DATA_DIR/triggering-merge-pr.json"
    LAST_SUCCESSFUL_RUN_FILE="$CI_FAILURE_DATA_DIR/last-successful-main-run.json"
    CANDIDATE_MERGES_FILE="$CI_FAILURE_DATA_DIR/candidate-merges.json"
    CANDIDATE_HISTORY_STATUS_FILE="$CI_FAILURE_DATA_DIR/candidate-merge-history-status.json"
    SANITIZED_TRUSTED_FAILED_JOBS_FILE=$(mktemp)
    trap 'rm -f "$SANITIZED_TRUSTED_FAILED_JOBS_FILE"' EXIT
    sanitize_trusted_failed_jobs \
      "$CI_FAILURE_DATA_DIR/failed-jobs.json" \
      "$SANITIZED_TRUSTED_FAILED_JOBS_FILE"

    [ -f "$PR_METADATA_FILE" ] || PR_METADATA_FILE=/dev/null
    [ -f "$TRIGGERING_MERGE_FILE" ] || TRIGGERING_MERGE_FILE=/dev/null
    [ -f "$LAST_SUCCESSFUL_RUN_FILE" ] || LAST_SUCCESSFUL_RUN_FILE=/dev/null
    [ -f "$CANDIDATE_MERGES_FILE" ] || CANDIDATE_MERGES_FILE=/dev/null
    [ -f "$CANDIDATE_HISTORY_STATUS_FILE" ] || CANDIDATE_HISTORY_STATUS_FILE=/dev/null

    jq -n \
      --arg analyzed_at "$ANALYZED_AT" \
      --slurpfile analysis "$ANALYSIS_FILE" \
      --slurpfile run_context "$RUN_CONTEXT_FILE" \
      --slurpfile run "$CI_FAILURE_DATA_DIR/run.json" \
      --slurpfile trusted_jobs "$SANITIZED_TRUSTED_FAILED_JOBS_FILE" \
      --slurpfile pr_metadata "$PR_METADATA_FILE" \
      --slurpfile triggering_merge "$TRIGGERING_MERGE_FILE" \
      --slurpfile last_successful_run "$LAST_SUCCESSFUL_RUN_FILE" \
      --slurpfile candidate_merges "$CANDIDATE_MERGES_FILE" \
      --slurpfile candidate_history_status "$CANDIDATE_HISTORY_STATUS_FILE" \
      "$JQ_SANITIZE_DEFS"'
        def persisted_text($max_length):
          if type == "string" then
            redact_display_metadata |
            gsub("[\r\n\t]+"; " ") |
            strip_unsafe |
            .[0:$max_length]
          else
            ""
          end;

        ($analysis[0]) as $analysis |
        ($run_context[0]) as $context |
        ($run[0]) as $run |
        ($trusted_jobs[0]) as $trusted_jobs |
        ($pr_metadata[0] // {}) as $pr |
        ($triggering_merge[0] // {}) as $triggering |
        ($last_successful_run[0] // {}) as $last_success |
        ($candidate_merges[0] // []) as $candidates |
        (($candidate_history_status[0].state // "unavailable")) as $candidate_history_state |
        ($analysis.failed_jobs | map({key: (.id | tostring), value: .}) | from_entries) as $analysis_jobs |
        ($trusted_jobs | map(.name) | map(select(type == "string" and length > 0)) | unique) as $trusted_job_names |
        {
          run_id: $context.run_id,
          run_attempt: $context.run_attempt,
          run_url: (($run.html_url // "") | persisted_text(1000)),
          run_scope: $context.run_scope,
          analyzed_at: $analyzed_at,
          verdict: $analysis.verdict,
          pr: (
            if $context.run_scope == "pull-request" and ($pr.number | type) == "number" then
              {
                number: $pr.number,
                title: (($pr.title // "") | persisted_text(500)),
                author: (($pr.user // "") | persisted_text(100)),
                state: (($pr.state // "") | persisted_text(50)),
                head_branch: (($pr.head_branch // "") | persisted_text(500)),
                base_branch: (($pr.base_branch // "") | persisted_text(500)),
                url: (($pr.html_url // "") | persisted_text(1000))
              }
            else
              null
            end
          ),
          triggering_merge_pr: (
            if $context.run_scope == "main" and $candidate_history_state == "available" and ($triggering.number | type) == "number" then
              {
                number: $triggering.number,
                title: (($triggering.title // "") | persisted_text(500)),
                author: (($triggering.user.login // "") | persisted_text(100)),
                state: (($triggering.state // "") | persisted_text(50)),
                head_branch: (($triggering.head.ref // "") | persisted_text(500)),
                base_branch: (($triggering.base.ref // "") | persisted_text(500)),
                url: (($triggering.html_url // "") | persisted_text(1000)),
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
                candidate_merge_history_state: $candidate_history_state,
                candidate_merges: (
                  if $candidate_history_state == "available" then
                    [
                      $candidates[]? |
                      {
                        sha: .sha,
                        message: ((.message // "") | persisted_text(500)),
                        html_url: ((.html_url // "") | persisted_text(1000)),
                        pull_request: {
                          number: .pull_request.number,
                          title: ((.pull_request.title // "") | persisted_text(500)),
                          url: ((.pull_request.url // "") | persisted_text(1000)),
                          merged_at: .pull_request.merged_at
                        }
                      }
                    ]
                  else
                    null
                  end
                )
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
              url: (($job.html_url // "") | persisted_text(1000)),
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
                (.name | persisted_text(500))
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
              standard_output: (.standard_output // ""),
              standard_error: (.standard_error // ""),
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
