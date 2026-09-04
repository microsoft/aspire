#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

COMMAND="${1:?command is required}"
CI_FAILURE_DATA_DIR="${CI_FAILURE_DATA_DIR:-ci-failure-data}"
RUN_CONTEXT_FILE="$CI_FAILURE_DATA_DIR/run-context.json"

JQ_SANITIZE_DEFS='
  def strip_unsafe:
    gsub("\u001b\\[[0-9;?]*[ -/]*[@-~]"; "") |
    gsub("\\p{Cf}|\\p{Zl}|\\p{Zp}|[\uFE00-\uFE0F]"; "") |
    gsub("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]"; "") |
    [explode[] | select((. < 917760 or . > 917999))] |
    implode;
  def sanitize_single_line:
    gsub("[\r\n\t]+"; " ") |
    strip_unsafe;
  def sanitize_multiline:
    gsub("\r\n?"; "\n") |
    strip_unsafe;
'

sanitize_document()
{
  local document_type="$1"
  local input_file="$2"
  local output_file="$3"

  # CI errors can contain CRLF, ANSI escapes, and invisible Unicode formatting.
  # Preserve diagnostic text while removing controls that can alter later prompt
  # or Markdown rendering.
  jq --arg document_type "$document_type" "$JQ_SANITIZE_DEFS"'
    if $document_type == "cause" then
      if (.title | type) == "string" then .title |= sanitize_single_line else . end |
      if (.test_name | type) == "string" then .test_name |= sanitize_single_line else . end |
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
           ((.stack_trace == null) or (.stack_trace | type) == "string") then
          {
            test: (.test | sanitize_single_line | .[0:500]),
            job: (.job | sanitize_single_line | .[0:500]),
            error: (.error | sanitize_multiline | .[0:1000]),
            stack_trace: ((.stack_trace // "") | sanitize_multiline | .[0:2000])
          }
        else
          error("trusted test failure has an invalid shape")
        end) |
      unique_by([.test, .job, .error, .stack_trace])
    end
  ' "$input_file" > "$output_file"
}

collect_test_failures()
{
  local test_results_directory="$1"
  local job_name="$2"
  local failed_jobs_file="$3"
  local output_file="$4"
  local json_lines

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

  json_lines=$(mktemp)
  while IFS= read -r -d '' extracted_path; do
    local parsed_lines
    parsed_lines=$(mktemp)
    if ! yq -p xml -o json '.' "$extracted_path" 2>/dev/null | jq -cr --arg job "$job_name" '
      # TRX represents one result as an object and multiple results as an array:
      #   <UnitTestResult testName="Tests.Failed" outcome="Failed">...</UnitTestResult>
      (.TestRun.Results.UnitTestResult // []) |
      (if type == "array" then . else [.] end) |
      map(select(.["+@outcome"] == "Failed")) |
      .[] |
      {
        test: (.["+@testName"] // ""),
        job: $job,
        error: ((.Output.ErrorInfo.Message // "") | if type == "object" then (.["+content"] // "") else tostring end | .[0:1000]),
        stack_trace: ((.Output.ErrorInfo.StackTrace // "") | if type == "object" then (.["+content"] // "") else tostring end | .[0:2000])
      }
    ' > "$parsed_lines"; then
      echo "::warning::Unable to parse extracted test result $(basename "$extracted_path")" >&2
    else
      cat "$parsed_lines" >> "$json_lines"
    fi
    rm -f "$parsed_lines"
  done < <(find "$test_results_directory" -maxdepth 1 -type f -name "*.trx" -print0)

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

select_test_results_artifact()
{
  local artifacts_file="$1"
  local started_at="$2"
  local updated_at="$3"
  local max_archive_bytes="${4:-104857600}"
  local selected_artifact
  local artifact_id
  local artifact_size

  selected_artifact=$(jq -r \
    --arg started_at "$started_at" \
    --arg updated_at "$updated_at" '
    [
      .[] |
      select(
        (.expired == false) and
        (.name == "All-TestResults") and
        ((.created_at | type) == "string") and
        (.created_at > $started_at and .created_at <= $updated_at))
    ] |
    sort_by([.created_at, .id]) |
    last |
    if . == null then "" else [(.id // ""), (.size_in_bytes // "")] | @tsv end
  ' "$artifacts_file")

  if [ -z "$selected_artifact" ]; then
    return 0
  fi

  IFS=$'\t' read -r artifact_id artifact_size <<< "$selected_artifact"
  if [[ ! "$artifact_id" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$artifact_size" =~ ^[0-9]+$ ]] ||
     [[ ! "$max_archive_bytes" =~ ^[1-9][0-9]*$ ]]; then
    echo "::warning::Newest test results artifact has invalid size metadata" >&2
    return 0
  fi
  if [ "$artifact_size" -gt "$max_archive_bytes" ]; then
    echo "::warning::Newest test results artifact exceeds the ${max_archive_bytes}-byte download budget" >&2
    return 0
  fi

  echo "$artifact_id"
}

# run-tests.yml names test jobs and their artifacts as:
#   Tests / No-package tests / Infrastructure (8-core-ubuntu-latest)
#   logs-Infrastructure-8-core-ubuntu-latest
select_test_result_artifacts()
{
  local artifacts_file="$1"
  local started_at="$2"
  local updated_at="$3"
  local failed_jobs_file="$4"
  local max_artifacts="${5:-20}"
  local max_total_bytes="${6:-1073741824}"
  local max_artifact_bytes="${7:-104857600}"

  jq -cer \
    --arg started_at "$started_at" \
    --arg updated_at "$updated_at" \
    --argjson max_artifacts "$max_artifacts" \
    --argjson max_total_bytes "$max_total_bytes" \
    --argjson max_artifact_bytes "$max_artifact_bytes" \
    --slurpfile artifacts "$artifacts_file" '
      def artifact_name:
        .name |
        capture("(^| / )(?<short>[^/]+) \\((?<runner>[^()]*)\\)$") |
        "logs-\(.short)-\(.runner)";

      [
        .[] |
        select(type == "object" and (.name | type) == "string") |
        . as $job |
        (try ($job | artifact_name) catch null) as $artifact_name |
        select($artifact_name != null) |
        [
          $artifacts[0][] |
          select(
            type == "object" and
            .expired == false and
            .name == $artifact_name and
            (.created_at | type) == "string" and
            (.created_at > $started_at and .created_at <= $updated_at))
        ] as $matches |
        if $matches | length == 0 then
          empty
        elif $matches | length == 1 then
          $matches[0] |
          {
            id,
            name,
            size_in_bytes,
            job: $job.name
          }
        else
          error("test result artifact does not identify exactly one failed job")
        end
      ] as $selected |
      if ($selected | length) > $max_artifacts then
        error("test result artifact count exceeds the download budget")
      elif any(
        $selected[];
        (.id | type) != "number" or
        (.id | floor) != .id or
        .id < 1 or
        (.size_in_bytes | type) != "number" or
        (.size_in_bytes | floor) != .size_in_bytes or
        .size_in_bytes < 0 or
        .size_in_bytes > $max_artifact_bytes
      ) then
        error("test result artifact has invalid or excessive size metadata")
      elif ($selected | map(.id) | unique | length) != ($selected | length) then
        error("test result artifact does not identify exactly one failed job")
      elif ($selected | map(.size_in_bytes) | add // 0) > $max_total_bytes then
        error("test result artifacts exceed the cumulative download budget")
      else
        $selected
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

  if [[ ! "$max_entries" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_uncompressed_bytes" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_archive_bytes" =~ ^[1-9][0-9]*$ ]] ||
     { [ -n "$expected_archive_bytes" ] && [[ ! "$expected_archive_bytes" =~ ^[0-9]+$ ]]; }; then
    echo "::error::Invalid test results extraction budget" >&2
    return 1
  fi

  python3 - "$archive_file" "$output_directory" \
    "$max_entries" "$max_uncompressed_bytes" "$max_archive_bytes" "$expected_archive_bytes" <<'PY'
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
        trx_index = 0
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
            if not raw_name.endswith(".trx"):
                continue

            trx_index += 1
            destination = temporary_path / f"{trx_index:05d}.trx"
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

render_issue_occurrences()
{
  local current_body_file="$1"
  local new_occurrence_row="$2"
  local total_occurrence_count="$3"
  local output_file="$4"
  local max_bytes="$5"
  local output_temp

  if [ ! -f "$current_body_file" ] ||
     [[ ! "$total_occurrence_count" =~ ^[1-9][0-9]*$ ]] ||
     [[ ! "$max_bytes" =~ ^[1-9][0-9]*$ ]]; then
    echo "::error::Invalid occurrence renderer input" >&2
    return 1
  fi

  output_temp=$(mktemp)
  if ! jq -nj \
      --rawfile body "$current_body_file" \
      --arg new_row "$new_occurrence_row" \
      --argjson total "$total_occurrence_count" \
      --argjson max_bytes "$max_bytes" '
      def normalized_body:
        $body | gsub("\r\n"; "\n");
      def is_occurrence_row:
        test("^\\| [0-9]{4}-[0-9]{2}-[0-9]{2} \\| \\[[0-9]+\\]\\(https://github\\.com/[^\\n]+\\) \\| .* \\| (main|unavailable|#[0-9]+) \\|$");
      def section($rows):
        "<!-- ci-failure-occurrences:start -->\n" +
        "## Occurrences\n\n" +
        "Showing \($rows | length) most recent of \($total) occurrences.\n\n" +
        "| Date | Build | Job | Context |\n" +
        "|------|-------|-----|----|\n" +
        ($rows | join("\n")) + "\n" +
        "<!-- ci-failure-occurrences:end -->\n";
      def render($prefix; $rows):
        ($prefix | sub("\n+$"; "")) + "\n\n" + section($rows);
      def fit($prefix; $rows):
        render($prefix; $rows) as $rendered |
        if ($rendered | utf8bytelength) <= $max_bytes then
          $rendered
        elif ($rows | length) > 1 then
          fit($prefix; $rows[1:])
        else
          error("occurrence section cannot fit within the publication budget")
        end;
      def managed_parts:
        (normalized_body | split("<!-- ci-failure-occurrences:start -->")) as $start_parts |
        if ($start_parts | length) != 2 then
          error("ambiguous managed occurrence section")
        else
          ($start_parts[1] | split("<!-- ci-failure-occurrences:end -->")) as $end_parts |
          if ($end_parts | length) != 2 or ($end_parts[1] | test("^\\s*$") | not) then
            error("ambiguous managed occurrence section")
          else
            { prefix: $start_parts[0], managed: $end_parts[0], legacy: false }
          end
        end;
      def legacy_parts:
        (normalized_body | split("\n## Occurrences\n")) as $parts |
        if ($parts | length) != 2 then
          error("unsupported legacy occurrence section")
        else
          { prefix: $parts[0], managed: ("## Occurrences\n" + $parts[1]), legacy: true }
        end;
      if ($new_row | is_occurrence_row | not) then
        error("invalid occurrence row")
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
          (is_occurrence_row | not))
        then
          error("unsupported occurrence section contents")
        else
          ([$lines[] | select(is_occurrence_row)] + [$new_row]) as $rows |
          if $total < ($rows | length) then
            error("occurrence total is smaller than the rendered history")
          else
            fit($parts.prefix; $rows)
          end
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

pr_locked()
{
  local repo="$1"
  local pr_number="$2"
  local pr_json
  local locked

  if ! pr_json=$(gh api "repos/${repo}/pulls/${pr_number}"); then
    echo "::warning::Unable to determine whether PR #${pr_number} is locked" >&2
    return 1
  fi
  if ! locked=$(jq -r '
      if (.locked | type) == "boolean" then
        .locked | tostring
      else
        error("locked must be a boolean")
      end
    ' <<< "$pr_json"); then
    echo "::warning::Unable to determine whether PR #${pr_number} is locked" >&2
    return 1
  fi
  if [ "$locked" != "true" ] && [ "$locked" != "false" ]; then
    echo "::warning::Unable to determine whether PR #${pr_number} is locked" >&2
    return 1
  fi

  printf '%s\n' "$locked"
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
  select-test-results-artifact)
    ARTIFACTS_FILE="${2:?artifacts file is required}"
    STARTED_AT="${3:?start time is required}"
    UPDATED_AT="${4:?update time is required}"
    select_test_results_artifact "$ARTIFACTS_FILE" "$STARTED_AT" "$UPDATED_AT"
    ;;
  select-test-result-artifacts)
    ARTIFACTS_FILE="${2:?artifacts file is required}"
    STARTED_AT="${3:?start time is required}"
    UPDATED_AT="${4:?update time is required}"
    FAILED_JOBS_FILE="${5:?failed jobs file is required}"
    select_test_result_artifacts \
      "$ARTIFACTS_FILE" "$STARTED_AT" "$UPDATED_AT" "$FAILED_JOBS_FILE" \
      "${6:-20}" "${7:-1073741824}" "${8:-104857600}"
    ;;
  extract-test-results-artifact)
    ARTIFACT_FILE="${2:?artifact file is required}"
    OUTPUT_DIRECTORY="${3:?output directory is required}"
    extract_test_results_artifact \
      "$ARTIFACT_FILE" "$OUTPUT_DIRECTORY" \
      "${4:-10000}" "${5:-1073741824}" "${6:-104857600}" "${7:-}"
    ;;
  render-issue-occurrences)
    CURRENT_BODY_FILE="${2:?current issue body file is required}"
    NEW_OCCURRENCE_ROW="${3:?new occurrence row is required}"
    TOTAL_OCCURRENCE_COUNT="${4:?total occurrence count is required}"
    OUTPUT_FILE="${5:?output file is required}"
    MAX_BYTES="${6:-65000}"
    render_issue_occurrences \
      "$CURRENT_BODY_FILE" "$NEW_OCCURRENCE_ROW" "$TOTAL_OCCURRENCE_COUNT" "$OUTPUT_FILE" "$MAX_BYTES"
    ;;
  cache-cause-issues)
    REPO="${2:?repository is required}"
    OPEN_ISSUES_FILE="${3:?open issues file is required}"
    CLOSED_ISSUES_FILE="${4:?closed issues file is required}"
    cache_cause_issues "$REPO" "$OPEN_ISSUES_FILE" "$CLOSED_ISSUES_FILE"
    ;;
  pr-locked)
    REPO="${2:?repository is required}"
    PR_NUMBER="${3:?pull request number is required}"
    pr_locked "$REPO" "$PR_NUMBER"
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
      "$TEST_RESULTS_DIRECTORY" "$JOB_NAME" "$FAILED_JOBS_FILE" "$OUTPUT_FILE"
    ;;
  cause-job-names)
    CAUSE_FILE="${2:?cause file is required}"
    TRUSTED_FAILED_JOBS_FILE="${3:?trusted failed jobs file is required}"
    FORMAT="${4:?format is required}"

    jq -er \
      --arg format "$FORMAT" \
      --slurpfile trusted_jobs "$TRUSTED_FAILED_JOBS_FILE" "$JQ_SANITIZE_DEFS"'
        def render_code_span:
          (([scan("`+") | length] | max // 0) + 1) as $delimiter_length |
          ("`" * $delimiter_length) + " " + . + " " + ("`" * $delimiter_length);
        .job_ids as $job_ids |
        [
          $job_ids[] as $job_id |
          [$trusted_jobs[0][] | select(.id == $job_id) | .name][0]
        ] as $job_names |
        if any($job_names[]; type != "string" or length == 0) then
          error("cause references an unknown trusted failed job")
        else
          $job_names
          | map(sanitize_single_line | .[0:500])
          | if $format == "plain" then
              join(", ")
            elif $format == "display" then
              map(render_code_span) | join("<br>")
            elif $format == "table" then
              map(gsub("\\|"; "\\|") | render_code_span) | join("<br>")
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
  merge-cause)
    NEW_CAUSE_FILE="${2:?new cause file is required}"
    EXISTING_CAUSE_FILE="${3:?existing cause file is required}"
    OUTPUT_FILE="${4:?output file is required}"

    jq -s '
      .[0] as $new | .[1] as $existing |
      ($existing | del(.job_ids, .job_names)) * {
        occurrences: (
          [($existing.occurrences // [])[], ($new.occurrences // [])[]]
          | unique_by(.run_id)
          | sort_by(.observed_at)
        )
      }
    ' "$NEW_CAUSE_FILE" "$EXISTING_CAUSE_FILE" > "$OUTPUT_FILE"
    ;;
  render-prior-cause)
    CAUSE_FILE="${2:?cause file is required}"

    sanitize_document cause "$CAUSE_FILE" /dev/stdout | jq -c '{
      id,
      type,
      title: ((.title // .id // "") | .[0:238]),
      test_name: (if .test_name then .test_name[0:500] else null end),
      issue_url: (.issue_url // null),
      error_pattern: ((.error_pattern // "") | .[0:500]),
      occurrence_count: ((.occurrences // []) | length),
      last_seen: ((.occurrences // [] | sort_by(.observed_at) | last | .observed_at) // null)
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
      '
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
            if $context.run_scope == "main" and $candidate_history_state == "available" and ($triggering.number | type) == "number" then
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
                candidate_merge_history_state: $candidate_history_state,
                candidate_merges: (
                  if $candidate_history_state == "available" then
                    [
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
