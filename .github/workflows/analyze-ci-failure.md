---
description: |
  Analyzes failed PR CI builds using Copilot to determine whether the failure
  is transient (flaky test, infrastructure issue) or caused by the PR changes
  (compilation error, test regression). For transient infrastructure failures,
  reruns the CI build. For transient test failures, posts a comment with
  details and suggested next steps. For non-transient failures, posts a
  comment explaining the root cause.

  NOTE: This workflow coexists with `auto-rerun-transient-ci-failures.yml`
  which also triggers on CI failures. That workflow uses pattern-matching to
  classify failures deterministically (currently in FORCE_RERUN_ALL mode).
  This workflow uses Copilot for deeper analysis and provides richer PR
  comments. When ENABLE_RERUN is 'false' (the default), this workflow only
  observes and comments — it does not conflict with the existing rerun
  workflow. Set ENABLE_RERUN to 'true' only after disabling
  FORCE_RERUN_ALL in the other workflow to avoid duplicate reruns.

on:
  # TODO: Enable automatic trigger after testing is complete
  # workflow_run:
  #   workflows: ["CI"]
  #   types:
  #     - completed
  #   branches:
  #     - main
  #     - 'release/**'
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
          github.event.workflow_run.event == 'pull_request'
          && github.event.workflow_run.conclusion == 'failure'
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
            # Fallback: search for PRs by head branch (requires owner:branch format)
            HEAD_OWNER=$(jq -r '.head_repository.owner.login // ""' ci-failure-data/run.json)
            if [ -n "${HEAD_OWNER}" ] && [ -n "${HEAD_BRANCH}" ]; then
              PR_NUMBERS=$(gh api "repos/${REPO}/pulls?state=open&head=${HEAD_OWNER}:${HEAD_BRANCH}" \
                --jq '[.[].number] | join(",")' 2>/dev/null || echo "")
            fi
          fi
          echo "pr_numbers=${PR_NUMBERS}" >> "$GITHUB_OUTPUT"

          if [ -z "${PR_NUMBERS}" ]; then
            echo "No associated PR found. Skipping analysis."
            echo "has_work=false" >> "$GITHUB_OUTPUT"
            exit 0
          fi

          # Fetch all jobs for this run attempt.
          # Use --jq '.jobs[]' to emit individual job objects (handles pagination
          # correctly) then jq -s collects them into a single JSON array.
          gh api --paginate "repos/${REPO}/actions/runs/${RUN_ID}/attempts/${RUN_ATTEMPT}/jobs" \
            --jq '.jobs[]' | jq -s '.' > ci-failure-data/all-jobs.json

          # Extract failed jobs
          jq '[.[] | select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out")]' \
            ci-failure-data/all-jobs.json > ci-failure-data/failed-jobs.json

          FAILED_COUNT=$(jq 'length' ci-failure-data/failed-jobs.json)
          echo "Failed jobs: ${FAILED_COUNT}"

          if [ "${FAILED_COUNT}" -eq 0 ]; then
            echo "No failed jobs found. Skipping analysis."
            echo "has_work=false" >> "$GITHUB_OUTPUT"
            exit 0
          fi

          echo "has_work=true" >> "$GITHUB_OUTPUT"

          # Fetch logs for each failed job (last 64KB of each, enough for analysis)
          jq -r '.[].id' ci-failure-data/failed-jobs.json | while read -r JOB_ID; do
            JOB_NAME=$(jq -r ".[] | select(.id == ${JOB_ID}) | .name" ci-failure-data/failed-jobs.json)
            echo "Fetching logs for job: ${JOB_NAME} (${JOB_ID})"
            gh api "repos/${REPO}/actions/jobs/${JOB_ID}/logs" > "ci-failure-data/job-${JOB_ID}.log" 2>/dev/null || \
              echo "(Failed to fetch logs for job ${JOB_ID})" > "ci-failure-data/job-${JOB_ID}.log"
            # Trim to last 64KB to keep context manageable
            tail -c 65536 "ci-failure-data/job-${JOB_ID}.log" > "ci-failure-data/job-${JOB_ID}-trimmed.log"
            mv "ci-failure-data/job-${JOB_ID}-trimmed.log" "ci-failure-data/job-${JOB_ID}.log"
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
          fi

          # Load the known transient failure patterns for reference
          if [ -f "eng/test-retry-patterns.json" ]; then
            cp eng/test-retry-patterns.json ci-failure-data/retry-patterns.json
          fi

          # Fetch test results artifact if available and extract test failure info
          ARTIFACT=$(gh api "repos/${REPO}/actions/runs/${RUN_ID}/artifacts" \
            --jq '[.artifacts[] | select(.name | test("test-results|TestResults"; "i"))] | first // empty' 2>/dev/null || echo "")
          if [ -n "${ARTIFACT}" ]; then
            ARTIFACT_ID=$(echo "${ARTIFACT}" | jq -r '.id')
            echo "Downloading test results artifact ${ARTIFACT_ID}..."
            gh api "repos/${REPO}/actions/runs/${RUN_ID}/artifacts/${ARTIFACT_ID}/zip" \
              > ci-failure-data/test-results.zip 2>/dev/null || true

            # Extract failed test names and error messages from TRX files
            if [ -f "ci-failure-data/test-results.zip" ] && [ -s "ci-failure-data/test-results.zip" ]; then
              mkdir -p ci-failure-data/test-results
              unzip -q -o ci-failure-data/test-results.zip -d ci-failure-data/test-results 2>/dev/null || true

              # Parse TRX files for failed tests (TRX is XML with UnitTestResult elements)
              # Produce one JSON object per line using jq for proper escaping, then
              # combine into an array. This avoids pipe-subshell variable propagation
              # issues and handles all JSON special characters correctly.
              # TRX format: <UnitTestResult testName="..." outcome="Failed"><Output><ErrorInfo><Message>...</Message></ErrorInfo></Output></UnitTestResult>
              > ci-failure-data/test-failures.jsonl
              while IFS= read -r TRX_FILE; do
                while IFS= read -r TEST_NAME; do
                  ERROR_MSG=$(grep -A5 "testName=\"${TEST_NAME}\".*outcome=\"Failed\"" "${TRX_FILE}" 2>/dev/null \
                    | grep -oP '<Message>\K[^<]+' 2>/dev/null | head -1 | cut -c1-200 || echo "")
                  jq -n --arg test "${TEST_NAME}" --arg error "${ERROR_MSG}" \
                    '{test: $test, error: $error}' >> ci-failure-data/test-failures.jsonl
                done < <(grep -oP '<UnitTestResult[^>]*testName="\K[^"]+(?="[^>]*outcome="Failed")' "${TRX_FILE}" 2>/dev/null || true)
              done < <(find ci-failure-data/test-results -name "*.trx" -type f 2>/dev/null)
              jq -s '.' ci-failure-data/test-failures.jsonl > ci-failure-data/test-failures.json 2>/dev/null || echo "[]" > ci-failure-data/test-failures.json
              rm -f ci-failure-data/test-failures.jsonl

              # Clean up the extracted files to save space in artifact
              rm -rf ci-failure-data/test-results
              rm -f ci-failure-data/test-results.zip
            fi
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

            echo "## Job Logs (Trimmed)"
            echo ""
            for LOG_FILE in ci-failure-data/job-*.log; do
              if [ -f "${LOG_FILE}" ]; then
                JOB_ID=$(basename "${LOG_FILE}" | sed 's/job-\(.*\)\.log/\1/')
                JOB_NAME=$(jq -r ".[] | select(.id == ${JOB_ID}) | .name" ci-failure-data/failed-jobs.json 2>/dev/null || echo "Unknown")
                echo "### Logs: ${JOB_NAME} (${JOB_ID})"
                echo '```'
                # Show last 200 lines to keep within context limits
                tail -200 "${LOG_FILE}"
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
                jq -r '.[] | "- `\(.test)`: \(.error)"' ci-failure-data/test-failures.json 2>/dev/null || echo "No parseable test failures."
              else
                echo "No test failures extracted from TRX artifacts."
              fi
            else
              echo "No test results artifact available."
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

tools:
  github:
    toolsets: [repos, issues, pull_requests]
    min-integrity: approved
  cache-memory:

safe-outputs:
  add-comment:
    max: 1
  jobs:
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
              const path = require('path');

              // Read inputs from the agent output artifact
              const outputFile = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputFile || !fs.existsSync(outputFile)) {
                core.setFailed('Agent output file not found');
                return;
              }
              const agentOutput = JSON.parse(fs.readFileSync(outputFile, 'utf8'));
              const items = agentOutput.rerun_failed_jobs || [];
              if (items.length === 0) {
                core.info('No rerun_failed_jobs items in agent output.');
                return;
              }
              const item = items[0];

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

## Cache-First Workflow

The analysis should only be performed on the **first run attempt**. Subsequent attempts reuse the cached classification.

### Step 0: Read the summary file

Read `ci-failure-data/analysis-summary.md`. It contains the run ID, run attempt, and either full failure data (first attempt) or instructions to read cached results (retry attempt).

### Step 1: Check for cached analysis

Using the **cache-memory** tool, try to read the key `ci-failure-analysis-<RUN_ID>` (where `<RUN_ID>` is the run ID from the summary).

- **If a cached value exists**: This is a retry. Parse the cached JSON and skip directly to the **Actions** section below using the cached `classification` and `details`. Do NOT re-analyze the logs.
- **If no cached value exists**: This is the first attempt. Proceed to Step 2.

### Step 2: Full analysis (first attempt only)

The analysis summary file contains:
- Failed jobs and their failed steps
- Job logs (last 200 lines per job)
- Job annotations
- Test failures from TRX artifacts (fully qualified test names and error messages)
- PR changed files
- Known transient failure patterns from `eng/test-retry-patterns.json`

Analyze all of this data to classify each failed job (see **Classification Rules** below).

### Step 3: Cache the classification

After completing the analysis, store the result using the **cache-memory** tool with key `ci-failure-analysis-<RUN_ID>`. The value must be a JSON object:

```json
{
  "classification": "transient-infrastructure" | "flaky-test" | "code-issue" | "mixed",
  "details": [
    {
      "job": "<job name>",
      "category": "transient-infrastructure" | "flaky-test" | "code-issue",
      "reason": "<brief explanation>",
      "error": "<key error message>"
    }
  ],
  "run_id": "<run id>",
  "run_url": "<run url>",
  "pr_numbers": "<comma-separated PR numbers>"
}
```

Then proceed to the **Actions** section.

## Input Data (first attempt only)

The file `ci-failure-data/analysis-summary.md` contains the full failure data:
- The failed workflow run information
- Failed jobs and their failed steps
- Job logs (last 200 lines per job)
- Job annotations
- Test failures extracted from TRX artifacts (test name and error message)
- PR changed files
- Known transient failure patterns from `eng/test-retry-patterns.json`

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

## Analysis Process (first attempt only)

1. Read `ci-failure-data/analysis-summary.md`
2. For each failed job, examine:
   - The failed step names
   - The job log output for error messages
   - The job annotations
3. Cross-reference failures against:
   - The known transient failure patterns
   - The PR changed files list
4. Classify each failed job
5. Cache the classification using `cache-memory` (see Step 3 above)
6. Determine the overall verdict and proceed to **Actions**

## Actions

### If ALL failures are Transient Infrastructure Failures:

Check the `ENABLE_RERUN` environment variable (set in the workflow `env:` block).

**If `ENABLE_RERUN` is `'true'`:** Use the `rerun-failed-jobs` safe output to rerun the failed CI jobs. Post a comment on the PR:

```
<!-- analyze-ci-failure:rerun -->
🔄 **CI Failure Analysis: Transient Infrastructure Failure**

The CI build failed due to transient infrastructure issues. The failed jobs have been automatically rerun.

**Failed jobs:**
- `<job name>` — <brief reason> (e.g., "Network timeout connecting to NuGet feed")

[View the rerun attempt](<rerun URL>)
```

**If `ENABLE_RERUN` is NOT `'true'` (dry-run mode):** Do NOT emit the `rerun_failed_jobs` safe output. Post a comment on the PR indicating this was a dry run:

```
<!-- analyze-ci-failure:rerun-dry-run -->
🔍 **CI Failure Analysis (Dry Run): Transient Infrastructure Failure**

The CI build failed due to transient infrastructure issues. This is a **dry run** — the failed jobs have **not** been automatically rerun.

**Failed jobs:**
- `<job name>` — <brief reason> (e.g., "Network timeout connecting to NuGet feed")

To rerun the failed jobs manually, visit the [workflow run page](<run URL>).
```

### If ANY failures are Transient Test Failures (Flaky Tests):

Post a comment on the PR with details about the flaky test(s). Do NOT automatically rerun — the user should decide whether to rerun or investigate.

Format the comment like this:
```
<!-- analyze-ci-failure:flaky -->
⚠️ **CI Failure Analysis: Possible Flaky Test(s)**

The CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.

**Suspected flaky test(s):**
- `<test fully qualified name>` in job `<job name>`
  - **Error**: <brief error message>
  - **Why likely flaky**: <explanation, e.g., "Test is in Aspire.Hosting.Tests which was not modified by this PR, and the error shows a connection timeout">

**Suggested actions:**
- Re-run the failed CI jobs to confirm if the failure is intermittent
- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using `/quarantine-test <test name> <issue URL>`
- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky

You can re-run the failed jobs from the [workflow run page](<run URL>).
```

### If ANY failures are Non-Transient (PR Code Issues):

Post a comment on the PR explaining the failure analysis. Do NOT rerun — the PR author needs to fix the code.

Format the comment like this:
```
<!-- analyze-ci-failure:code-issue -->
❌ **CI Failure Analysis: Code Issue Detected**

The CI build failed due to issue(s) caused by changes in this PR.

**Failure details:**
- **Job**: `<job name>`
- **Failed Step**: `<step name>`
- **Error**: <the key error message from the logs>
- **Likely cause**: <brief explanation linking the error to a PR change>

<If compilation error, include the specific error messages>
<If test failure, explain which test failed and how it relates to the PR changes>

The CI will not be automatically rerun. Please fix the issue and push an updated commit.
```

### Mixed Failures

If there are both transient and non-transient failures, treat the overall result as non-transient (do NOT rerun). Report all findings in the comment, clearly separating transient and non-transient failures.

## Important Rules

1. **Never rerun when there are code issues** — only rerun for pure infrastructure failures.
2. **Respect `ENABLE_RERUN`** — only emit the `rerun_failed_jobs` safe output when `ENABLE_RERUN` is `'true'`. Otherwise, post a dry-run comment instead.
3. **Be specific** — include actual error messages and job/test names in the comment.
4. **Cross-reference PR files** — always check whether the failing test is in an area modified by the PR.
5. **One comment per analysis** — post exactly one comment summarizing all findings.
6. **Use HTML comments as markers** — include the `<!-- analyze-ci-failure:... -->` marker so duplicate comments can be detected and collapsed.
7. **PR must be open** — verify the PR is still open before posting a comment. If all associated PRs are closed/merged, skip the analysis.
