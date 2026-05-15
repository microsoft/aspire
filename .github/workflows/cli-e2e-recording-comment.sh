#!/usr/bin/env bash

set -euo pipefail

write_output() {
  local name="$1"
  local value="$2"

  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    echo "${name}=${value}" >> "$GITHUB_OUTPUT"
  fi
}

parse_test_results() {
  # Parse TRX (XML) files to extract test method outcomes using yq (pre-installed on ubuntu-latest).
  # Produces a JSON map of testMethodName -> outcome for the bash comment step to consume.
  # When the same test appears in multiple TRX files (e.g. retries), "Failed" wins over other outcomes.
  if compgen -G trx_files/*.trx > /dev/null 2>&1; then
    TRX_COUNT=$(find trx_files -maxdepth 1 -name '*.trx' | wc -l)
    echo "Parsing $TRX_COUNT TRX file(s) with yq..."
    # yq (mikefarah) can read XML natively; XML attributes are exposed with the
    # default "+@" prefix. UnitTestResult is a single object when there is exactly
    # one result and an array when there are multiple, so we wrap it in [] | flatten
    # to always get an array (mikefarah/yq does not support jq-style "if type ==..."
    # expressions, which is why the previous expression silently failed and produced
    # no outcomes - leading to every recording showing "?" status).
    #
    # Each TRX file produces one JSON document; jq -s slurps them into a single
    # array of arrays which we flatten with `add`. We then build a map keyed by
    # the fully qualified test name, the simple display name, and the simple
    # method name without theory arguments so the .cast filename (which uses
    # [CallerMemberName]) can match. "Failed" wins over any other outcome when
    # duplicates exist (e.g. test retries across multiple TRX files).
    yq -p xml -o json '[.TestRun.Results.UnitTestResult] | flatten | map({"testName": .["+@testName"], "outcome": .["+@outcome"]})' trx_files/*.trx \
      | jq -s '
          (add // [])
          | map(select(.testName != null))
          | reduce .[] as $r ({};
              ($r.testName) as $full |
              ($full | split(".") | last) as $method |
              ($method | sub("\\(.*$"; "")) as $methodWithoutArguments |
              ($r.outcome) as $o |
              (if .[$full] == "Failed" then . else .[$full] = $o end) |
              (if .[$method] == "Failed" then . else .[$method] = $o end) |
              (if .[$methodWithoutArguments] == "Failed" then . else .[$methodWithoutArguments] = $o end)
            )
        ' > test_outcomes.json

    # Some recordings are created inside helper methods, so the .cast filename may
    # not match any TRX test name. In that case, fall back to the aggregate outcome
    # from the artifact directory that contained the recording.
    for artifact_dir in recordings/extracted_*; do
      [ -d "$artifact_dir" ] || continue
      compgen -G "$artifact_dir/*.cast" > /dev/null 2>&1 || continue
      compgen -G "$artifact_dir/*.trx" > /dev/null 2>&1 || continue

      ARTIFACT_OUTCOME=$(
        yq -p xml -o json '[.TestRun.Results.UnitTestResult] | flatten | map({"outcome": .["+@outcome"]})' "$artifact_dir"/*.trx \
          | jq -sr '
              (add // [])
              | map(select(.outcome != null))
              | if any(.outcome == "Failed") then "Failed"
                elif any(.outcome == "Passed") then "Passed"
                elif length > 0 then .[0].outcome
                else "Unknown"
                end
            '
      )

      [ "$ARTIFACT_OUTCOME" != "Unknown" ] || continue

      for castfile in "$artifact_dir"/*.cast; do
        [ -f "$castfile" ] || continue
        CAST_NAME=$(basename "$castfile" .cast)
        jq --arg name "$CAST_NAME" --arg outcome "$ARTIFACT_OUTCOME" \
          'if has($name) then . else .[$name] = $outcome end' \
          test_outcomes.json > test_outcomes.json.tmp
        mv test_outcomes.json.tmp test_outcomes.json
      done
    done

    OUTCOME_COUNT=$(jq 'length' test_outcomes.json)
    echo "Parsed $OUTCOME_COUNT test outcome(s)"
    # Defense-in-depth: if we had TRX files but produced zero outcomes, the parser
    # silently misfired (this is exactly how the previous yq/jq mismatch escaped).
    # Annotate as a warning so it shows up on the workflow run summary, and emit a
    # signal the consuming step uses to prepend a notice to the PR comment.
    if [ "$OUTCOME_COUNT" -eq 0 ]; then
      echo "::warning title=CLI E2E TRX parse produced no outcomes::Found ${TRX_COUNT} TRX file(s) but parsed 0 outcomes. The yq/jq expression in this step may be broken - recordings will show '?' status."
      write_output "parse_warning" "true"
    else
      write_output "parse_warning" "false"
    fi
    write_output "has_outcomes" "true"
  else
    echo "No TRX files found"
    echo '{}' > test_outcomes.json
    write_output "has_outcomes" "false"
    write_output "parse_warning" "false"
  fi
}

upload_recording() {
  local castfile="$1"
  local safe_filename="$2"

  if [ "${DRY_RUN:-false}" = "true" ]; then
    echo "${ASCIINEMA_BASE_URL:-https://example.invalid/a}/${safe_filename}"
    return
  fi

  local asciinema_url=""
  for attempt in $(seq 1 "${MAX_UPLOAD_RETRIES:-5}"); do
    upload_output=$(asciinema upload "$castfile" 2>&1) || true
    asciinema_url=$(echo "$upload_output" | grep -oP 'https://asciinema\.org/a/[a-zA-Z0-9_-]+' | head -1) || true
    if [ -n "$asciinema_url" ]; then
      break
    fi
    if [ "$attempt" -lt "${MAX_UPLOAD_RETRIES:-5}" ]; then
      delay=$((attempt * ${RETRY_BASE_DELAY_SECONDS:-30}))
      echo "Upload attempt $attempt failed, retrying in ${delay}s..." >&2
      sleep "$delay"
    fi
  done

  echo "$asciinema_url"
}

ensure_current_pr_head() {
  local current_head_sha
  current_head_sha=$(gh pr view "$PR_NUMBER" --repo "$GITHUB_REPOSITORY" --json headRefOid --jq '.headRefOid')
  if [ "$current_head_sha" != "$HEAD_SHA" ]; then
    echo "Skipping recording comment for stale workflow run $RUN_ID: run SHA $HEAD_SHA is not current PR head $current_head_sha"
    return 1
  fi
  return 0
}

post_recordings_comment() {
  PR_NUMBER="${PR_NUMBER:?PR_NUMBER must be set}"
  RUN_ID="${RUN_ID:?RUN_ID must be set}"
  HEAD_SHA="${HEAD_SHA:?HEAD_SHA must be set}"
  SHORT_SHA="${HEAD_SHA:0:7}"

  RECORDINGS_DIR="${RECORDINGS_DIR:-cast_files}"

  if [ -d "$RECORDINGS_DIR" ] && compgen -G "$RECORDINGS_DIR"/*.cast > /dev/null; then
    if [ "${DRY_RUN:-false}" != "true" ]; then
      ensure_current_pr_head || return 0
      pip install asciinema
    fi

    # Load test outcomes from TRX parsing step (JSON: {"methodName": "Passed|Failed", ...})
    HAS_OUTCOMES="${HAS_OUTCOMES:-false}"
    if [ "$HAS_OUTCOMES" = "true" ] && [ -f "test_outcomes.json" ]; then
      echo "Loaded test outcomes from TRX files"
    else
      echo "No test outcomes available, will show recordings without pass/fail status"
      echo '{}' > test_outcomes.json
    fi

    # Unique marker to identify CLI E2E recording comments
    COMMENT_MARKER="<!-- cli-e2e-recordings -->"

    # Retry configuration for asciinema uploads
    MAX_UPLOAD_RETRIES="${MAX_UPLOAD_RETRIES:-5}"
    RETRY_BASE_DELAY_SECONDS="${RETRY_BASE_DELAY_SECONDS:-30}"

    UPLOAD_COUNT=0
    FAIL_COUNT=0
    TOTAL_COUNT=0
    TEST_FAIL_COUNT=0
    UNKNOWN_COUNT=0

    # Arrays to track failed test recordings separately
    FAILED_TESTS_BODY=""
    TABLE_BODY=""

    for castfile in "$RECORDINGS_DIR"/*.cast; do
      if [ -f "$castfile" ]; then
        filename=$(basename "$castfile" .cast)
        echo "Uploading $castfile..."
        TOTAL_COUNT=$((TOTAL_COUNT + 1))

        # Sanitize filename for safe markdown rendering.
        # .cast files are named via [CallerMemberName] so should be valid C# identifiers,
        # but we sanitize defensively since this runs in a privileged workflow_run context
        # and artifacts could come from fork PRs.
        safe_filename=$(echo "$filename" | tr -cd 'A-Za-z0-9_.-')

        # Look up test outcome from TRX data.
        # .cast files are named after the test method name (via [CallerMemberName] in CreateTestTerminal),
        # so the filename matches the method name key in the outcomes JSON.
        TEST_OUTCOME=$(jq -r --arg name "$filename" '.[$name] // "Unknown"' test_outcomes.json)
        if [ "$TEST_OUTCOME" = "Passed" ]; then
          STATUS_EMOJI="✅"
        elif [ "$TEST_OUTCOME" = "Failed" ]; then
          STATUS_EMOJI="❌"
          TEST_FAIL_COUNT=$((TEST_FAIL_COUNT + 1))
        else
          STATUS_EMOJI="❔"
          UNKNOWN_COUNT=$((UNKNOWN_COUNT + 1))
        fi

        ASCIINEMA_URL=$(upload_recording "$castfile" "$safe_filename")
        if [ -n "$ASCIINEMA_URL" ]; then
          TABLE_BODY="${TABLE_BODY}
| ${STATUS_EMOJI} | ${safe_filename} | [▶️ View Recording](${ASCIINEMA_URL}) |"
          echo "Uploaded: $ASCIINEMA_URL"
          UPLOAD_COUNT=$((UPLOAD_COUNT + 1))

          # Track failed tests for the prominent section
          if [ "$TEST_OUTCOME" = "Failed" ]; then
            FAILED_TESTS_BODY="${FAILED_TESTS_BODY}
- ❌ **${safe_filename}** — [▶️ View Recording](${ASCIINEMA_URL})"
          fi
        else
          TABLE_BODY="${TABLE_BODY}
| ${STATUS_EMOJI} | ${safe_filename} | ⚠️ Upload failed |"
          echo "Failed to upload $castfile after $MAX_UPLOAD_RETRIES attempts"
          FAIL_COUNT=$((FAIL_COUNT + 1))

          if [ "$TEST_OUTCOME" = "Failed" ]; then
            FAILED_TESTS_BODY="${FAILED_TESTS_BODY}
- ❌ **${safe_filename}** — ⚠️ Recording upload failed"
          fi
        fi
      fi
    done

    echo "Uploaded $UPLOAD_COUNT recordings, $FAIL_COUNT upload failures, $TEST_FAIL_COUNT test failures"

    # Detect silent parser regressions: TRX parse step warned, OR every uploaded
    # recording resolved to "Unknown" despite recordings being present (which is
    # almost always a parser bug, not a legitimate state since CLI E2E tests run
    # under MTP and produce TRX). Surface this both as a workflow-run annotation
    # and a banner on the PR comment so reviewers don't mistake it for real data.
    PARSE_WARNING="${PARSE_WARNING:-false}"
    PARSE_NOTICE=""
    if [ "$PARSE_WARNING" = "true" ] || { [ "$TOTAL_COUNT" -gt 0 ] && [ "$UNKNOWN_COUNT" -eq "$TOTAL_COUNT" ]; }; then
      echo "::warning title=CLI E2E recording status unresolved::Could not determine pass/fail status for any of $TOTAL_COUNT recording(s). The TRX parse step may have failed - see logs."
      PARSE_NOTICE="
> ⚠️ Could not determine pass/fail status for the recordings below. The TRX parse step may be broken — see the [workflow logs](https://github.com/${GITHUB_REPOSITORY}/actions/runs/${RUN_ID})."
    fi

    # Build comment with summary outside collapsible and table inside
    if [ "$TEST_FAIL_COUNT" -gt 0 ]; then
      SUMMARY_EMOJI="❌"
      SUMMARY_TEXT="${TEST_FAIL_COUNT} test(s) failed, ${UPLOAD_COUNT} recordings uploaded"
    elif [ "$FAIL_COUNT" -gt 0 ]; then
      SUMMARY_EMOJI="⚠️"
      SUMMARY_TEXT="${UPLOAD_COUNT}/${TOTAL_COUNT} recordings uploaded, ${FAIL_COUNT} upload(s) failed"
    else
      SUMMARY_EMOJI="🎬"
      SUMMARY_TEXT="${UPLOAD_COUNT} recordings uploaded"
    fi

    # Build the failed tests section (shown outside the collapsible)
    FAILED_SECTION=""
    if [ -n "$FAILED_TESTS_BODY" ]; then
      FAILED_SECTION="
### Failed Tests
${FAILED_TESTS_BODY}
"
    fi

    COMMENT_BODY="${COMMENT_MARKER}
${SUMMARY_EMOJI} **CLI E2E Test Recordings** — ${SUMMARY_TEXT} (commit \`${SHORT_SHA}\`)
${PARSE_NOTICE}
${FAILED_SECTION}
<details>
<summary>View all recordings</summary>

| Status | Test | Recording |
|--------|------|-----------|${TABLE_BODY}

---
<sub>📹 Recordings uploaded automatically from [CI run #${RUN_ID}](https://github.com/${GITHUB_REPOSITORY}/actions/runs/${RUN_ID})</sub>

</details>"

    if [ -n "${COMMENT_OUTPUT_PATH:-}" ]; then
      printf '%s' "$COMMENT_BODY" > "$COMMENT_OUTPUT_PATH"
    fi

    if [ "${DRY_RUN:-false}" = "true" ]; then
      echo "Dry run enabled; skipping GitHub comment update"
      return
    fi

    ensure_current_pr_head || return 0

    # Delete any existing recording comments, then post the new one
    EXISTING_COMMENT_IDS=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $pr: Int!) {
        repository(owner: $owner, name: $repo) {
          pullRequest(number: $pr) {
            comments(first: 100) {
              nodes {
                databaseId
                author { login }
                body
              }
            }
          }
        }
      }' -f owner="$GITHUB_REPOSITORY_OWNER" -f repo="$GITHUB_EVENT_REPO_NAME" -F pr="$PR_NUMBER" \
      --jq '.data.repository.pullRequest.comments.nodes[] | select(.author.login == "github-actions[bot]" and (.body | contains("'"${COMMENT_MARKER}"'"))) | .databaseId') || true

    for COMMENT_ID in $EXISTING_COMMENT_IDS; do
      echo "Deleting old comment $COMMENT_ID"
      gh api \
        --method DELETE \
        -H "Accept: application/vnd.github+json" \
        "/repos/${GITHUB_REPOSITORY}/issues/comments/${COMMENT_ID}" || true
    done

    echo "Creating new comment on PR #${PR_NUMBER}"
    gh pr comment "${PR_NUMBER}" --repo "$GITHUB_REPOSITORY" --body "$COMMENT_BODY"

    echo "Posted comment to PR #${PR_NUMBER}"
  else
    echo "No recordings found in $RECORDINGS_DIR"
  fi
}

case "${1:-}" in
  parse)
    parse_test_results
    ;;
  post-comment)
    post_recordings_comment
    ;;
  *)
    echo "Usage: $0 {parse|post-comment}" >&2
    exit 2
    ;;
esac
