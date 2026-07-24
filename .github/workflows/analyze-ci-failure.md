---
description: |
  Analyzes failed PR CI builds using Copilot to determine whether the failure
  is transient (flaky test, infrastructure issue) or caused by the PR changes
  (compilation error, test regression). For transient infrastructure failures,
  reruns the CI build. For transient test failures, posts a comment with
  details and suggested next steps. For non-transient failures, posts a
  comment explaining the root cause.

on:
  workflow_run:
    workflows: ["CI"]
    types:
      - completed
    # Intentional for now: only analyze CI runs for builds against main while this workflow is being validated.
    branches:
      - main
  workflow_dispatch:
    inputs:
      run_id:
        description: "CI workflow run ID to analyze"
        required: true
        type: number

jobs:
  collect-data:
    runs-on: ubuntu-latest
    if: >-
      github.repository_owner == 'microsoft'
      && (
        github.event_name == 'workflow_dispatch'
        || (
          github.event.workflow_run.conclusion == 'failure'
          && github.event.workflow_run.run_attempt <= 3
        )
      )
    permissions:
      contents: read
      actions: read
      checks: read
      pull-requests: read
    outputs:
      has-work: ${{ steps.collect.outputs.has_work }}
      run_id: ${{ steps.collect.outputs.run_id }}
      run_attempt: ${{ steps.collect.outputs.run_attempt }}
      run_url: ${{ steps.collect.outputs.run_url }}
      pr_numbers: ${{ steps.collect.outputs.pr_numbers }}
    env:
      GH_TOKEN: ${{ github.token }}
    steps:
      - name: Checkout (for retry patterns)
        uses: actions/checkout@v4
        with:
          sparse-checkout: eng/test-retry-patterns.json
          sparse-checkout-cone-mode: false
      - name: Collect CI failure data
        id: collect
        env:
          REPO: ${{ github.repository }}
          MANUAL_RUN_ID: ${{ inputs.run_id }}
          WORKFLOW_RUN_ID: ${{ github.event.workflow_run.id }}
          EVENT_NAME: ${{ github.event_name }}
        run: |
          set -euo pipefail

          mkdir -p ci-failure-data

          # Resolve the run ID
          if [ "${EVENT_NAME}" = "workflow_dispatch" ]; then
            RUN_ID="${MANUAL_RUN_ID}"
          else
            RUN_ID="${WORKFLOW_RUN_ID}"
          fi

          echo "Analyzing CI run: ${RUN_ID}"
          echo "run_id=${RUN_ID}" >> "$GITHUB_OUTPUT"

          # Fetch the workflow run metadata
          gh api "repos/${REPO}/actions/runs/${RUN_ID}" > ci-failure-data/run.json

          RUN_ATTEMPT=$(jq -r '.run_attempt // 1' ci-failure-data/run.json)
          HEAD_SHA=$(jq -r '.head_sha // ""' ci-failure-data/run.json)
          HEAD_BRANCH=$(jq -r '.head_branch // ""' ci-failure-data/run.json)
          RUN_URL=$(jq -r '.html_url // ""' ci-failure-data/run.json)
          CONCLUSION=$(jq -r '.conclusion // ""' ci-failure-data/run.json)
          echo "run_attempt=${RUN_ATTEMPT}" >> "$GITHUB_OUTPUT"
          echo "head_sha=${HEAD_SHA}" >> "$GITHUB_OUTPUT"
          echo "run_url=${RUN_URL}" >> "$GITHUB_OUTPUT"

          # Skip analysis if the run succeeded (e.g. manual dispatch on a passing run)
          if [ "${CONCLUSION}" = "success" ]; then
            echo "Run concluded with success. Nothing to analyze."
            echo "has_work=false" >> "$GITHUB_OUTPUT"
            exit 0
          fi

          # Find the associated PR number
          PR_NUMBERS=$(jq -r '[.pull_requests[]?.number] | join(",")' ci-failure-data/run.json)
          if [ -z "${PR_NUMBERS}" ]; then
            # Fallback 1: search for PRs by head branch (requires owner:branch format)
            HEAD_OWNER=$(jq -r '.head_repository.owner.login // ""' ci-failure-data/run.json)
            if [ -n "${HEAD_OWNER}" ] && [ -n "${HEAD_BRANCH}" ]; then
              PR_NUMBERS=$(gh api "repos/${REPO}/pulls?state=open&head=${HEAD_OWNER}:${HEAD_BRANCH}" \
                --jq '[.[].number] | join(",")' 2>/dev/null || echo "")
            fi
          fi
          if [ -z "${PR_NUMBERS}" ]; then
            # Fallback 2: find PRs associated with the head commit SHA.
            # This works even when the PR is merged/closed or the run metadata
            # doesn't include the pull_requests array.
            if [ -n "${HEAD_SHA}" ]; then
              PR_NUMBERS=$(gh api "repos/${REPO}/commits/${HEAD_SHA}/pulls" \
                --jq '[.[].number] | join(",")' 2>/dev/null || echo "")
            fi
          fi
          echo "pr_numbers=${PR_NUMBERS}" >> "$GITHUB_OUTPUT"

          if [ -z "${PR_NUMBERS}" ]; then
            echo "No associated PR found. Analysis will proceed without PR context."
          fi

          # Fetch all jobs for this run attempt.
          # Use --jq '.jobs[]' to emit individual job objects (handles pagination
          # correctly) then jq -s collects them into a single JSON array.
          gh api --paginate "repos/${REPO}/actions/runs/${RUN_ID}/attempts/${RUN_ATTEMPT}/jobs" \
            --jq '.jobs[]' | jq -s '.' > ci-failure-data/all-jobs.json

          # Extract failed jobs, excluding "gate" jobs that just check dependency status.
          # Gate jobs (e.g. "Final Results", "Final Test Results") only echo "dependent jobs
          # failed" and provide zero diagnostic value — they just inflate the logs.
          jq '[.[] | select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out")
               | select(
                   (.steps // [] | map(select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out")) | length) > 0
                   and (
                     (.steps // [] | map(select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out")) | .[0].name)
                     | test("^(Fail if|Check ).*(depend|failed)"; "i") | not
                   )
                 )]' \
            ci-failure-data/all-jobs.json > ci-failure-data/failed-jobs.json

          FAILED_COUNT=$(jq 'length' ci-failure-data/failed-jobs.json)
          echo "Failed jobs: ${FAILED_COUNT}"

          if [ "${FAILED_COUNT}" -eq 0 ]; then
            echo "No failed jobs found. Skipping analysis."
            echo "has_work=false" >> "$GITHUB_OUTPUT"
            exit 0
          fi

          echo "has_work=true" >> "$GITHUB_OUTPUT"

          # Fetch logs for each failed job and extract only error-relevant lines.
          # Raw logs are huge (64KB+). Instead of blindly taking the last N lines,
          # we grep for error indicators with context to produce a focused extract.
          jq -r '.[].id' ci-failure-data/failed-jobs.json | while read -r JOB_ID; do
            JOB_NAME=$(jq -r ".[] | select(.id == ${JOB_ID}) | .name" ci-failure-data/failed-jobs.json)
            echo "Fetching logs for job: ${JOB_NAME} (${JOB_ID})"
            gh api "repos/${REPO}/actions/jobs/${JOB_ID}/logs" > "ci-failure-data/job-${JOB_ID}-raw.log" 2>/dev/null || \
              echo "(Failed to fetch logs for job ${JOB_ID})" > "ci-failure-data/job-${JOB_ID}-raw.log"

            # Extract error-relevant lines with 3 lines of context before and 5 after.
            # Patterns: compiler errors, build failures, test failures, runtime errors,
            # infrastructure errors, and GitHub Actions error annotations.
            grep -n -i -B3 -A5 \
              -e 'error [A-Z]\{2,\}[0-9]' \
              -e '##\[error\]' \
              -e '\bFAILED\b' \
              -e '\bfailed!\b' \
              -e 'Build FAILED' \
              -e 'ECONNRESET\|ECONNREFUSED\|ENOTFOUND' \
              -e 'Connection reset by peer' \
              -e 'Could not resolve host' \
              -e 'Operation timed out' \
              -e 'The SSL connection could not be established' \
              -e '403 Forbidden' \
              -e 'exit code [1-9]' \
              -e 'Process completed with exit code' \
              "ci-failure-data/job-${JOB_ID}-raw.log" 2>/dev/null \
              | head -150 > "ci-failure-data/job-${JOB_ID}.log" || true

            # If grep found nothing, fall back to last 200 lines (job may have unusual errors)
            if [ ! -s "ci-failure-data/job-${JOB_ID}.log" ]; then
              tail -200 "ci-failure-data/job-${JOB_ID}-raw.log" > "ci-failure-data/job-${JOB_ID}.log"
            fi
            rm -f "ci-failure-data/job-${JOB_ID}-raw.log"
          done

          # Fetch annotations for each failed job
          jq -r '.[].id' ci-failure-data/failed-jobs.json | while read -r JOB_ID; do
            CHECK_RUN_ID=$(jq -r ".[] | select(.id == ${JOB_ID}) | .check_run_url" ci-failure-data/failed-jobs.json \
              | grep -oP '\d+$' || echo "")
            if [ -n "${CHECK_RUN_ID}" ]; then
              gh api --paginate "repos/${REPO}/check-runs/${CHECK_RUN_ID}/annotations" \
                > "ci-failure-data/annotations-${JOB_ID}.json" 2>/dev/null || \
                echo "[]" > "ci-failure-data/annotations-${JOB_ID}.json"
            else
              echo "[]" > "ci-failure-data/annotations-${JOB_ID}.json"
            fi
          done

          # Fetch the PR diff to compare against failures
          FIRST_PR=$(echo "${PR_NUMBERS}" | cut -d',' -f1)
          if [ -n "${FIRST_PR}" ]; then
            gh api "repos/${REPO}/pulls/${FIRST_PR}/files" --paginate \
              --jq '.[]' | jq -s '[.[] | {filename, status, additions, deletions, changes}]' \
              > ci-failure-data/pr-files.json 2>/dev/null || echo "[]" > ci-failure-data/pr-files.json

            # Fetch PR metadata (state, title, author) so the agent doesn't need
            # to make MCP pull_request_read calls at runtime.
            gh api "repos/${REPO}/pulls/${FIRST_PR}" \
              --jq '{number, title, state, user: .user.login, head_branch: .head.ref, base_branch: .base.ref, html_url}' \
              > ci-failure-data/pr-metadata.json 2>/dev/null || echo "{}" > ci-failure-data/pr-metadata.json
          fi

          # Load the known transient failure patterns for reference
          if [ -f "eng/test-retry-patterns.json" ]; then
            cp eng/test-retry-patterns.json ci-failure-data/retry-patterns.json
          fi

          # Fetch prior cause files from the memory branch so the agent can
          # identify recurring failures and append occurrences rather than
          # creating duplicate cause entries.
          MEMORY_BRANCH="memory/ci-failure-analysis"
          if git clone --depth 1 --branch "$MEMORY_BRANCH" \
              "https://x-access-token:${GH_TOKEN}@github.com/${REPO}.git" \
              memory-checkout 2>/dev/null; then
            if [ -d "memory-checkout/causes" ]; then
              mkdir -p ci-failure-data/prior-causes
              cp memory-checkout/causes/*.json ci-failure-data/prior-causes/ 2>/dev/null || true
              PRIOR_COUNT=$(find ci-failure-data/prior-causes -name '*.json' -type f 2>/dev/null | wc -l)
              echo "Loaded ${PRIOR_COUNT} prior cause file(s) from memory branch"
            else
              echo "No prior causes directory on memory branch"
            fi
            rm -rf memory-checkout
          else
            echo "Memory branch not found (first run or not yet created)"
          fi

          # Fetch test results artifact if available and extract test failure info
          ARTIFACT_NAME=$(gh api "repos/${REPO}/actions/runs/${RUN_ID}/artifacts" \
            --jq '[.artifacts[] | select(.name | test("test-results|TestResults"; "i"))] | first | .name // empty' 2>/dev/null || echo "")
          if [ -n "${ARTIFACT_NAME}" ]; then
            echo "Downloading test results artifact: ${ARTIFACT_NAME}..."
            mkdir -p ci-failure-data/test-results
            if gh run download "${RUN_ID}" --repo "${REPO}" --name "${ARTIFACT_NAME}" --dir ci-failure-data/test-results 2>&1; then
              echo "Download complete."

              # List TRX files found
              TRX_COUNT=$(find ci-failure-data/test-results -name "*.trx" -type f 2>/dev/null | wc -l)
              echo "Found ${TRX_COUNT} TRX file(s):"
              find ci-failure-data/test-results -name "*.trx" -type f 2>/dev/null | while IFS= read -r f; do
                echo "  - $(basename "$f") ($(stat -c%s "$f" 2>/dev/null || echo "?") bytes)"
              done || true

              # Parse TRX files for failed tests using yq (pre-installed) + jq.
              # yq converts XML to JSON, then jq extracts failed test info.
              # TRX uses UnitTestResult elements with outcome="Failed" containing
              # Output/ErrorInfo/Message and Output/ErrorInfo/StackTrace.
              > ci-failure-data/test-failures.jsonl
              find ci-failure-data/test-results -name "*.trx" -type f 2>/dev/null | while IFS= read -r TRX_FILE; do
                echo "Processing: $(basename "$TRX_FILE")"
                yq -p xml -o json '.' "$TRX_FILE" 2>/dev/null | jq -r '
                  # Navigate to UnitTestResult — may be array or single object
                  (.TestRun.Results.UnitTestResult // []) |
                  (if type == "array" then . else [.] end) |
                  map(select(.["+@outcome"] == "Failed")) |
                  .[] |
                  {
                    test: (.["+@testName"] // ""),
                    error: ((.Output.ErrorInfo.Message // "") | if type == "object" then (.["+content"] // "") else tostring end | .[0:1000]),
                    stack_trace: ((.Output.ErrorInfo.StackTrace // "") | if type == "object" then (.["+content"] // "") else tostring end | .[0:2000])
                  }
                ' >> ci-failure-data/test-failures.jsonl 2>/dev/null || true
              done
              jq -s '.' ci-failure-data/test-failures.jsonl > ci-failure-data/test-failures.json 2>/dev/null || echo "[]" > ci-failure-data/test-failures.json
              rm -f ci-failure-data/test-failures.jsonl
              echo "Extracted $(jq 'length' ci-failure-data/test-failures.json) test failure(s) from TRX files"

              # Clean up the extracted files to save space in artifact
              rm -rf ci-failure-data/test-results
            else
              echo "Warning: Failed to download test results artifact"
            fi
          else
            echo "No test results artifact found for run ${RUN_ID}"
          fi

          echo "Data collection complete."

      - name: Create analysis summary
        if: steps.collect.outputs.has_work == 'true'
        env:
          RUN_ID: ${{ steps.collect.outputs.run_id }}
          RUN_ATTEMPT: ${{ steps.collect.outputs.run_attempt }}
          RUN_URL: ${{ steps.collect.outputs.run_url }}
          PR_NUMBERS: ${{ steps.collect.outputs.pr_numbers }}
        run: |
          set -euo pipefail

          # Create a structured summary of the failure data for the agent
          {
            echo "# CI Failure Analysis Data"
            echo ""
            echo "## Run Information"
            echo "- **Run ID**: ${RUN_ID}"
            echo "- **Run Attempt**: ${RUN_ATTEMPT}"
            echo "- **Run URL**: ${RUN_URL}"
            echo "- **Associated PRs**: ${PR_NUMBERS}"
            echo ""

            echo "## Failed Jobs"
            echo ""
            jq -r '.[] | "### Job: \(.name)\n- **ID**: \(.id)\n- **Conclusion**: \(.conclusion)\n- **URL**: \(.html_url // "N/A")\n- **Failed Steps**: \([.steps[]? | select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out") | .name] | join(", "))\n"' \
              ci-failure-data/failed-jobs.json

            echo "## Job Logs (Error-Focused)"
            echo ""
            for LOG_FILE in ci-failure-data/job-*.log; do
              if [ -f "${LOG_FILE}" ]; then
                JOB_ID=$(basename "${LOG_FILE}" | sed 's/job-\(.*\)\.log/\1/')
                JOB_NAME=$(jq -r ".[] | select(.id == ${JOB_ID}) | .name" ci-failure-data/failed-jobs.json 2>/dev/null || echo "Unknown")
                echo "### Logs: ${JOB_NAME} (${JOB_ID})"
                echo '```'
                cat "${LOG_FILE}"
                echo '```'
                echo ""
              fi
            done

            echo "## Job Annotations"
            echo ""
            for ANN_FILE in ci-failure-data/annotations-*.json; do
              if [ -f "${ANN_FILE}" ]; then
                JOB_ID=$(basename "${ANN_FILE}" | sed 's/annotations-\(.*\)\.json/\1/')
                JOB_NAME=$(jq -r ".[] | select(.id == ${JOB_ID}) | .name" ci-failure-data/failed-jobs.json 2>/dev/null || echo "Unknown")
                ANN_COUNT=$(jq 'length' "${ANN_FILE}" 2>/dev/null || echo "0")
                if [ "${ANN_COUNT}" -gt 0 ]; then
                  echo "### Annotations: ${JOB_NAME} (${JOB_ID})"
                  jq -r '.[] | "- **\(.annotation_level // "unknown")**: \(.message // "no message")"' "${ANN_FILE}" 2>/dev/null || true
                  echo ""
                fi
              fi
            done

            echo "## Test Failures (from TRX artifacts)"
            echo ""
            if [ -f "ci-failure-data/test-failures.json" ]; then
              FAILURE_COUNT=$(jq 'length' ci-failure-data/test-failures.json 2>/dev/null || echo "0")
              if [ "${FAILURE_COUNT}" -gt 0 ]; then
                jq -r '.[] | "### `\(.test)`\n\n**Error:**\n```\n\(.error)\n```\n" + (if .stack_trace != "" then "**Stack Trace:**\n```\n\(.stack_trace)\n```\n" else "" end)' ci-failure-data/test-failures.json 2>/dev/null || echo "No parseable test failures."
              else
                echo "No test failures extracted from TRX artifacts."
              fi
            else
              echo "No test results artifact available."
            fi
            echo ""

            echo "## Pull Request"
            echo ""
            if [ -f "ci-failure-data/pr-metadata.json" ]; then
              jq -r '"- **PR**: #\(.number) \(.title)\n- **Author**: @\(.user)\n- **State**: \(.state)\n- **Branch**: \(.head_branch) → \(.base_branch)\n- **URL**: \(.html_url)"' ci-failure-data/pr-metadata.json 2>/dev/null || echo "No PR metadata available."
            else
              echo "No PR metadata available."
            fi
            echo ""

            echo "## PR Changed Files"
            echo ""
            if [ -f "ci-failure-data/pr-files.json" ]; then
              jq -r '.[] | "- \(.filename) (\(.status), +\(.additions)/-\(.deletions))"' ci-failure-data/pr-files.json 2>/dev/null || echo "No file data available."
            else
              echo "No PR file data available."
            fi
            echo ""

            echo "## Known Transient Failure Patterns"
            echo ""
            if [ -f "ci-failure-data/retry-patterns.json" ]; then
              echo "### Test Failure Patterns"
              jq -r '.testFailurePatterns[]? | "- \(.reason // "unnamed"): \(if .output | type == "string" then .output else .output.regex end)"' \
                ci-failure-data/retry-patterns.json 2>/dev/null || echo "None loaded."
              echo ""
              echo "### Job Failure Patterns"
              jq -r '.jobFailurePatterns[]? | "- \(.reason // "unnamed"): \(if .output | type == "string" then .output else .output.regex end)"' \
                ci-failure-data/retry-patterns.json 2>/dev/null || echo "None loaded."
            else
              echo "No retry patterns file found."
            fi
            echo ""

            echo "## Prior Causes (from memory branch)"
            echo ""
            echo "These are previously identified CI failure causes. If this run's"
            echo "failure matches an existing cause, reuse the same cause ID and"
            echo "append a new occurrence rather than creating a duplicate."
            echo ""
            if [ -d "ci-failure-data/prior-causes" ] && [ "$(find ci-failure-data/prior-causes -name '*.json' -type f 2>/dev/null | wc -l)" -gt 0 ]; then
              for CAUSE_FILE in ci-failure-data/prior-causes/*.json; do
                [ -f "$CAUSE_FILE" ] || continue
                jq -r '"### `\(.id)`\n- **Type**: \(.type)\n- **Title**: \(.title)\n- **Test**: \(.test_name // "N/A")\n- **Issue**: \(.issue_url // "none")\n- **Error pattern**: \(.error_pattern | .[0:300])\n- **Occurrences**: \(.occurrences | length)\n- **Last seen**: \(.occurrences | sort_by(.observed_at) | last | .observed_at // "unknown")\n"' \
                  "$CAUSE_FILE" 2>/dev/null || true
              done
            else
              echo "No prior causes available (first run or memory branch not initialized)."
            fi
          } > ci-failure-data/analysis-summary.md

          echo "Analysis summary written to ci-failure-data/analysis-summary.md"

      - uses: actions/upload-artifact@v4
        if: steps.collect.outputs.has_work == 'true'
        with:
          name: ci-failure-data
          path: ci-failure-data/

if: needs.collect-data.outputs.has-work == 'true'

env:
  # Set to 'true' to actually rerun failed CI jobs on transient failures.
  # Set to 'false' for dry-run mode: the agent still analyzes and comments
  # on the PR, but the comment will note that it was a dry run and no rerun
  # was triggered. Comments are intentionally posted even in dry-run mode to
  # provide visibility into CI failure classifications for debugging and
  # validation of the analysis quality.
  ENABLE_RERUN: 'false'

concurrency:
  group: analyze-ci-failure-${{ github.event_name == 'workflow_dispatch' && inputs.run_id || github.event.workflow_run.id }}
  cancel-in-progress: false

permissions:
  contents: read
  actions: read
  checks: read
  pull-requests: read
  issues: read
  copilot-requests: write

network:
  allowed:
    - defaults
    - github

safe-outputs:
  jobs:
    publish-data:
      name: "Publish analysis data and comment on PR"
      description: |
        Publishes the CI failure analysis to the memory branch and posts a PR
        comment. The agent must write:
          - /tmp/gh-aw/agent/analysis-result.json (run summary)
          - /tmp/gh-aw/agent/causes/*.json (one file per failure cause)
        Emit exactly one `publish_data` item with run_id and pr_numbers.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        contents: write
        issues: write
        pull-requests: write
      inputs:
        run_id:
          description: "The workflow run ID that was analyzed."
          required: true
          type: number
        pr_numbers:
          description: "Comma-separated list of associated PR numbers."
          required: true
          type: string
      env:
        GH_TOKEN: ${{ github.token }}
      steps:
        - name: Publish analysis data and comment on PR
          run: |
            set -euo pipefail

            OUTPUT_FILE="$GH_AW_AGENT_OUTPUT"
            if [ -z "$OUTPUT_FILE" ]; then
              echo "::error::No GH_AW_AGENT_OUTPUT environment variable found"
              exit 1
            fi

            ARTIFACT_DIR=$(dirname "$OUTPUT_FILE")
            ANALYSIS_FILE="$ARTIFACT_DIR/agent/analysis-result.json"
            CAUSES_DIR="$ARTIFACT_DIR/agent/causes"

            if [ ! -f "$ANALYSIS_FILE" ]; then
              echo "::error::Analysis result not found at $ANALYSIS_FILE"
              exit 1
            fi

            # Validate summary JSON
            if ! jq empty "$ANALYSIS_FILE" 2>/dev/null; then
              echo "::error::analysis-result.json is not valid JSON"
              exit 1
            fi

            # Validate cause files
            if [ -d "$CAUSES_DIR" ]; then
              for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
                [ -f "$CAUSE_FILE" ] || continue
                if ! jq empty "$CAUSE_FILE" 2>/dev/null; then
                  echo "::warning::Invalid JSON in cause file: $(basename "$CAUSE_FILE")"
                fi
              done
            fi

            REPO="${{ github.repository }}"
            MEMORY_BRANCH="memory/ci-failure-analysis"

            # Read fields from the analysis JSON
            RUN_ID=$(jq -r '.run_id' "$ANALYSIS_FILE")
            VERDICT=$(jq -r '.verdict' "$ANALYSIS_FILE")
            RUN_URL=$(jq -r '.run_url // ""' "$ANALYSIS_FILE")
            # Build a comma-separated list of PR numbers. The JSON schema has
            # a single pr.number; if the collect-data job passed multiple PRs
            # in the future, extend the agent schema accordingly.
            PR_NUMBERS=$(jq -r '.pr.number // "" | tostring' "$ANALYSIS_FILE")

            # ── 1. Set up memory branch and merge cause data ──
            # Skip persisting data for code-issue verdicts — these are not
            # actionable by CI automation and would just add noise.
            if [ "$VERDICT" = "code-issue" ]; then
              echo "Verdict is code-issue. Skipping memory branch persistence."
            else
              if ! git clone --depth 1 --branch "$MEMORY_BRANCH" \
                  "https://x-access-token:${GH_TOKEN}@github.com/${REPO}.git" \
                  memory-repo 2>/dev/null; then
                echo "Memory branch does not exist yet, creating orphan branch"
                git init memory-repo
                git -C memory-repo checkout --orphan "$MEMORY_BRANCH"
                git -C memory-repo remote add origin \
                  "https://x-access-token:${GH_TOKEN}@github.com/${REPO}.git"
              fi
              git -C memory-repo config user.name "github-actions[bot]"
              git -C memory-repo config user.email "github-actions[bot]@users.noreply.github.com"

              # Store run summary under runs/ directory
              mkdir -p "memory-repo/runs"
              cp "$ANALYSIS_FILE" "memory-repo/runs/${RUN_ID}.json"

              # Store individual cause files under causes/ (shared across runs).
              # Each cause file accumulates occurrences over time. The agent
              # writes cause definitions (no occurrences); we build the occurrence
              # from the run summary and merge it into the stored cause file.
              if [ -d "$CAUSES_DIR" ]; then
                mkdir -p "memory-repo/causes"

                # Build the occurrence entry from the run summary JSON
                ANALYZED_AT=$(jq -r '.analyzed_at' "$ANALYSIS_FILE")
                PR_NUMBER=$(jq -r '.pr.number // 0' "$ANALYSIS_FILE")
                # Find the first failed job name for context in the occurrence
                FIRST_JOB=$(jq -r '.failed_jobs[0].name // "unknown"' "$ANALYSIS_FILE")

                for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
                  [ -f "$CAUSE_FILE" ] || continue
                  # Skip code-issue causes — only persist transient/flaky causes.
                  CAUSE_TYPE_CHECK=$(jq -r '.type' "$CAUSE_FILE" 2>/dev/null || echo "")
                  if [ "$CAUSE_TYPE_CHECK" = "code-issue" ]; then
                    continue
                  fi
                  CAUSE_BASENAME=$(basename "$CAUSE_FILE")
                  EXISTING="memory-repo/causes/${CAUSE_BASENAME}"

                  # Add an occurrences array with this run's entry to the agent's cause file
                  CAUSE_WITH_OCC=$(jq --argjson run_id "$RUN_ID" \
                    --arg run_url "$RUN_URL" \
                    --arg job "$FIRST_JOB" \
                    --argjson pr_number "$PR_NUMBER" \
                    --arg observed_at "$ANALYZED_AT" \
                    '. + {occurrences: [{run_id: $run_id, run_url: $run_url, job: $job, pr_number: $pr_number, observed_at: $observed_at}]}' \
                    "$CAUSE_FILE")

                  if [ -f "$EXISTING" ]; then
                    # Merge: append new occurrence, deduplicate by run_id
                    echo "$CAUSE_WITH_OCC" | jq -s --slurpfile existing "$EXISTING" '
                      .[0] as $new | $existing[0] as $ex |
                      ($new | del(.occurrences)) * {
                        occurrences: (
                          [$ex.occurrences[], $new.occurrences[]]
                          | unique_by(.run_id)
                          | sort_by(.observed_at)
                        )
                      }
                    ' > "${EXISTING}.tmp" && mv "${EXISTING}.tmp" "$EXISTING"
                  else
                    echo "$CAUSE_WITH_OCC" > "$EXISTING"
                  fi
                done
                CAUSE_COUNT=$(find "memory-repo/causes" -name '*.json' -type f 2>/dev/null | wc -l)
                echo "Persisted cause files to causes/ (${CAUSE_COUNT} total)"
              fi

            # ── 2. Create or update issues for each cause ──
            if [ -d "$CAUSES_DIR" ]; then
              # Build occurrence info from the run summary for issue updates
              ANALYZED_AT=$(jq -r '.analyzed_at' "$ANALYSIS_FILE")
              PR_NUMBER=$(jq -r '.pr.number // 0' "$ANALYSIS_FILE")
              FIRST_JOB=$(jq -r '.failed_jobs[0].name // "unknown"' "$ANALYSIS_FILE")

              # Build the occurrence table row for this run
              OCC_DATE=$(echo "$ANALYZED_AT" | cut -dT -f1)
              NEW_OCCURRENCE_ROW="| ${OCC_DATE} | [${RUN_ID}](${RUN_URL}) | ${FIRST_JOB} | #${PR_NUMBER} |"

              for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
                [ -f "$CAUSE_FILE" ] || continue
                jq empty "$CAUSE_FILE" 2>/dev/null || continue

                CAUSE_ID=$(jq -r '.id' "$CAUSE_FILE")

                # Validate CAUSE_ID is a safe slug (lowercase alphanumeric + hyphens)
                # to prevent HTML comment injection via the marker.
                if ! echo "$CAUSE_ID" | grep -qP '^[a-z0-9][a-z0-9-]*$'; then
                  echo "::warning::Invalid cause ID '${CAUSE_ID}', skipping"
                  continue
                fi

                CAUSE_TYPE=$(jq -r '.type' "$CAUSE_FILE")

                # Skip issue creation for code-issue causes — those are the
                # PR author's responsibility, not a recurring CI problem.
                if [ "$CAUSE_TYPE" = "code-issue" ]; then
                  echo "Skipping issue for code-issue cause: ${CAUSE_ID}"
                  continue
                fi

                CAUSE_STORED="memory-repo/causes/${CAUSE_ID}.json"
                MARKER="<!-- ci-failure-cause:${CAUSE_ID} -->"

                # Check if the stored cause file already has a linked issue
                EXISTING_ISSUE=""
                if [ -f "$CAUSE_STORED" ]; then
                  STORED_ISSUE_URL=$(jq -r '.issue_url // empty' "$CAUSE_STORED")
                  if [ -n "$STORED_ISSUE_URL" ]; then
                    # Extract issue number from URL (e.g. .../issues/1234 -> 1234)
                    EXISTING_ISSUE=$(echo "$STORED_ISSUE_URL" | grep -oP '\d+$' || true)
                    # Verify issue still exists
                    if [ -n "$EXISTING_ISSUE" ]; then
                      ISSUE_STATE=$(gh api "repos/${REPO}/issues/${EXISTING_ISSUE}" --jq '.state' 2>/dev/null || echo "")
                      if [ -z "$ISSUE_STATE" ]; then
                        echo "Linked issue #${EXISTING_ISSUE} no longer exists, will create new"
                        EXISTING_ISSUE=""
                      fi
                    fi
                  fi
                fi

                # If no stored issue link, fall back to searching by marker
                REOPEN="false"
                if [ -z "$EXISTING_ISSUE" ]; then
                  # Fetch labeled issues for marker search (lazy-loaded once)
                  if [ -z "${ISSUES_CACHE_LOADED:-}" ]; then
                    OPEN_ISSUES_CACHE=$(mktemp)
                    CLOSED_ISSUES_CACHE=$(mktemp)
                    gh issue list --repo "$REPO" --label "ci-failure-cause" --state open --limit 500 --json number,body \
                      > "$OPEN_ISSUES_CACHE" 2>/dev/null || echo '[]' > "$OPEN_ISSUES_CACHE"
                    gh issue list --repo "$REPO" --label "ci-failure-cause" --state closed --limit 500 --json number,body \
                      > "$CLOSED_ISSUES_CACHE" 2>/dev/null || echo '[]' > "$CLOSED_ISSUES_CACHE"
                    ISSUES_CACHE_LOADED="true"
                  fi

                  EXISTING_ISSUE=$(jq -r --arg marker "$MARKER" '.[] | select(.body | contains($marker)) | .number' "$OPEN_ISSUES_CACHE" | head -1 || true)

                  if [ -z "$EXISTING_ISSUE" ]; then
                    EXISTING_ISSUE=$(jq -r --arg marker "$MARKER" '.[] | select(.body | contains($marker)) | .number' "$CLOSED_ISSUES_CACHE" | head -1 || true)
                    if [ -n "$EXISTING_ISSUE" ]; then
                      REOPEN="true"
                    fi
                  fi
                else
                  # Check if the stored issue is closed (may need reopening)
                  if [ "$ISSUE_STATE" = "closed" ]; then
                    REOPEN="true"
                  fi
                fi

                if [ -n "$EXISTING_ISSUE" ]; then
                  # Store issue URL in the cause file on memory branch
                  ISSUE_URL="https://github.com/${REPO}/issues/${EXISTING_ISSUE}"
                  if [ -f "$CAUSE_STORED" ]; then
                    jq --arg url "$ISSUE_URL" '.issue_url = $url' "$CAUSE_STORED" > "${CAUSE_STORED}.tmp" \
                      && mv "${CAUSE_STORED}.tmp" "$CAUSE_STORED"
                  fi

                  # Append new occurrence rows to the existing issue body, skipping
                  # if this run_id is already recorded (avoids duplicates on re-runs).
                  CURRENT_BODY=$(gh api "repos/${REPO}/issues/${EXISTING_ISSUE}" --jq '.body // ""')
                  # Anchor the pattern with '(' from the markdown link to avoid
                  # partial matches (e.g., run 123 matching run 1234).
                  if echo "$CURRENT_BODY" | grep -qF "[${RUN_ID}]("; then
                    echo "Occurrence for run ${RUN_ID} already recorded in issue #${EXISTING_ISSUE}. Skipping."
                  else
                    BODY_FILE=$(mktemp)
                    printf '%s\n%s\n' "$CURRENT_BODY" "$NEW_OCCURRENCE_ROW" > "$BODY_FILE"
                    gh issue edit "$EXISTING_ISSUE" --repo "$REPO" --body-file "$BODY_FILE"
                    rm -f "$BODY_FILE"
                  fi

                  if [ "$REOPEN" = "true" ]; then
                    gh issue reopen "$EXISTING_ISSUE" --repo "$REPO"
                    echo "Reopened and updated issue #${EXISTING_ISSUE} for cause: ${CAUSE_ID}"
                  else
                    echo "Updated issue #${EXISTING_ISSUE} for cause: ${CAUSE_ID}"
                  fi
                else
                  # Create a new issue for this cause
                  BODY_FILE=$(mktemp)
                  TEST_NAME=$(jq -r '.test_name // empty' "$CAUSE_FILE")
                  {
                    echo "${MARKER}"
                    echo ""
                    echo "## Build Information"
                    echo ""
                    echo "Build: ${RUN_URL}"
                    if [ -n "$TEST_NAME" ]; then
                      echo "Build error leg or test failing: ${FIRST_JOB} / \`${TEST_NAME}\`"
                    else
                      echo "Build error leg: ${FIRST_JOB}"
                    fi
                    echo "Pull request: #${PR_NUMBER}"
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
                    echo "| Date | Build | Job | PR |"
                    echo "|------|-------|-----|----|"
                    echo "$NEW_OCCURRENCE_ROW"
                  } > "$BODY_FILE"

                  LABELS="ci-failure-cause"
                  if [ "$CAUSE_TYPE" = "flaky-test" ]; then
                    LABELS="ci-failure-cause,test-failure"
                  fi

                  # Build the title via jq to avoid shell metacharacter issues
                  # with agent-generated cause titles.
                  ISSUE_TITLE=$(jq -r '"[CI Failure] " + .title' "$CAUSE_FILE")
                  CREATED_ISSUE_URL=$(gh issue create --repo "$REPO" \
                    --title "$ISSUE_TITLE" \
                    --label "$LABELS" \
                    --body-file "$BODY_FILE")
                  rm -f "$BODY_FILE"
                  echo "Created issue for cause: ${CAUSE_ID} — ${CREATED_ISSUE_URL}"

                  # Store issue URL in the cause file on memory branch
                  if [ -f "$CAUSE_STORED" ]; then
                    jq --arg url "$CREATED_ISSUE_URL" '.issue_url = $url' "$CAUSE_STORED" > "${CAUSE_STORED}.tmp" \
                      && mv "${CAUSE_STORED}.tmp" "$CAUSE_STORED"
                  fi
                fi
              done
              rm -f "${OPEN_ISSUES_CACHE:-}" "${CLOSED_ISSUES_CACHE:-}"
            fi

              # ── 3. Push memory branch ──
              git -C memory-repo add -A
              if git -C memory-repo diff --cached --quiet; then
                echo "No changes to memory branch"
              else
                git -C memory-repo commit -m "Add CI failure analysis for run ${RUN_ID}"
                git -C memory-repo push origin "HEAD:$MEMORY_BRANCH"
                echo "Memory branch updated with analysis for run ${RUN_ID}"
              fi
            fi

            # ── 4. Post PR comment using the analysis JSON ──
            FIRST_PR=$(echo "$PR_NUMBERS" | cut -d',' -f1)
            if [ -z "$FIRST_PR" ] || [ "$FIRST_PR" = "null" ]; then
              echo "No PR number found in analysis. Skipping comment."
              exit 0
            fi

            # Check PR is not locked (still comment on closed PRs)
            PR_LOCKED=$(gh api "repos/${REPO}/pulls/${FIRST_PR}" --jq '.locked' 2>/dev/null || echo "false")
            if [ "$PR_LOCKED" = "true" ]; then
              echo "PR #${FIRST_PR} is locked. Skipping comment."
              exit 0
            fi

            # Build comment body from the analysis JSON and write to a file
            # to avoid shell expansion issues and ARG_MAX limits.
            COMMENT_FILE=$(mktemp)
            jq -r '
              def job_list:
                [.failed_jobs[] | "- `\(.name)` — \(.reason) (\(.classification))"]
                | join("\n");
              def test_list:
                [.failed_tests[]? | select(.classification == "flaky") |
                  "- `\(.name)` in job `\(.job)`\n  - **Error**: \(.error)\n" +
                  (if (.stack_trace // "") != "" then "  - **Stack Trace** (first frames):\n    ```\n    \(.stack_trace | split("\n") | .[0:5] | join("\n    "))\n    ```\n" else "" end) +
                  "  - **Why likely flaky**: \(.reason)"]
                | join("\n");

              "<!-- analyze-ci-failure -->\n" +
              if .verdict == "transient-infra" then
                "🔍 **CI Failure Analysis: Transient Infrastructure Failure**\n\nThe CI build failed due to transient infrastructure issues.\n\n**Failed jobs:**\n" + job_list + "\n\nIf a rerun was not already requested automatically, visit the [workflow run page](" + .run_url + ") to rerun the failed jobs manually.\n"
              elif .verdict == "flaky-test" then
                "⚠️ **CI Failure Analysis: Possible Flaky Test(s)**\n\nThe CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.\n\n**Suspected flaky test(s):**\n" + test_list + "\n\n**Suggested actions:**\n- Re-run the failed CI jobs to confirm if the failure is intermittent\n- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using `/quarantine-test <test name> <issue URL>`\n- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky\n\nYou can re-run the failed jobs from the [workflow run page](" + .run_url + ").\n"
              elif .verdict == "code-issue" then
                "❌ **CI Failure Analysis: Code Issue Detected**\n\nThe CI build failed due to issue(s) caused by changes in this PR.\n\n**Failed jobs:**\n" + job_list + "\n\nThe CI will not be automatically rerun. Please fix the issue and push an updated commit.\n"
              else
                "⚠️ **CI Failure Analysis: Mixed Failures**\n\nThe CI build contains both transient and non-transient failures.\n\n**Failed jobs:**\n" + job_list + "\n\nThe CI will not be automatically rerun. Please review the failures above.\n"
              end
            ' "$ANALYSIS_FILE" > "$COMMENT_FILE"

            # Update an existing analysis comment if one exists (by marker),
            # otherwise create a new one. This prevents stacking duplicate
            # comments on PRs with repeated CI failures.
            MARKER="<!-- analyze-ci-failure -->"
            EXISTING_COMMENT_ID=$(gh api "repos/${REPO}/issues/${FIRST_PR}/comments" --paginate \
              --jq ".[] | select(.body | contains(\"${MARKER}\")) | .id" 2>/dev/null | head -1 || true)

            if [ -n "$EXISTING_COMMENT_ID" ]; then
              gh api --method PATCH "repos/${REPO}/issues/comments/${EXISTING_COMMENT_ID}" \
                -f body="$(cat "$COMMENT_FILE")" > /dev/null
              echo "Updated existing analysis comment (ID: ${EXISTING_COMMENT_ID}) on PR #${FIRST_PR}"
            else
              gh pr comment "$FIRST_PR" --repo "$REPO" --body-file "$COMMENT_FILE"
              echo "Posted new analysis comment on PR #${FIRST_PR}"
            fi
            rm -f "$COMMENT_FILE"
    rerun-failed-jobs:
      name: "Rerun failed CI jobs"
      description: |
        Reruns the failed CI jobs when the agent determines all failures are
        transient infrastructure issues. Emit exactly one `rerun_failed_jobs`
        item with the run_id and pr_numbers when a rerun is warranted.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        actions: write
        contents: read
        pull-requests: write
      inputs:
        run_id:
          description: "The workflow run ID to rerun failed jobs for."
          required: true
          type: number
        pr_numbers:
          description: "Comma-separated list of associated PR numbers."
          required: true
          type: string
        reason:
          description: "Short summary of why the rerun was requested."
          required: true
          type: string
      steps:
        - name: Rerun failed jobs
          uses: actions/github-script@v9
          env:
            ENABLE_RERUN: ${{ env.ENABLE_RERUN }}
          with:
            script: |
              const fs = require('fs');

              // Read inputs from the agent output artifact.
              // gh-aw writes { "items": [ { "type": "rerun_failed_jobs", ... } ] }.
              const outputFile = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputFile || !fs.existsSync(outputFile)) {
                core.setFailed('Agent output file not found');
                return;
              }
              const payload = JSON.parse(fs.readFileSync(outputFile, 'utf8'));
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'rerun_failed_jobs');
              if (!item) {
                core.info('No rerun_failed_jobs items in agent output.');
                return;
              }

              const owner = context.repo.owner;
              const repo = context.repo.repo;
              const runId = Number(item.run_id);
              const prNumbers = String(item.pr_numbers).split(',').map(Number).filter(n => n > 0);
              const reason = item.reason || '';
              const enableRerun = String(process.env.ENABLE_RERUN).toLowerCase() === 'true';

              if (!Number.isInteger(runId) || runId <= 0) {
                core.setFailed(`Invalid run_id: ${item.run_id}`);
                return;
              }

              if (!enableRerun) {
                core.info(`Dry-run mode (ENABLE_RERUN is not 'true'). Would have rerun failed jobs for run ${runId}. Reason: ${reason}`);
                return;
              }

              // Verify at least one PR is still open
              let hasOpenPr = false;
              for (const prNumber of prNumbers) {
                try {
                  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: prNumber });
                  if (pr.state === 'open') {
                    hasOpenPr = true;
                    break;
                  }
                } catch (e) {
                  core.warning(`Failed to check PR #${prNumber}: ${e.message}`);
                }
              }

              if (!hasOpenPr) {
                core.info('All associated PRs are closed. Skipping rerun.');
                return;
              }

              // Request rerun of failed jobs
              await github.rest.actions.reRunWorkflowFailedJobs({
                owner,
                repo,
                run_id: runId,
              });

              core.info(`Requested rerun of failed jobs for run ${runId}. Reason: ${reason}`);

steps:
  - uses: actions/download-artifact@v4
    with:
      name: ci-failure-data
      path: ci-failure-data/
---

# Analyze CI Failure

You are analyzing a failed CI build for a pull request in the **microsoft/aspire** repository. Your job is to determine the root cause of the failure and take the appropriate action.

## Workflow

### Step 1: Read the summary file

Read `ci-failure-data/analysis-summary.md`. It contains the run information, PR metadata, failed jobs, error-focused logs, annotations, test failures, PR changed files, and known transient failure patterns.

### Step 2: Analyze

Analyze all of the data to classify each failed job (see **Classification Rules** below).

#### Matching against prior causes (transient failures only)

When a failure is classified as `flaky-test` or `infra-failure` (NOT `code-issue`), check the **Prior Causes** section in the summary for a match. Prior causes are loaded from JSON files in the `ci-failure-data/prior-causes/` directory (one file per cause, e.g. `ci-failure-data/prior-causes/nuget-feed-timeout.json`). These files are fetched by the `collect-data` job from the `memory/ci-failure-analysis` branch's `causes/` directory and rendered into the summary under the "Prior Causes (from memory branch)" heading.

If any of this run's transient failures match an existing cause, you MUST reuse that cause's `id` when writing the cause file in Step 3b. This allows the publish job to merge occurrences into the existing cause rather than creating duplicates. Do NOT attempt to match code-issue failures against prior causes — those are not tracked.

A failure matches an existing cause when:
- For flaky tests: the failing test name matches `test_name` in a prior cause, OR the error message/stack trace substantially matches the `error_pattern`
- For infra failures: the error message substantially matches the `error_pattern` of a prior infra-failure cause

When reusing an existing cause, keep the same `id`, `type`, `title`, `test_name`, and `error_pattern` fields (you may improve the `title` or `error_pattern` if the new failure provides better detail). Also add the cause ID to the `causes` array in the run summary.

### Step 3: Write the analysis JSON files

Write two types of files:

#### 3a. Run summary file

Write the run summary to `/tmp/gh-aw/agent/analysis-result.json`. The JSON must follow this schema:

```json
{
  "run_id": 12345,
  "run_attempt": 1,
  "run_url": "https://github.com/microsoft/aspire/actions/runs/12345",
  "analyzed_at": "2026-06-30T12:00:00Z",
  "verdict": "transient-infra | flaky-test | code-issue | mixed",
  "pr": {
    "number": 1234,
    "title": "PR title",
    "author": "username",
    "state": "open",
    "head_branch": "feature-branch",
    "base_branch": "main",
    "url": "https://github.com/microsoft/aspire/pull/1234"
  },
  "failed_jobs": [
    {
      "name": "Build and Test (ubuntu-latest)",
      "id": 67890,
      "conclusion": "failure",
      "url": "https://github.com/microsoft/aspire/actions/runs/12345/job/67890",
      "classification": "transient-infra | flaky-test | code-issue",
      "reason": "Brief explanation of why this job failed",
      "failed_steps": ["step1", "step2"]
    }
  ],
  "failed_tests": [
    {
      "name": "Fully.Qualified.TestName",
      "job": "job-name",
      "error": "the error message from the test failure",
      "stack_trace": "the stack trace from the test failure (first few frames)",
      "classification": "flaky | code-issue",
      "reason": "Why this test is classified this way"
    }
  ],
  "causes": ["cause-id-1", "cause-id-2"]
}
```

Field details:
- `verdict`: The overall classification. Use `"transient-infra"` if ALL failures are infrastructure issues, `"flaky-test"` if ANY failures are flaky tests (and none are code issues), `"code-issue"` if ANY failures are caused by PR changes, or `"mixed"` if there are both transient and non-transient failures.
- `failed_jobs[].classification`: Per-job classification — one of `"transient-infra"`, `"flaky-test"`, or `"code-issue"`.
- `failed_tests[].classification`: Per-test classification — `"flaky"` or `"code-issue"`.
- `failed_tests[].error`: The full error message from the TRX test failure data.
- `failed_tests[].stack_trace`: The stack trace from the TRX test failure data (include the first few relevant frames).
- `analyzed_at`: The current UTC timestamp in ISO 8601 format.
- `causes`: An array of cause IDs (strings) that were identified for this run. These correspond to the cause files written in Step 3b. The publish job uses this to add an occurrence entry to each referenced cause. Empty array `[]` for code-issue verdicts.

#### 3b. Per-cause files (flaky-test and infra-failure only)

For each distinct underlying cause that is NOT a code-issue, write a separate JSON file to `/tmp/gh-aw/agent/causes/<cause-id>.json`. The `<cause-id>` should be a filesystem-safe identifier derived from the cause (e.g., sanitized test name for flaky tests, or a short descriptive slug for infrastructure issues). Do NOT create cause files for code-issue classifications — those are the PR author's responsibility and are not tracked as recurring CI problems.

Each cause file must follow this schema:

```json
{
  "id": "cause-id",
  "type": "flaky-test | infra-failure",
  "title": "Human-readable short description of the cause",
  "test_name": "Fully.Qualified.TestName (only for flaky-test with a specific test)",
  "error_pattern": "The key error message or pattern that identifies this cause"
}
```

Field details:
- `id`: Must match the filename (without `.json`). Use lowercase with hyphens. For flaky tests, derive from the test name (e.g., `aspire-hosting-tests-mytest`). For infra failures, use a descriptive slug (e.g., `nuget-feed-timeout`, `docker-registry-rate-limit`).
- `type`: One of `"flaky-test"` or `"infra-failure"`. Do NOT create cause files for code-issue classifications.
- `title`: A brief human-readable description (e.g., "Flaky: MyNamespace.MyTest times out intermittently", "NuGet feed connection timeout").
- `test_name`: The fully qualified test name. Omit this field for infrastructure failures that aren't test-specific.
- `error_pattern`: The actual error message and relevant stack trace from the failure. For flaky tests, use the error message and first few stack trace frames from the TRX data. For infra failures, use the error text from the job logs. Include enough detail to identify and reproduce the issue (up to ~500 characters).

Do NOT include an `occurrences` field — the publish job builds occurrences automatically from the run summary JSON.

Create the `/tmp/gh-aw/agent/causes/` directory and write one `.json` file per distinct cause. Multiple failed tests with the same root cause (e.g., same infrastructure error) can be grouped into a single cause file. When a failure matches an existing prior cause, use the same filename (`<cause-id>.json`) so the publish job merges correctly.

### Step 4: Take action

Determine the overall verdict and proceed to the **Actions** section.

## Input Data

The file `ci-failure-data/analysis-summary.md` contains the full failure data:
- The failed workflow run information
- PR metadata (number, title, author, state, branch)
- Failed jobs and their failed steps
- Job logs (error-focused extracts)
- Job annotations
- Test failures extracted from TRX artifacts (test name and error message)
- PR changed files
- Known transient failure patterns from `eng/test-retry-patterns.json`
- **Prior causes** from the memory branch (previously identified recurring failures with their IDs and occurrence history)

## Classification Rules

Classify each failed job into one of these categories:

### 1. Transient Infrastructure Failure

The failure was caused by infrastructure issues outside the PR author's control. Indicators:
- Network errors: `ECONNRESET`, `ECONNREFUSED`, `ENOTFOUND`, `Could not resolve host`, `Connection reset by peer`
- SSL/TLS failures: `The SSL connection could not be established`
- Timeout errors not caused by test code: `Operation timed out`, `A connection attempt failed`
- Container registry rate limiting: `403 Forbidden` from `mcr.microsoft.com`, `The request is blocked`
- GitHub runner issues: `The job was not acquired by Runner`, `The hosted runner lost communication`
- NuGet feed failures: errors from `pkgs.dev.azure.com/dnceng` or `dnceng.pkgs.visualstudio.com`
- Git operation failures: `expected 'packfile'`, `RPC failed`, `Recv failure`
- Windows process init: `0xC0000142`, exit code `-1073741502`
- Steps like "Set up job", "Checkout code", "Set up .NET Core" failing with transient errors

### 2. Transient Test Failure (Flaky Test)

A test failed, but the failure is NOT related to PR changes. Indicators:
- The test failure message matches a known transient pattern from `eng/test-retry-patterns.json`
- The failing test is in a code area NOT modified by the PR (check the PR changed files)
- The failure shows intermittent/timing-related errors (race conditions, port conflicts, timeout in integration tests)
- The test name or namespace does not correspond to any file changed in the PR
- The error message shows environmental issues (Docker connectivity, service availability, port already in use)

### 3. Non-Transient Failure (PR Code Issue)

The failure was directly caused by changes in the PR. Indicators:
- **Build/compilation errors**: `error CS`, `error MSB`, `Build FAILED`, syntax errors in files changed by the PR
- **Test failures in PR-modified code**: test assertions fail in tests that test functionality changed by the PR
- **New test failures**: tests that previously passed now fail due to behavioral changes from the PR
- **API compatibility failures**: public API surface changes that break compatibility
- **Lint/format errors**: code style violations in PR-changed files

## Analysis Process

1. Read `ci-failure-data/analysis-summary.md`
2. For each failed job, examine:
   - The failed step names
   - The job log output for error messages
   - The job annotations
3. Cross-reference failures against:
   - The known transient failure patterns
   - The PR changed files list
4. Classify each failed job
5. Determine the overall verdict and proceed to **Actions**

## Actions

After writing the JSON files (summary + per-cause), take action based on the verdict:

### If ALL failures are Transient Infrastructure Failures:

Set `verdict` to `"transient-infra"` in the JSON. Check the `ENABLE_RERUN` environment variable (set in the workflow `env:` block).

**If `ENABLE_RERUN` is `'true'`:** Emit the `rerun-failed-jobs` safe output to rerun the failed CI jobs.

**Regardless of `ENABLE_RERUN`:** Emit the `publish-data` safe output so the analysis is pushed to the memory branch and a PR comment is posted.

### If ANY failures are Transient Test Failures (Flaky Tests):

Set `verdict` to `"flaky-test"` in the JSON. Ensure `failed_tests` entries have `classification: "flaky"` and include a `reason` explaining why the test is likely flaky.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

### If ANY failures are Non-Transient (PR Code Issues):

Set `verdict` to `"code-issue"` in the JSON. Ensure `failed_jobs` entries have `classification: "code-issue"` with a clear `reason` linking the error to PR changes.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

### Mixed Failures

If there are both transient and non-transient failures, set `verdict` to `"mixed"`. Report all findings with per-job and per-test classifications.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

## Important Rules

1. **Always write the run summary** — every analysis must produce `/tmp/gh-aw/agent/analysis-result.json`. Write cause files in `/tmp/gh-aw/agent/causes/` only for `flaky-test` and `infra-failure` causes (NOT for `code-issue`).
2. **Always emit the `publish-data` safe output** — with `run_id` and `pr_numbers` so the publish-data job can push the data and post a comment.
3. **Never rerun when there are code issues** — only emit `rerun-failed-jobs` for pure infrastructure failures with `ENABLE_RERUN` set to `'true'`.
4. **Be specific** — include actual error messages and job/test names in the JSON fields.
5. **Cross-reference PR files** — always check whether the failing test is in an area modified by the PR.
6. **PR must not be locked** — check the PR state from the "Pull Request" section in the summary file. If the PR is locked, skip the analysis and call `noop`. Still analyze and comment on closed PRs.
7. **Do NOT use MCP to query GitHub** — all needed data (PR metadata, changed files, job logs, annotations) is already in the summary file. No GitHub API tools are available.
8. **Do NOT post PR comments directly** — the `publish-data` job handles commenting using the JSON file. Do not use `add-comment`.
