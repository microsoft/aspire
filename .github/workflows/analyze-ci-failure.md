---
description: |
  Analyzes failed CI builds using Copilot to determine whether the failure is
  transient (flaky test, infrastructure issue), caused by pull request changes,
  or a repository break on main. Pull request failures are reported on the PR;
  main repository breaks create a dedicated issue.

on:
  workflow_run:
    workflows: ["CI"]
    types: [completed]
    # Only analyze main builds while the workflow is being validated.
    branches: [main]
  workflow_dispatch:
    inputs:
      run_id:
        description: "CI workflow run ID to analyze"
        required: true
        type: string

jobs:
  conclusion:
    permissions:
      contents: read
    # gh-aw inserts these after runtime setup and before its operational reporters.
    pre-steps:
      - name: Checkout trusted report preparation
        uses: actions/checkout@v7.0.1
        with:
          ref: ${{ github.workflow_sha }}
          persist-credentials: false
          sparse-checkout: .github/workflows/analyze-ci-failure/
          sparse-checkout-cone-mode: false
      - name: Include source CI run in operational reports
        uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
        env:
          SOURCE_EVENT: ${{ github.event_name }}
          SOURCE_RUN_ID: ${{ github.event_name == 'workflow_run' && github.event.workflow_run.id || inputs.run_id }}
        with:
          script: |
            const path = require('node:path');
            const { prepareReportTemplates } = require(path.join(
              process.env.GITHUB_WORKSPACE,
              '.github/workflows/analyze-ci-failure/prepare-report-templates.js'));
            prepareReportTemplates(process.env);
  require-publication:
    runs-on: ubuntu-latest
    # An explicit agent dependency makes this a downstream custom job in gh-aw.
    # always() still checks skipped/failed publication, including agent failures.
    needs: [agent, collect-data, publish_data]
    if: always()
    permissions:
      contents: read
    steps:
      - name: Checkout trusted publication gate
        if: needs.collect-data.outputs.has-work == 'true'
        uses: actions/checkout@v7.0.1
        with:
          persist-credentials: false
          sparse-checkout: |
            global.json
            NuGet.config
            tools/AnalyzeCiFailure/
            tools/Aspire.TestTools/
          sparse-checkout-cone-mode: false
      - name: Set up analyzer SDK
        if: needs.collect-data.outputs.has-work == 'true'
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          global-json-file: global.json
      - name: Check completion for relevant CI failures
        if: needs.collect-data.outputs.has-work == 'true'
        env:
          HAS_WORK: ${{ needs.collect-data.outputs.has-work }}
          PUBLICATION_RESULT: ${{ needs.publish_data.result }}
          COLLECTION_STATUS: ${{ needs.collect-data.outputs.collection-status }}
          COLLECTION_RESULT: ${{ needs.collect-data.result }}
        run: |
          dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj --configuration Release -- \
            workflow check-completion
  collect-data:
    runs-on: ubuntu-latest
    if: >-
      github.repository == 'microsoft/aspire'
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
      analysis-ready: ${{ steps.collect.outputs.analysis_ready }}
      collection-status: ${{ steps.collect.outputs.collection_status }}
      run_id: ${{ steps.collect.outputs.run_id }}
      run_attempt: ${{ steps.collect.outputs.run_attempt }}
      run_url: ${{ steps.collect.outputs.run_url }}
      run_scope: ${{ steps.collect.outputs.run_scope }}
      pr_numbers: ${{ steps.collect.outputs.pr_numbers }}
    env:
      GH_TOKEN: ${{ github.token }}
    steps:
      - name: Checkout trusted analyzer
        uses: actions/checkout@v7.0.1
        with:
          persist-credentials: false
          sparse-checkout: |
            global.json
            NuGet.config
            tools/AnalyzeCiFailure/
            tools/Aspire.TestTools/
            eng/test-retry-patterns.json
          sparse-checkout-cone-mode: false
      - name: Set up analyzer SDK
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          global-json-file: global.json
      - name: Collect CI failure evidence and context
        id: collect
        env:
          MANUAL_RUN_ID: ${{ inputs.run_id }}
          WORKFLOW_RUN_ID: ${{ github.event.workflow_run.id }}
          WORKFLOW_RUN_ATTEMPT: ${{ github.event.workflow_run.run_attempt }}
          EVENT_NAME: ${{ github.event_name }}
        run: |
          args=(collect --repo "$GITHUB_REPOSITORY" --github-output "$GITHUB_OUTPUT")
          if [ "$EVENT_NAME" = "workflow_dispatch" ]; then
            args+=(--run-id "$MANUAL_RUN_ID")
          elif [ "$EVENT_NAME" = "workflow_run" ]; then
            args+=(--run-id "$WORKFLOW_RUN_ID" --run-attempt "$WORKFLOW_RUN_ATTEMPT")
          else
            echo "::error::Unsupported workflow event"
            exit 1
          fi
          dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj --configuration Release -- \
            "${args[@]}"
      - uses: actions/upload-artifact@v7.0.1
        if: steps.collect.outputs.has_work == 'true'
        with:
          name: ci-failure-evidence
          path: ${{ steps.collect.outputs.evidence_dir }}
          if-no-files-found: error
      - name: Upload collection recovery diagnostics
        if: always() && steps.collect.outputs.recovery_dir != ''
        uses: actions/upload-artifact@v7.0.1
        with:
          name: ci-failure-recovery-collection-${{ github.run_id }}-${{ github.run_attempt }}
          path: ${{ steps.collect.outputs.recovery_dir }}
          if-no-files-found: ignore
          retention-days: 7
      - name: Upload collection decision summary
        if: always() && steps.collect.outputs.diagnostics_dir != ''
        uses: actions/upload-artifact@v7.0.1
        with:
          name: ci-failure-diagnostics-collection-${{ github.run_id }}-${{ github.run_attempt }}
          path: ${{ steps.collect.outputs.diagnostics_dir }}
          if-no-files-found: ignore
          retention-days: 7

if: needs.collect-data.outputs.analysis-ready == 'true'

env:
  # Rerun effects remain disabled; analysis and publication still run.
  ENABLE_RERUN: 'false'

# Two ordinary history pushes surround issue effects. Serialize analyses and preserve queued work.
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
        comment or updates cause issues according to trusted scope.
        Write /tmp/gh-aw/agent/proposal.json. Emit exactly one publish_data item with string run_id
        and, only when present in trusted context, pr_numbers.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      if: needs.detection.result == 'success' && needs.detection.outputs.detection_success == 'true' && needs.safe_outputs.result == 'success'
      permissions:
        actions: read
        contents: write
        issues: write
        pull-requests: write
      inputs:
        run_id:
          description: "The workflow run ID that was analyzed."
          required: true
          type: string
        pr_numbers:
          description: "The unambiguous subject PR number. Omit when absent."
          required: false
          type: string
      env:
        GH_TOKEN: ${{ github.token }}
      steps:
        - name: Download CI analysis files
          id: download-analysis
          uses: actions/download-artifact@v8.0.1
          with:
            name: ci-analysis-output
            path: ${{ runner.temp }}/proposal
        - name: Checkout trusted analyzer
          uses: actions/checkout@v7.0.1
          with:
            persist-credentials: false
            sparse-checkout: |
              global.json
              NuGet.config
              tools/AnalyzeCiFailure/
              tools/Aspire.TestTools/
            sparse-checkout-cone-mode: false
        - name: Set up analyzer SDK
          uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
          with:
            global-json-file: global.json
        - id: download-evidence
          uses: actions/download-artifact@v8.0.1
          with:
            name: ci-failure-evidence
            path: ci-failure-analysis/evidence/
        - name: Publish analysis history, issues, and PR comment
          id: publish
          env:
            ANALYSIS_DIR: ${{ steps.download-analysis.outputs.download-path }}
            EVIDENCE_DIR: ${{ steps.download-evidence.outputs.download-path }}
          # Shared validation runs in this process before any publication.
          run: |
            dotnet run \
              --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj \
              --configuration Release \
              -- \
              publish \
              --repo "$GITHUB_REPOSITORY" \
              --evidence-dir "$EVIDENCE_DIR" \
              --github-output "$GITHUB_OUTPUT" \
              --analysis-dir "$ANALYSIS_DIR"
        - name: Upload publication recovery diagnostics
          if: always() && steps.publish.outputs.recovery_dir != ''
          uses: actions/upload-artifact@v7.0.1
          with:
            name: ci-failure-recovery-publication-${{ github.run_id }}-${{ github.run_attempt }}
            path: ${{ steps.publish.outputs.recovery_dir }}
            if-no-files-found: ignore
            retention-days: 7
        - name: Upload publication decision summary
          if: always() && steps.publish.outputs.diagnostics_dir != ''
          uses: actions/upload-artifact@v7.0.1
          with:
            name: ci-failure-diagnostics-publication-${{ github.run_id }}-${{ github.run_attempt }}
            path: ${{ steps.publish.outputs.diagnostics_dir }}
            if-no-files-found: ignore
            retention-days: 7
    rerun-failed-jobs:
      name: "Rerun failed CI jobs"
      description: |
        Reruns failed CI jobs only for pure transient infrastructure failures.
        Emit exactly one rerun_failed_jobs item with run_id and reason. Include
        pr_numbers only when present in trusted context.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      if: needs.detection.result == 'success' && needs.detection.outputs.detection_success == 'true' && needs.safe_outputs.result == 'success'
      permissions:
        actions: write
        contents: read
        pull-requests: write
      inputs:
        run_id:
          description: "The workflow run ID to rerun failed jobs for."
          required: true
          type: string
        pr_numbers:
          description: "The unambiguous subject PR number. Omit when absent."
          required: false
          type: string
        reason:
          description: "Short summary of why the rerun was requested."
          required: true
          type: string
      steps:
        - name: Download CI analysis files for rerun
          id: download-analysis
          uses: actions/download-artifact@v8.0.1
          with:
            name: ci-analysis-output
            path: ${{ runner.temp }}/proposal
        - name: Checkout trusted analyzer
          uses: actions/checkout@v7.0.1
          with:
            persist-credentials: false
            sparse-checkout: |
              global.json
              NuGet.config
              tools/AnalyzeCiFailure/
              tools/Aspire.TestTools/
            sparse-checkout-cone-mode: false
        - name: Set up analyzer SDK
          uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
          with:
            global-json-file: global.json
        - id: download-evidence
          uses: actions/download-artifact@v8.0.1
          with:
            name: ci-failure-evidence
            path: ci-failure-analysis/evidence/
        - name: Authorize and request rerun
          env:
            ANALYSIS_DIR: ${{ steps.download-analysis.outputs.download-path }}
            EVIDENCE_DIR: ${{ steps.download-evidence.outputs.download-path }}
            ENABLE_RERUN: ${{ env.ENABLE_RERUN }}
            GH_TOKEN: ${{ github.token }}
          run: |
            dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj --configuration Release -- \
              workflow rerun \
              --repo "$GITHUB_REPOSITORY" \
              --evidence-dir "$EVIDENCE_DIR" \
              --analysis-dir "$ANALYSIS_DIR"

steps:
  - uses: actions/download-artifact@v8.0.1
    with:
      name: ci-failure-evidence
      path: ci-failure-analysis/evidence/

post-steps:
  - name: Upload CI analysis files
    uses: actions/upload-artifact@v7.0.1
    with:
      name: ci-analysis-output
      path: /tmp/gh-aw/agent/proposal.json
      if-no-files-found: error
---

# Analyze CI Failure

Analyze the failed CI build in **microsoft/aspire**. Determine the current failure mechanism, classify the evidence, and write the analysis files. The run scope is derived from immutable `event` and `head_branch`; never infer or change it from associated PRs.

## Read the prepared evidence

Read `ci-failure-analysis/evidence/analysis-summary.md`, then the complete `ci-failure-analysis/evidence/history/cause-index.json`. The early preview is not the complete history. Each indexed `file` points to the full original record, including occurrences, diagnostics, keyless infrastructure causes, and opaque legacy labels.

`ci-failure-analysis/evidence/collection-outcome.json` records collection problems and known progress. It is diagnostic context, never publication authority.

Use `ci-failure-analysis/evidence/evidence.json` as the authoritative run, job and structured-test inventory. Exact `name` values identify observations; `display_name` and summary previews are bounded/redacted presentation only. Do not truncate raw names, strip parameters, normalize methods, or split opaque legacy labels on `/`. All GitHub IDs are canonical decimal strings, never JSON numbers.

Read the bundle jobs, including failed and skipped steps. Classify the current failing operation before comparing history. A job name describes what the job intended to run, not what failed. A prerequisite download returning HTTP 504 and curl exit 22 while tests were skipped is infrastructure failure, not a flaky test.

Missing logs are a collection problem, not evidence of a known test failure. Use fetch diagnostics, annotations, and structured results; never substitute a prior cause's diagnostic for missing current evidence. Do not infer failed tests from logs.

A job's `runner_failure` records a confirmed hosted-runner-loss annotation. Missing results from that interrupted job do not invalidate the other jobs' observations. Classify a confirmed runner-loss job with no observed test failures as `transient-infra` and cover it with an infrastructure cause. If results were captured, retain and classify those observations using their actual diagnostics.

The summary groups exact diagnostic records and lists every member ref to make shared fixture failures visible. A large group is not automatically infrastructure: establish its mechanism, retain every observation, and do not invent independent flaky causes for a shared environment failure.

## Classification rules

- **`transient-infra`**: infrastructure outside the author's control. Examples include network/DNS/SSL failures, HTTP 5xx prerequisite downloads, registry rate limits, runner failures, NuGet feed failures, Git transport errors, and Windows process initialization errors (`0xC0000142`, `-1073741502`). Consult `eng/test-retry-patterns.json` through the prepared evidence.
- **`flaky-test`**: a test actually ran and failed, with independent transient/timing/environmental evidence in current trusted structured diagnostics. For PR scope, unchanged files are an indicator, not proof. Main scope also requires independent transient evidence. Missing logs, skipped tests, a matching job name, or an exit code do not establish flakiness.
- **`code-issue`**: deterministic PR-caused compilation, test, API compatibility, lint, or formatting failure. This classification is valid only for pull-request scope. Link the diagnostic to changed code.
- **`main-repository-breakage`**: deterministic compilation, test, API compatibility, lint, formatting, or semantic merge failure on main. It is not infrastructure merely because it is unrelated to the triggering merge.

For main, investigate every candidate merge since the last successful main run. Name a causal PR only when current diagnostics and changed code provide direct evidence and candidate history is a complete `ahead` comparison. Identical, behind, diverged, malformed, or incomplete comparisons are non-attributable: do not name any PR as causal, including the triggering merge.

Use overall verdict `transient-infra` when every job and test observation is infrastructure-caused; `flaky-test` when failures are all transient and at least one is flaky; `code-issue` when all failures are PR-caused; `main-repository-breakage` when all are deterministic main failures; otherwise `mixed`. Reruns additionally require zero structured failed-test observations.

A single job can contain a deterministic failure and a flaky test. Classify the job by the deterministic failure, include the flaky observation and cause, and use `mixed` so neither is omitted.

## Match prior causes by mechanism

For tracked failures (`flaky-test`, `infra-failure`, `main-repository-breakage`), inspect relevant full prior records. Retrieval hints do not establish identity or exclude other records.

Reuse an existing cause ID only when category, failing phase, and current diagnostic agree. A shared method, job, or shard name does not establish a shared mechanism. One mechanism can cover several tests; one method can have different causes. A skipped test cannot match an earlier test assertion.

When a mechanism matches a stored cause, use the `existing` cause variant with its `id`. Its type and stored descriptive fields are publisher-owned. Scalar `test_name` in history is opaque legacy descriptive metadata. Do not track PR code issues as recurring causes.

## Write the decision-only proposal

Always write only `/tmp/gh-aw/agent/proposal.json`:

```json
{
  "schema_version": 1,
  "run": {"repository": "microsoft/aspire", "id": "12345", "attempt": 1},
  "jobs": [
    {"job_id": "67890", "classification": "flaky-test", "reason": "Intermittent timeout"}
  ],
  "tests": [
    {
      "ref": "test-1",
      "classification": "flaky",
      "reason": "Independent evidence of intermittent behavior"
    }
  ],
  "causes": [
    {
      "kind": "existing",
      "id": "test-timeout",
      "job_ids": ["67890"],
      "test_refs": ["test-1"],
      "reason": "The current failed phase and diagnostic match the recorded mechanism."
    }
  ]
}
```

- Copy only the repository/run/attempt identity, job IDs and test refs from the bundle. Attempts are positive integers; IDs are canonical decimal strings.
- Include every trusted job and structured test ref exactly once, with no omissions, additions or duplicates. Multiple executions stay separate even when names and diagnostics agree.
- Test classifications are `flaky`, `code-issue` or `transient-infra`; job classifications follow the rules above. Preserve infrastructure-caused fixture failures as test observations rather than omitting them or labelling them flaky. Each infrastructure test must belong to an infrastructure job covered by an infrastructure cause; that job-level cause covers its observations without test refs. Reasons are single-line, at most 500 characters.
- Do not repeat names, diagnostics, PR/scope/URL metadata, timestamps, overall verdict or redundant cause IDs. Validation derives the verdict and binds all collected facts without changing the proposal.
- Include every distinct supported mechanism in `causes`, even above ten. The publisher uses individual cause issues through ten mechanisms and one aggregate run-level issue above ten. Use `[]` for PR deterministic-only failures. Cover every tracked job and flaky ref with compatible causes; overlapping compatible coverage is allowed.

## Define a new cause only when no inspected prior record matches

Use this alternative inline in the proposal `causes` array (not a separate file):

```json
{
  "kind": "new",
  "definition": {
    "id": "test-timeout",
    "type": "flaky-test",
    "title": "Intermittent debugger timeout",
    "error_pattern": "Connection timed out"
  },
  "job_ids": ["67890"],
  "test_refs": ["test-1"],
  "reason": "No inspected prior record describes this mechanism."
}
```

- `id` is a lowercase hyphenated slug absent from the collected catalogue. Existing and new are distinct variants; never mix their fields.
- `type` is `flaky-test`, `infra-failure`, or `main-repository-breakage`. Never create a PR code-issue cause.
- `title` is a single-line description of at most 238 characters.
- Flaky causes reference the exact bundle-local `test_refs` classified flaky, sharing the same mechanism. Each referenced test belongs to a claimed job, and every claimed job contains a referenced observation. Infrastructure/main causes use an empty `test_refs` array.
- `error_pattern` is the actual diagnostic, up to 500 characters, optionally LF-delimited. Do not include styling/unsafe controls.
- `job_ids` is a nonempty array of unique trusted string IDs, never names. Infrastructure/main causes require compatible classifications.
- Do not include `occurrences`, issue links or other publisher-owned fields. No transient refs, all-source lists or artifact IDs enter durable history.

Do not merge distinct mechanisms or omit observations to reduce issue count. A single compatible cause may cover several tests.

## Request effects

Always emit exactly one `publish-data` safe output with `run_id`, including code-issue verdicts and closed/locked PRs. Include `pr_numbers` only when trusted context contains a subject PR; omit it for main runs rather than sending an empty string. Code issues produce a PR report but no recurring history. Main reports use cause issues, not PR comments. Main issue titles and public diagnostics derive from trusted context, not agent attribution.

Only emit `rerun-failed-jobs` for pure `transient-infra`, with no failed tests, and only when `ENABLE_RERUN` is `'true'`. Otherwise do not request a rerun. Publication and rerun authorization recheck open/unlocked PR state immediately before PR-directed effects.

Do not call GitHub MCP/API tools or post comments directly. All needed evidence is already prepared; deterministic validation, history, issue updates, and PR comments belong to the application.
