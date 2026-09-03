---
description: |
  Analyzes failed CI builds using Copilot to determine whether the failure is
  transient (flaky test, infrastructure issue), caused by pull request changes,
  or a repository break on main. Pull request failures are reported on the PR;
  main repository breaks create a dedicated issue.

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
      run_scope: ${{ steps.collect.outputs.run_scope }}
      pr_numbers: ${{ steps.collect.outputs.pr_numbers }}
    env:
      GH_TOKEN: ${{ github.token }}
    steps:
      - name: Checkout data collection helpers
        uses: actions/checkout@v4.3.1
        with:
          sparse-checkout: |
            eng/test-retry-patterns.json
            .github/workflows/analyze-ci-failure-history.sh
            .github/workflows/analyze-ci-failure-candidates.sh
          sparse-checkout-cone-mode: false
      - name: Collect CI failure data
        id: collect
        env:
          REPO: ${{ github.repository }}
          MANUAL_RUN_ID: ${{ inputs.run_id }}
          WORKFLOW_RUN_ID: ${{ github.event.workflow_run.id }}
          WORKFLOW_RUN_ATTEMPT: ${{ github.event.workflow_run.run_attempt }}
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

          # A workflow_run can wait behind another analysis, during which the source run may
          # be rerun. Pin that event to its immutable attempt; manual dispatch intentionally
          # analyzes the latest attempt.
          if [ "${EVENT_NAME}" = "workflow_dispatch" ]; then
            RUN_METADATA_ENDPOINT="repos/${REPO}/actions/runs/${RUN_ID}"
          else
            if ! [[ "${WORKFLOW_RUN_ATTEMPT}" =~ ^[1-9][0-9]*$ ]]; then
              echo "::error::The workflow_run event did not provide a valid run attempt"
              exit 1
            fi
            RUN_METADATA_ENDPOINT="repos/${REPO}/actions/runs/${RUN_ID}/attempts/${WORKFLOW_RUN_ATTEMPT}"
          fi
          gh api "${RUN_METADATA_ENDPOINT}" > ci-failure-data/run.json

          RUN_ATTEMPT=$(jq -r '.run_attempt // 1' ci-failure-data/run.json)
          RUN_STARTED_AT=$(jq -r '.run_started_at // ""' ci-failure-data/run.json)
          RUN_UPDATED_AT=$(jq -r '.updated_at // ""' ci-failure-data/run.json)
          RUN_EVENT=$(jq -r '.event // ""' ci-failure-data/run.json)
          RUN_WORKFLOW_PATH=$(jq -r '.path // ""' ci-failure-data/run.json)
          HEAD_SHA=$(jq -r '.head_sha // ""' ci-failure-data/run.json)
          HEAD_BRANCH=$(jq -r '.head_branch // ""' ci-failure-data/run.json)
          RUN_URL=$(jq -r '.html_url // ""' ci-failure-data/run.json)
          CONCLUSION=$(jq -r '.conclusion // ""' ci-failure-data/run.json)
          if [ "$RUN_WORKFLOW_PATH" != ".github/workflows/ci.yml" ]; then
            echo "::error::Run ${RUN_ID} belongs to workflow '${RUN_WORKFLOW_PATH}', not '.github/workflows/ci.yml'"
            exit 1
          fi
          case "${RUN_EVENT}:${HEAD_BRANCH}" in
            push:main)
              RUN_SCOPE="main"
              ;;
            pull_request:*|pull_request_target:*)
              RUN_SCOPE="pull-request"
              ;;
            *)
              echo "::notice::Unsupported run scope: event=${RUN_EVENT}, branch=${HEAD_BRANCH}. Skipping analysis."
              echo "has_work=false" >> "$GITHUB_OUTPUT"
              exit 0
              ;;
          esac
          echo "run_attempt=${RUN_ATTEMPT}" >> "$GITHUB_OUTPUT"
          echo "head_sha=${HEAD_SHA}" >> "$GITHUB_OUTPUT"
          echo "run_url=${RUN_URL}" >> "$GITHUB_OUTPUT"
          echo "run_scope=${RUN_SCOPE}" >> "$GITHUB_OUTPUT"

          # Skip analysis if the run succeeded (e.g. manual dispatch on a passing run)
          if [ "${CONCLUSION}" = "success" ]; then
            echo "Run concluded with success. Nothing to analyze."
            echo "has_work=false" >> "$GITHUB_OUTPUT"
            exit 0
          fi

          PR_NUMBERS=""
          if [ "${RUN_SCOPE}" = "pull-request" ]; then
            # Workflow metadata can include pull requests from forks that happen
            # to reference this commit, so only accept PRs targeting this repository.
            PR_NUMBERS=$(jq -r --arg repo_url "https://api.github.com/repos/${REPO}" \
              '[.pull_requests[]? | select(.base.repo.url == $repo_url) | .number] | join(",")' \
              ci-failure-data/run.json)
            if [ -z "${PR_NUMBERS}" ]; then
              HEAD_OWNER=$(jq -r '.head_repository.owner.login // ""' ci-failure-data/run.json)
              if [ -n "${HEAD_OWNER}" ] && [ -n "${HEAD_BRANCH}" ]; then
                # Branch names may contain '&' and '=', so pass state/head as
                # separate -f fields rather than concatenating a query string;
                # gh api URL-encodes -f values, preventing query injection.
                PR_NUMBERS=$(gh api --method GET "repos/${REPO}/pulls" \
                  -f state=open \
                  -f "head=${HEAD_OWNER}:${HEAD_BRANCH}" \
                  --jq '[.[].number] | join(",")' 2>/dev/null || echo "")
              fi
            fi
            if [ -z "${PR_NUMBERS}" ] && [ -n "${HEAD_SHA}" ]; then
              PR_NUMBERS=$(gh api "repos/${REPO}/commits/${HEAD_SHA}/pulls" \
                --jq "[.[] | select(.base.repo.full_name == \"${REPO}\") | .number] | join(\",\")" \
                2>/dev/null || echo "")
            fi

            if [ -z "${PR_NUMBERS}" ]; then
              echo "No associated PR found. Analysis will proceed without PR context."
            fi
          else
            # The PR associated with the failed head commit identifies the merge
            # that triggered this run. It is context only and is not presumed causal.
            gh api "repos/${REPO}/commits/${HEAD_SHA}/pulls" \
              --jq "[.[] | select(.base.repo.full_name == \"${REPO}\" and .base.ref == \"main\" and .merged_at != null)] | first // {}" \
              > ci-failure-data/triggering-merge-pr.json 2>/dev/null \
              || echo "{}" > ci-failure-data/triggering-merge-pr.json

            WORKFLOW_ID=$(jq -r '.workflow_id' ci-failure-data/run.json)
            RUN_CREATED_AT=$(jq -r '.created_at' ci-failure-data/run.json)
            if ! bash .github/workflows/analyze-ci-failure-history.sh \
                "$REPO" "$WORKFLOW_ID" "$RUN_CREATED_AT" \
                ci-failure-data/last-successful-main-run.json; then
              echo "::warning::Unable to find the last successful main run. Continuing without a candidate merge range."
              echo "{}" > ci-failure-data/last-successful-main-run.json
            fi

            LAST_SUCCESSFUL_SHA=$(jq -r '.head_sha // ""' ci-failure-data/last-successful-main-run.json)
            bash .github/workflows/analyze-ci-failure-candidates.sh \
              "$REPO" "$LAST_SUCCESSFUL_SHA" "$HEAD_SHA" \
              ci-failure-data/candidate-merges.json \
              ci-failure-data/candidate-merge-history-status.json
          fi
          echo "pr_numbers=${PR_NUMBERS}" >> "$GITHUB_OUTPUT"

          jq -n \
            --argjson run_id "${RUN_ID}" \
            --argjson run_attempt "${RUN_ATTEMPT}" \
            --arg event "${RUN_EVENT}" \
            --arg head_branch "${HEAD_BRANCH}" \
            --arg head_sha "${HEAD_SHA}" \
            --arg run_scope "${RUN_SCOPE}" \
            --arg pr_numbers "${PR_NUMBERS}" \
            '{
              run_id: $run_id,
              run_attempt: $run_attempt,
              event: $event,
              head_branch: $head_branch,
              head_sha: $head_sha,
              run_scope: $run_scope,
              pr_numbers: $pr_numbers
            }' > ci-failure-data/run-context.json

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

          # Artifact listings are run-scoped and can contain same-named artifacts from
          # multiple attempts. The attempt metadata bounds the upload window, and downloading
          # by artifact ID prevents gh from choosing a same-named artifact from another attempt.
          ARTIFACTS_FILE="ci-failure-data/artifacts.json"
          if ! gh api --paginate "repos/${REPO}/actions/runs/${RUN_ID}/artifacts" \
              --jq '.artifacts[]' | jq -s '.' > "${ARTIFACTS_FILE}"; then
            echo "Warning: Failed to list test results artifacts"
            echo "[]" > "${ARTIFACTS_FILE}"
          fi
          ARTIFACT_ID=$(jq -r \
            --arg started_at "${RUN_STARTED_AT}" \
            --arg updated_at "${RUN_UPDATED_AT}" \
            '[
              .[] |
              select(
                (.expired == false) and
                ((.name | type) == "string") and
                (.name | test("test-results|TestResults"; "i")) and
                ((.created_at | type) == "string") and
                (.created_at >= $started_at and .created_at <= $updated_at))
            ] | sort_by([.created_at, .id]) | last | .id // empty' \
            "${ARTIFACTS_FILE}")
          if [ -n "${ARTIFACT_ID}" ]; then
            ARTIFACT_NAME=$(jq -r \
              --argjson artifact_id "${ARTIFACT_ID}" \
              '[.[] | select(.id == $artifact_id)] | first | .name // empty' \
              "${ARTIFACTS_FILE}")
            ARTIFACT_ZIP="ci-failure-data/test-results.zip"
            echo "Downloading test results artifact: ${ARTIFACT_NAME} (${ARTIFACT_ID})..."
            mkdir -p ci-failure-data/test-results
            if gh api "repos/${REPO}/actions/artifacts/${ARTIFACT_ID}/zip" > "${ARTIFACT_ZIP}" 2>/dev/null &&
                unzip -q "${ARTIFACT_ZIP}" -d ci-failure-data/test-results; then
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
            rm -f "${ARTIFACT_ZIP}"
          else
            echo "No test results artifact found for run ${RUN_ID} attempt ${RUN_ATTEMPT}"
          fi

          echo "Data collection complete."

      - name: Create analysis summary
        if: steps.collect.outputs.has_work == 'true'
        env:
          RUN_ID: ${{ steps.collect.outputs.run_id }}
          RUN_ATTEMPT: ${{ steps.collect.outputs.run_attempt }}
          RUN_URL: ${{ steps.collect.outputs.run_url }}
          RUN_SCOPE: ${{ steps.collect.outputs.run_scope }}
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
            echo "- **Run Scope**: ${RUN_SCOPE}"
            jq -r '"- **Event**: \(.event)\n- **Branch**: \(.head_branch)\n- **Failed SHA**: \(.head_sha)"' \
              ci-failure-data/run-context.json
            if [ "${RUN_SCOPE}" = "pull-request" ]; then
              echo "- **Associated PRs**: ${PR_NUMBERS}"
            fi
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

            if [ "${RUN_SCOPE}" = "pull-request" ]; then
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
            else
              echo "## Main Branch Context"
              echo ""
              jq -r '"- **Last successful main run**: " + (if .id then "[\(.id)](\(.html_url)) at `\(.head_sha)`" else "Not found" end)' \
                ci-failure-data/last-successful-main-run.json
              jq -r '"- **Triggering merge PR (context only, not necessarily causal)**: " + (if .number then "#\(.number) \(.title) (\(.html_url))" else "Not found" end)' \
                ci-failure-data/triggering-merge-pr.json
              echo ""
              echo "### Candidate merges since the last successful main run"
              echo ""
              CANDIDATE_HISTORY_STATE=$(jq -r '.state // "unavailable"' ci-failure-data/candidate-merge-history-status.json)
              case "$CANDIDATE_HISTORY_STATE" in
                unavailable)
                  echo "Candidate merge history is unavailable."
                  ;;
                incomplete)
                  echo "Candidate merge history is incomplete."
                  ;;
                available)
                  if [ "$(jq 'length' ci-failure-data/candidate-merges.json)" -eq 0 ]; then
                    echo "No candidate merges found."
                  fi
                  ;;
              esac
              if [ "$(jq 'length' ci-failure-data/candidate-merges.json)" -gt 0 ]; then
                jq -r '.[] | "- #\(.pull_request.number) \(.pull_request.title) (\(.pull_request.url)) — `\(.sha)`"' \
                  ci-failure-data/candidate-merges.json
              fi
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

      - uses: actions/upload-artifact@v4.6.2
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

# Publication performs two ordinary memory pushes around issue side effects, so serialize every
# analysis. The maximum queue preserves pending work that the default single queue would replace.
concurrency:
  group: analyze-ci-failure
  cancel-in-progress: false
  queue: max

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
        Publishes the CI failure analysis to the memory branch, then posts a PR
        comment or updates a main-breakage issue according to the trusted scope.
        The agent must write:
          - /tmp/gh-aw/agent/analysis-result.json (run summary)
          - /tmp/gh-aw/agent/causes/*.json (one file per failure cause)
        Emit exactly one `publish_data` item with run_id and pr_numbers.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        actions: read
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
        - name: Checkout publication helpers
          uses: actions/checkout@v4.3.1
          with:
            persist-credentials: false
            sparse-checkout: |
              .github/workflows/analyze-ci-failure-validation.sh
              .github/workflows/analyze-ci-failure-persistence.sh
              .github/workflows/analyze-ci-failure-comment.sh
              .github/workflows/analyze-ci-failure-issue.sh
            sparse-checkout-cone-mode: false
        - uses: actions/download-artifact@v4
          with:
            name: ci-failure-data
            path: ci-failure-data/
        - name: Validate analysis scope
          run: bash .github/workflows/analyze-ci-failure-validation.sh
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

            RUN_CONTEXT_FILE="ci-failure-data/run-context.json"
            TRUSTED_FAILED_JOBS_FILE="ci-failure-data/failed-jobs.json"
            TRUSTED_RUN_ID=$(jq -r '.run_id' "$RUN_CONTEXT_FILE")
            TRUSTED_RUN_SCOPE=$(jq -r '.run_scope' "$RUN_CONTEXT_FILE")
            VERDICT=$(jq -r '.verdict' "$ANALYSIS_FILE")

            REPO="${{ github.repository }}"
            MEMORY_BRANCH="memory/ci-failure-analysis"

            # Read fields from the analysis JSON
            RUN_ID="$TRUSTED_RUN_ID"
            RUN_SCOPE="$TRUSTED_RUN_SCOPE"
            RUN_URL=$(jq -r '.html_url // ""' ci-failure-data/run.json)
            ANALYZED_AT=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
            PR_NUMBER=$(bash .github/workflows/analyze-ci-failure-persistence.sh pr-number)

            # ── 1. Set up memory branch and merge cause data ──
            # Pull request code issues are handled on the PR and do not need
            # stable cause records. Main repository breakages are persisted.
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
              bash .github/workflows/analyze-ci-failure-persistence.sh write-run-summary \
                "$ANALYSIS_FILE" "memory-repo/runs/${RUN_ID}.json" "$ANALYZED_AT"

              # Store individual cause files under causes/ (shared across runs).
              # Each cause file accumulates occurrences over time. The agent
              # writes cause definitions (no occurrences); we build the occurrence
              # from the run summary and merge it into the stored cause file.
              if [ -d "$CAUSES_DIR" ]; then
                mkdir -p "memory-repo/causes"

                # Build the occurrence entry from the run summary JSON
                for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
                  [ -f "$CAUSE_FILE" ] || continue
                  CAUSE_BASENAME=$(basename "$CAUSE_FILE")
                  CAUSE_TYPE=$(jq -r '.type' "$CAUSE_FILE")
                  EXISTING="memory-repo/causes/${CAUSE_BASENAME}"
                  CAUSE_JOBS=$(bash .github/workflows/analyze-ci-failure-persistence.sh \
                    cause-job-names "$CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" display)

                  # Add an occurrences array with this run's entry to the agent's cause file
                  CAUSE_WITH_OCC=$(bash .github/workflows/analyze-ci-failure-persistence.sh add-occurrence \
                    "$CAUSE_FILE" "$RUN_ID" "$RUN_URL" "$CAUSE_JOBS" "$ANALYZED_AT" |
                    jq 'del(.job_ids, .job_names)')

                  if [ -f "$EXISTING" ]; then
                    CURRENT_CAUSE_TYPE=$(jq -r '.type // ""' "$EXISTING")
                    if [ "$CURRENT_CAUSE_TYPE" != "$CAUSE_TYPE" ]; then
                      echo "::error::Stored cause ${CAUSE_BASENAME} cannot change type from '${CURRENT_CAUSE_TYPE}' to '${CAUSE_TYPE}'"
                      exit 1
                    fi
                    # Merge: append new occurrence, deduplicate by run_id
                    echo "$CAUSE_WITH_OCC" | jq -s --slurpfile existing "$EXISTING" '
                      .[0] as $new | $existing[0] as $ex |
                      ($ex | del(.job_ids, .job_names)) *
                      ($new | del(.occurrences, .issue_url, .job_ids, .job_names)) * {
                        occurrences: (
                          [$ex.occurrences[], $new.occurrences[]]
                          | unique_by(.run_id)
                          | sort_by(.observed_at)
                        )
                      } * (if $ex.issue_url then {issue_url: $ex.issue_url} else {} end)
                    ' > "${EXISTING}.tmp" && mv "${EXISTING}.tmp" "$EXISTING"
                  else
                    echo "$CAUSE_WITH_OCC" > "$EXISTING"
                  fi
                done
                CAUSE_COUNT=$(find "memory-repo/causes" -name '*.json' -type f 2>/dev/null | wc -l)
                echo "Persisted cause files to causes/ (${CAUSE_COUNT} total)"
              fi

              # Push the validated cause identities before issue side effects. A
              # concurrent publisher that cloned stale memory will fail here
              # instead of creating or updating an issue for a conflicting type.
              git -C memory-repo add -A
              if git -C memory-repo diff --cached --quiet; then
                echo "No initial changes to memory branch"
              else
                git -C memory-repo commit -m "Add CI failure analysis for run ${RUN_ID}"
                git -C memory-repo push origin "HEAD:$MEMORY_BRANCH"
                echo "Memory branch updated with analysis for run ${RUN_ID}"
              fi

            # ── 2. Create or update issues for each cause ──
            if [ -d "$CAUSES_DIR" ]; then
              # Build occurrence info from the run summary for issue updates
              # Build the occurrence table row for this run
              OCC_DATE=$(echo "$ANALYZED_AT" | cut -dT -f1)
              if [ "$RUN_SCOPE" = "main" ]; then
                OCCURRENCE_CONTEXT="main"
              elif [ "$PR_NUMBER" = "0" ]; then
                OCCURRENCE_CONTEXT="unavailable"
              else
                OCCURRENCE_CONTEXT="#${PR_NUMBER}"
              fi
              for CAUSE_FILE in "$CAUSES_DIR"/*.json; do
                [ -f "$CAUSE_FILE" ] || continue

                CAUSE_ID=$(jq -r '.id' "$CAUSE_FILE")

                CAUSE_TYPE=$(jq -r '.type' "$CAUSE_FILE")
                CAUSE_JOBS=$(bash .github/workflows/analyze-ci-failure-persistence.sh \
                  cause-job-names "$CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" display)
                CAUSE_JOBS_TABLE=$(bash .github/workflows/analyze-ci-failure-persistence.sh \
                  cause-job-names "$CAUSE_FILE" "$TRUSTED_FAILED_JOBS_FILE" table)
                NEW_OCCURRENCE_ROW="| ${OCC_DATE} | [${RUN_ID}](${RUN_URL}) | ${CAUSE_JOBS_TABLE} | ${OCCURRENCE_CONTEXT} |"

                CAUSE_STORED="memory-repo/causes/${CAUSE_ID}.json"
                MARKER="<!-- ci-failure-cause:${CAUSE_ID} -->"
                TYPE_MARKER="<!-- ci-failure-cause-type:${CAUSE_TYPE} -->"

                # Check if the stored cause file already has a linked issue
                EXISTING_ISSUE=""
                if [ -f "$CAUSE_STORED" ]; then
                  STORED_ISSUE_URL=$(jq -r '.issue_url // empty' "$CAUSE_STORED")
                  if [ -n "$STORED_ISSUE_URL" ]; then
                    if [[ "$STORED_ISSUE_URL" =~ ^https://github\.com/${REPO}/issues/([0-9]+)$ ]]; then
                      EXISTING_ISSUE="${BASH_REMATCH[1]}"
                      ISSUE_JSON=$(gh api "repos/${REPO}/issues/${EXISTING_ISSUE}" 2>/dev/null || echo "")
                      if [ -n "$ISSUE_JSON" ] && jq -e \
                          --arg marker "$MARKER" \
                          --arg type_marker "$TYPE_MARKER" \
                          --arg cause_type "$CAUSE_TYPE" '
                          (.body // "" | split("\n") | map(rtrimstr("\r"))) as $lines |
                          (.pull_request == null) and
                          any(.labels[]?; .name == "ci-failure-cause") and
                          ($lines[0] == $marker) and
                          (
                            ($lines[1] == $type_marker) or
                            ([$lines[] | select(startswith("**Type**: "))] == ["**Type**: " + $cause_type])
                          )
                        ' <<< "$ISSUE_JSON" >/dev/null; then
                        ISSUE_STATE=$(jq -r '.state' <<< "$ISSUE_JSON")
                      else
                        echo "Linked issue #${EXISTING_ISSUE} does not match cause ${CAUSE_ID}, will search by marker"
                        EXISTING_ISSUE=""
                      fi
                    else
                      echo "Stored issue URL is not a canonical ${REPO} issue URL, will search by marker"
                      EXISTING_ISSUE=""
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

                  EXISTING_ISSUE=$(jq -r \
                    --arg marker "$MARKER" \
                    --arg type_marker "$TYPE_MARKER" \
                    --arg cause_type "$CAUSE_TYPE" '
                      .[] |
                      (.body // "" | split("\n") | map(rtrimstr("\r"))) as $lines |
                      select(
                        ($lines[0] == $marker) and
                        (
                          ($lines[1] == $type_marker) or
                          ([$lines[] | select(startswith("**Type**: "))] == ["**Type**: " + $cause_type])
                        )
                      ) |
                      .number
                    ' \
                    "$OPEN_ISSUES_CACHE" | head -1 || true)

                  if [ -z "$EXISTING_ISSUE" ]; then
                    EXISTING_ISSUE=$(jq -r \
                      --arg marker "$MARKER" \
                      --arg type_marker "$TYPE_MARKER" \
                      --arg cause_type "$CAUSE_TYPE" '
                        .[] |
                        (.body // "" | split("\n") | map(rtrimstr("\r"))) as $lines |
                        select(
                          ($lines[0] == $marker) and
                          (
                            ($lines[1] == $type_marker) or
                            ([$lines[] | select(startswith("**Type**: "))] == ["**Type**: " + $cause_type])
                          )
                        ) |
                        .number
                      ' \
                      "$CLOSED_ISSUES_CACHE" | head -1 || true)
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
                  ISSUE_METADATA_FILE=$(mktemp)
                  bash .github/workflows/analyze-ci-failure-issue.sh \
                    "$CAUSE_FILE" "$RUN_CONTEXT_FILE" \
                    ci-failure-data/last-successful-main-run.json \
                    ci-failure-data/triggering-merge-pr.json \
                    "$RUN_URL" "$RUN_SCOPE" "$PR_NUMBER" "$CAUSE_JOBS" \
                    "$NEW_OCCURRENCE_ROW" "$BODY_FILE" "$ISSUE_METADATA_FILE"

                  if [ "$CAUSE_TYPE" = "main-repository-breakage" ]; then
                    gh label create "main-ci-break" --repo "$REPO" \
                      --color "b60205" \
                      --description "Deterministic repository breakage on the main branch" \
                      --force
                  fi

                  ISSUE_TITLE=$(jq -r '.title' "$ISSUE_METADATA_FILE")
                  LABELS=$(jq -r '.labels' "$ISSUE_METADATA_FILE")
                  CREATED_ISSUE_URL=$(gh issue create --repo "$REPO" \
                    --title "$ISSUE_TITLE" \
                    --label "$LABELS" \
                    --body-file "$BODY_FILE")
                  rm -f "$BODY_FILE" "$ISSUE_METADATA_FILE"
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

              # ── 3. Push issue links to the memory branch ──
              git -C memory-repo add -A
              if git -C memory-repo diff --cached --quiet; then
                echo "No issue-link changes to memory branch"
              else
                git -C memory-repo commit -m "Link CI failure issues for run ${RUN_ID}"
                git -C memory-repo push origin "HEAD:$MEMORY_BRANCH"
                echo "Memory branch updated with issue links for run ${RUN_ID}"
              fi
            fi

        - name: Comment on PR
          run: |
            set -euo pipefail

            OUTPUT_FILE="$GH_AW_AGENT_OUTPUT"
            ANALYSIS_FILE="$(dirname "$OUTPUT_FILE")/agent/analysis-result.json"
            RUN_CONTEXT_FILE="ci-failure-data/run-context.json"
            TRUSTED_FAILED_JOBS_FILE="ci-failure-data/failed-jobs.json"
            RUN_SCOPE=$(jq -r '.run_scope' "$RUN_CONTEXT_FILE")
            RUN_URL=$(jq -r '.html_url // ""' ci-failure-data/run.json)
            PR_NUMBERS=$(jq -r '.pr_numbers' "$RUN_CONTEXT_FILE")
            REPO="${{ github.repository }}"

            # ── 4. Post PR comment using the analysis JSON ──
            if [ "$RUN_SCOPE" = "main" ]; then
              echo "Main run analysis is reported through cause issues, not PR comments."
              exit 0
            fi

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
            bash .github/workflows/analyze-ci-failure-comment.sh \
              "$ANALYSIS_FILE" "$TRUSTED_FAILED_JOBS_FILE" "$RUN_URL" > "$COMMENT_FILE"

            # Update an existing analysis comment if one exists (by marker),
            # otherwise create a new one. This prevents stacking duplicate
            # comments on PRs with repeated CI failures.
            MARKER="<!-- analyze-ci-failure -->"
            EXISTING_COMMENT_ID=$(gh api "repos/${REPO}/issues/${FIRST_PR}/comments" --paginate \
              --jq ".[] | select(.user.login == \"github-actions[bot]\" and ((.body // \"\") | startswith(\"${MARKER}\\n\"))) | .id" \
              2>/dev/null | head -1 || true)

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
        - uses: actions/download-artifact@v4
          with:
            name: ci-failure-data
            path: ci-failure-data/
        - name: Rerun failed jobs
          uses: actions/github-script@v9.0.0
          env:
            ENABLE_RERUN: ${{ env.ENABLE_RERUN }}
          with:
            script: |
              const fs = require('fs');
              const path = require('path');

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

              const analysisFile = path.join(path.dirname(outputFile), 'agent', 'analysis-result.json');
              const causesDir = path.join(path.dirname(outputFile), 'agent', 'causes');
              const runContextFile = path.join('ci-failure-data', 'run-context.json');
              const trustedFailedJobsFile = path.join('ci-failure-data', 'failed-jobs.json');
              const priorCausesDir = path.join('ci-failure-data', 'prior-causes');
              if (!fs.existsSync(analysisFile) ||
                  !fs.existsSync(runContextFile) ||
                  !fs.existsSync(trustedFailedJobsFile)) {
                core.setFailed('Analysis result or trusted run data not found');
                return;
              }

              const analysis = JSON.parse(fs.readFileSync(analysisFile, 'utf8'));
              const runContext = JSON.parse(fs.readFileSync(runContextFile, 'utf8'));
              const trustedFailedJobs = JSON.parse(fs.readFileSync(trustedFailedJobsFile, 'utf8'));
              const owner = context.repo.owner;
              const repo = context.repo.repo;
              const requestedRunId = Number(item.run_id);
              const trustedRunId = Number(runContext.run_id);
              const trustedRunAttempt = Number(runContext.run_attempt);
              const trustedPrNumberText = String(runContext.pr_numbers || '');
              const trustedRunScope = String(runContext.run_scope || '');
              const reason = item.reason || '';
              const enableRerun = String(process.env.ENABLE_RERUN).toLowerCase() === 'true';

              if (!Number.isInteger(requestedRunId) || requestedRunId <= 0) {
                core.setFailed(`Invalid run_id: ${item.run_id}`);
                return;
              }
              if (!Number.isInteger(trustedRunId) || trustedRunId <= 0) {
                core.setFailed(`Invalid trusted run_id: ${runContext.run_id}`);
                return;
              }
              if (!Number.isInteger(trustedRunAttempt) || trustedRunAttempt <= 0) {
                core.setFailed(`Invalid trusted run attempt: ${runContext.run_attempt}`);
                return;
              }
              if (requestedRunId !== trustedRunId) {
                core.setFailed('Rerun request does not match trusted run context');
                return;
              }
              if (Number(analysis.run_id) !== trustedRunId ||
                  analysis.run_scope !== trustedRunScope ||
                  analysis.verdict !== 'transient-infra') {
                core.setFailed('Rerun requires a trusted transient-infra analysis for the same run');
                return;
              }
              if (trustedRunScope !== 'main' && trustedRunScope !== 'pull-request') {
                core.setFailed(`Unsupported trusted run scope: ${trustedRunScope}`);
                return;
              }
              if (!Array.isArray(analysis.failed_jobs) ||
                  analysis.failed_jobs.length === 0 ||
                  !analysis.failed_jobs.every(job => job && Number.isInteger(job.id)) ||
                  !analysis.failed_jobs.every(job => job && job.classification === 'transient-infra')) {
                core.setFailed('Rerun requires every failed job to be classified as transient-infra');
                return;
              }
              if (!Array.isArray(analysis.failed_tests) || analysis.failed_tests.length !== 0) {
                core.setFailed('Rerun requires a transient-infra analysis without failed tests');
                return;
              }
              if (!Array.isArray(trustedFailedJobs) ||
                  !trustedFailedJobs.every(job => job && Number.isInteger(job.id))) {
                core.setFailed('Trusted failed jobs are invalid');
                return;
              }
              const analysisJobIds = analysis.failed_jobs.map(job => job.id);
              const trustedJobIds = trustedFailedJobs.map(job => job.id);
              const analysisJobIdSet = new Set(analysisJobIds);
              const trustedJobIdSet = new Set(trustedJobIds);
              if (analysisJobIdSet.size !== analysisJobIds.length ||
                  analysisJobIdSet.size !== trustedJobIdSet.size ||
                  !analysisJobIds.every(jobId => trustedJobIdSet.has(jobId))) {
                core.setFailed('Analysis failed-job IDs do not match the trusted failed jobs');
                return;
              }

              const summaryCauseIds = Array.isArray(analysis.causes) ? analysis.causes : [];
              const causeFiles = fs.existsSync(causesDir)
                ? fs.readdirSync(causesDir).filter(fileName => fileName.endsWith('.json'))
                : [];
              if (summaryCauseIds.length === 0 ||
                  !summaryCauseIds.every(causeId => typeof causeId === 'string') ||
                  new Set(summaryCauseIds).size !== summaryCauseIds.length ||
                  causeFiles.length !== summaryCauseIds.length) {
                core.setFailed('Rerun requires unique analysis cause IDs matching the generated cause files');
                return;
              }
              const causeJobIdCoverage = new Set();
              for (const causeFileName of causeFiles) {
                let cause;
                try {
                  cause = JSON.parse(fs.readFileSync(path.join(causesDir, causeFileName), 'utf8'));
                } catch (error) {
                  core.setFailed(`Invalid JSON in rerun cause file ${causeFileName}: ${error.message}`);
                  return;
                }

                const causeId = String(cause.id || '');
                if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(causeId) ||
                    `${causeId}.json` !== causeFileName ||
                    cause.type !== 'infra-failure' ||
                    !summaryCauseIds.includes(causeId)) {
                  core.setFailed(`Rerun cause ${causeFileName} must be a valid infra-failure cause`);
                  return;
                }

                if (!Array.isArray(cause.job_ids) ||
                    cause.job_ids.length === 0 ||
                    !cause.job_ids.every(jobId => Number.isInteger(jobId) && jobId > 0) ||
                    new Set(cause.job_ids).size !== cause.job_ids.length ||
                    !cause.job_ids.every(jobId => trustedJobIdSet.has(jobId))) {
                  core.setFailed(`Rerun cause ${causeFileName} has invalid or untrusted job_ids`);
                  return;
                }
                for (const jobId of cause.job_ids) {
                  causeJobIdCoverage.add(jobId);
                }

                const priorCauseFile = path.join(priorCausesDir, causeFileName);
                if (fs.existsSync(priorCauseFile)) {
                  let priorCause;
                  try {
                    priorCause = JSON.parse(fs.readFileSync(priorCauseFile, 'utf8'));
                  } catch {
                    core.setFailed(`Invalid JSON in prior rerun cause file ${causeFileName}`);
                    return;
                  }
                  if (!priorCause || typeof priorCause !== 'object' || typeof priorCause.type !== 'string') {
                    core.setFailed(`Prior rerun cause ${causeFileName} must be an object with a string type`);
                    return;
                  }
                  if (priorCause.type !== cause.type) {
                    core.setFailed(`Rerun cause ${causeFileName} cannot change stored type from '${priorCause.type}' to '${cause.type}'`);
                    return;
                  }
                }
              }

              if (!analysisJobIds.every(jobId => causeJobIdCoverage.has(jobId))) {
                core.setFailed('Rerun cause job_ids do not cover every trusted failed job');
                return;
              }

              if (!enableRerun) {
                core.info(`Dry-run mode (ENABLE_RERUN is not 'true'). Would have rerun failed jobs for run ${trustedRunId}. Reason: ${reason}`);
                return;
              }

              if (trustedRunScope === 'pull-request') {
                const trustedPrNumbers = trustedPrNumberText.split(',').map(Number).filter(n => n > 0);
                let hasOpenPr = false;
                for (const prNumber of trustedPrNumbers) {
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
              }

              const { data: currentRun } = await github.rest.actions.getWorkflowRun({
                owner,
                repo,
                run_id: trustedRunId,
              });
              if (currentRun.run_attempt !== trustedRunAttempt) {
                core.warning(`Run ${trustedRunId} advanced from attempt ${trustedRunAttempt} to ${currentRun.run_attempt}. Skipping stale rerun request.`);
                return;
              }

              // Request rerun of failed jobs
              await github.rest.actions.reRunWorkflowFailedJobs({
                owner,
                repo,
                run_id: trustedRunId,
              });

              core.info(`Requested rerun of failed jobs for run ${trustedRunId}. Reason: ${reason}`);

steps:
  - uses: actions/download-artifact@v4.3.0
    with:
      name: ci-failure-data
      path: ci-failure-data/
---

# Analyze CI Failure

You are analyzing a failed CI build in the **microsoft/aspire** repository. Your job is to determine the root cause of the failure and take the appropriate action. The run scope in the summary was derived deterministically from the failed run's immutable `event` and `head_branch`; never infer or change it based on associated pull requests.

## Workflow

### Step 1: Read the summary file

Read `ci-failure-data/analysis-summary.md`. It contains the run information, PR metadata, failed jobs, error-focused logs, annotations, test failures, PR changed files, and known transient failure patterns.

### Step 2: Analyze

Analyze all of the data to classify each failed job (see **Classification Rules** below).

#### Matching against prior causes

When a failure is classified as `flaky-test`, `infra-failure`, or `main-repository-breakage` (NOT pull-request `code-issue`), check the **Prior Causes** section in the summary for a match. Prior causes are loaded from JSON files in the `ci-failure-data/prior-causes/` directory (one file per cause, e.g. `ci-failure-data/prior-causes/nuget-feed-timeout.json`). These files are fetched by the `collect-data` job from the `memory/ci-failure-analysis` branch's `causes/` directory and rendered into the summary under the "Prior Causes (from memory branch)" heading.

If any of this run's tracked failures match an existing cause, you MUST reuse that cause's `id` when writing the cause file in Step 3b. This allows the publish job to merge occurrences into the existing cause rather than creating duplicates. Do NOT attempt to match code-issue failures against prior causes — those are not tracked.

A failure matches an existing cause when:
- For flaky tests: the failing test name matches `test_name` in a prior cause, OR the error message/stack trace substantially matches the `error_pattern`
- For infra failures: the error message substantially matches the `error_pattern` of a prior infra-failure cause
- For main repository breakages: the deterministic failure substantially matches the `error_pattern` of a prior main-repository-breakage cause

When reusing an existing cause, keep the same `id`, `type`, `title`, `test_name`, and `error_pattern` fields (you may improve the `title` or `error_pattern` if the new failure provides better detail). Add the current run's `job_ids` as described below and add the cause ID to the `causes` array in the run summary.

### Step 3: Write the analysis JSON files

Write two types of files:

#### 3a. Run summary file

Write the run summary to `/tmp/gh-aw/agent/analysis-result.json`. The JSON must follow this schema:

```json
{
  "run_id": 12345,
  "run_attempt": 1,
  "run_url": "https://github.com/microsoft/aspire/actions/runs/12345",
  "run_scope": "main | pull-request",
  "analyzed_at": "2026-06-30T12:00:00Z",
  "verdict": "transient-infra | flaky-test | code-issue | main-repository-breakage | mixed",
  "pr": {
    "number": 1234,
    "title": "PR title",
    "author": "username",
    "state": "open",
    "head_branch": "feature-branch",
    "base_branch": "main",
    "url": "https://github.com/microsoft/aspire/pull/1234"
  },
  "triggering_merge_pr": null,
  "main_context": null,
  "failed_jobs": [
    {
      "name": "Build and Test (ubuntu-latest)",
      "id": 67890,
      "conclusion": "failure",
      "url": "https://github.com/microsoft/aspire/actions/runs/12345/job/67890",
      "classification": "transient-infra | flaky-test | code-issue | main-repository-breakage",
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
- `run_scope`: Copy the immutable run scope from the summary exactly.
- `verdict`: The overall classification. Use `"transient-infra"` when every failed job is an infrastructure issue, `"flaky-test"` when at least one failed job is a flaky test and every failed job is transient, `"code-issue"` when every failed job is caused by pull request changes, `"main-repository-breakage"` when every failed job is a deterministic repository failure on main, or `"mixed"` when transient and non-transient failures occur together.
- `pr`: For pull-request scope, include the subject PR object when the summary provides one; otherwise use `null`. For main scope, this MUST be `null`.
- `triggering_merge_pr`: For main scope, include the triggering merge PR from the summary when available. It is non-causal context and MUST NOT be copied to `pr`. For pull-request scope, this is `null`.
- `main_context`: For main scope, include `last_successful_main_sha`, `failed_sha`, and `candidate_merges` from the summary. For pull-request scope, this is `null`.
- `failed_jobs[].classification`: Per-job classification — one of `"transient-infra"`, `"flaky-test"`, `"code-issue"`, or `"main-repository-breakage"`.
- `failed_jobs` MUST contain exactly one object for every failed job in the summary, using its exact numeric ID, with no additions, omissions, or duplicates.
- `failed_tests[].classification`: Per-test classification — `"flaky"` or `"code-issue"`.
- `failed_tests[].error`: The full error message from the TRX test failure data.
- `failed_tests[].stack_trace`: The stack trace from the TRX test failure data (include the first few relevant frames).
- `analyzed_at`: The current UTC timestamp in ISO 8601 format.
- `causes`: An array of cause IDs (strings) that were identified for this run. These correspond to the cause files written in Step 3b. The publish job uses this to add an occurrence entry to each referenced cause. Empty array `[]` for code-issue verdicts. `causes` MUST cover every `transient-infra` failed job with an `infra-failure` cause, every `flaky-test` failed job with a `flaky-test` cause, and every `main-repository-breakage` failed job with a `main-repository-breakage` cause. `code-issue` jobs are exempt.

#### 3b. Per-cause files

For each distinct underlying cause that is NOT a pull-request code issue, write a separate JSON file to `/tmp/gh-aw/agent/causes/<cause-id>.json`. The `<cause-id>` should be a filesystem-safe identifier derived from the cause (e.g., sanitized test name for flaky tests, or a short descriptive slug for infrastructure issues and main repository breakages). Do NOT create cause files for `code-issue` classifications — those are the PR author's responsibility and are not tracked as recurring CI problems.

Each cause file must follow this schema:

```json
{
  "id": "cause-id",
  "type": "flaky-test | infra-failure | main-repository-breakage",
  "title": "Human-readable short description of the cause",
  "test_name": "Fully.Qualified.TestName (only for flaky-test with a specific test)",
  "error_pattern": "The key error message or pattern that identifies this cause",
  "job_ids": [123456789]
}
```

Field details:
- `id`: Must match the filename (without `.json`). Use lowercase with hyphens. For flaky tests, derive from the test name (e.g., `aspire-hosting-tests-mytest`). For infra failures, use a descriptive slug (e.g., `nuget-feed-timeout`, `docker-registry-rate-limit`).
- `type`: One of `"flaky-test"`, `"infra-failure"`, or `"main-repository-breakage"`. Do NOT create cause files for pull-request code-issue classifications.
- `title`: A brief human-readable description (e.g., "Flaky: MyNamespace.MyTest times out intermittently", "NuGet feed connection timeout").
- `test_name`: The fully qualified test name for a flaky-test cause. Omit this field for infrastructure failures; infrastructure causes MUST NOT include a non-empty `test_name`.
- `error_pattern`: The actual error message and relevant stack trace from the failure. For flaky tests, use the error message and first few stack trace frames from the TRX data. For infra failures, use the error text from the job logs. Include enough detail to identify and reproduce the issue (up to ~500 characters).
- `job_ids`: A non-empty array of unique numeric IDs for the failed jobs where this cause occurred. Use only IDs from the trusted failed-job summary; do not write job names. An `infra-failure` cause may reference only `transient-infra` jobs, and a `main-repository-breakage` cause may reference only `main-repository-breakage` jobs. A `flaky-test` cause normally references `flaky-test` jobs, but it may reference a `code-issue` or `main-repository-breakage` job when `failed_tests` contains a `"flaky"` test from that same job.

Do NOT include an `occurrences` field — the publish job builds occurrences automatically from the run summary JSON. The publisher derives display names from trusted job metadata and removes `job_ids` before storing the stable cause definition.

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

Apply rules based on the immutable run scope:

- For `pull-request`, determine whether the PR changes caused the failure and report deterministic failures as `code-issue`.
- For `main`, consider the complete candidate merge range since the last successful main run. The triggering merge PR is context only and is not necessarily causal. Deterministic compilation, test, API compatibility, lint, or formatting failures are `main-repository-breakage`; they MUST NOT be classified as infrastructure merely because they are unrelated to the triggering merge PR.

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

A test failed transiently rather than because repository code changed. PR-file relationships are indicators only for pull-request scope; main-scope `flaky-test` classification requires independent transient evidence. Indicators:
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

This classification is valid only for pull-request scope.

### 4. Main Repository Breakage

The failure is a deterministic code or repository failure on main. Indicators:
- Compilation or build errors caused by the combined repository state
- Deterministic test, API compatibility, lint, or formatting failures on main
- Semantic merge conflicts where independently valid changes are incompatible together

Use all candidate merges since the last successful main run when investigating. Name a specific PR as causal only when the logs and changed code provide direct evidence and candidate history is available and complete. If candidate history is unavailable or incomplete, do not name any PR as causal, including the triggering merge; report only repository-level evidence.

## Analysis Process

1. Read `ci-failure-data/analysis-summary.md`
2. For each failed job, examine:
   - The failed step names
   - The job log output for error messages
   - The job annotations
3. Cross-reference failures against:
   - The known transient failure patterns
   - For pull requests, the PR changed files list
   - For main, all candidate merges since the last successful main run
4. Classify each failed job
5. Determine the overall verdict and proceed to **Actions**

## Actions

After writing the JSON files (summary + per-cause), take action based on the verdict:

### If ALL failures are Transient Infrastructure Failures:

Set `verdict` to `"transient-infra"` in the JSON. Set `failed_tests` to an empty array for `transient-infra`; a run with any reported failed test must use `flaky-test`, `code-issue`, or `mixed` according to the evidence. Check the `ENABLE_RERUN` environment variable (set in the workflow `env:` block).

**If `ENABLE_RERUN` is `'true'`:** Emit the `rerun-failed-jobs` safe output to rerun the failed CI jobs.

**Regardless of `ENABLE_RERUN`:** Emit the `publish-data` safe output so the analysis is pushed to the memory branch and a PR comment is posted.

### If failures include Transient Test Failures and no deterministic failures:

Set `verdict` to `"flaky-test"` in the JSON. Ensure `failed_tests` entries have `classification: "flaky"` and include a `reason` explaining why the test is likely flaky.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

### If ALL failures are Non-Transient PR Code Issues:

Set `verdict` to `"code-issue"` in the JSON. Ensure `failed_jobs` entries have `classification: "code-issue"` with a clear `reason` linking the error to PR changes.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

### If ALL failures are Main Repository Breakages:

Set `verdict` to `"main-repository-breakage"` in the JSON. Set `pr` to `null`, populate `triggering_merge_pr` only as non-causal context when candidate history is available and complete, and include the main candidate range in `main_context`. If candidate history is unavailable or incomplete, do not identify a causal PR or claim a candidate range. Write a `main-repository-breakage` cause file so the publish job creates or updates the dedicated main-CI-break issue.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

### Mixed Failures

If there are both transient and non-transient failures, set `verdict` to `"mixed"`. Report all findings with per-job and per-test classifications.

A single failed job can contain both a deterministic failure and a flaky failed test. In that case, classify the job by the deterministic failure, include the flaky test and its cause, and use `mixed` so neither failure is omitted.

Emit the `publish-data` safe output. Do NOT emit `rerun-failed-jobs`.

## Important Rules

1. **Always write the run summary** — every analysis must produce `/tmp/gh-aw/agent/analysis-result.json`. Write cause files in `/tmp/gh-aw/agent/causes/` for `flaky-test`, `infra-failure`, and `main-repository-breakage` causes (NOT for pull-request `code-issue`).
2. **Always emit the `publish-data` safe output** — with `run_id` and `pr_numbers` so the publish-data job can push the data and post a comment.
3. **Never rerun when there are code issues** — only emit `rerun-failed-jobs` for pure infrastructure failures with `ENABLE_RERUN` set to `'true'`.
4. **Be specific** — include actual error messages and job/test names in the JSON fields.
5. **Use scope-appropriate history** — cross-reference PR files only for pull-request scope; for main scope, consider every candidate merge since the last successful main run.
6. **PR must not be locked** — for pull-request scope, check the PR state from the "Pull Request" section in the summary file. If the PR is locked, skip the analysis and call `noop`. Still analyze and comment on closed PRs. This rule does not apply to main scope.
7. **Do NOT use MCP to query GitHub** — all needed data (PR metadata, changed files, job logs, annotations) is already in the summary file. No GitHub API tools are available.
8. **Do NOT post PR comments directly** — the `publish-data` job handles commenting using the JSON file. Do not use `add-comment`.
