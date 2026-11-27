---
description: |
  Weekly audit of Aspire PR CI's dynamic test selection (`tools/SelectTests`,
  `eng/github-ci/test-trigger-map.yml`). Looks for pull requests where the
  selector fell back to running ALL tests (over-selection) or where a
  narrow result misses a runtime-only consumer, even with no matching
  rule (under-selection),
  classifies why, checks how similar cases were handled in the trigger
  map's own commit history, and files at most one issue per run for its
  single highest-confidence case where the selection could be made safer
  or cheaper. Raw per-PR evidence is retained for a rolling 14-day window,
  while settled per-input/target verdicts persist in a memory branch.
  The filed issue is assigned to the Copilot coding agent, which
  implements and validates the fix and opens a PR for human review. This
  workflow never edits the trigger map itself.

max-ai-credits: 300
max-daily-ai-credits: 600

on:
  schedule: weekly on monday
  workflow_dispatch:
    inputs:
      lookback_days:
        description: "How many days of PRs/CI runs to analyze (default: 14)"
        required: false
        type: number
      pr_numbers:
        description: "Optional: comma-separated PR numbers to focus on instead of the lookback window"
        required: false
        type: string

# Only run in the canonical repository. Forks don't have the required
# secrets/permissions for this report workflow.
if: github.repository == 'microsoft/aspire'

permissions:
  contents: read
  issues: read
  pull-requests: read
  actions: read
  copilot-requests: write

concurrency:
  # gh-aw's compiler always emits a static top-level group for this
  # workflow ("gh-aw-${{ github.workflow }}", queue: max) in addition to
  # whatever this field configures. It serializes every run of this
  # workflow, full-window or PR-focused, one at a time, queued in trigger
  # order. That is
  # deliberate here, not just an accepted side effect: two agent runs
  # executing concurrently would each read the memory ledger from the
  # same base and independently rewrite it (head replacements and
  # watchlist updates); the push that lands second can discard the first's
  # rows, even for appends (see step 13). Use the bounded run ID for the
  # redundant job-level groups: GitHub evaluates concurrency before the
  # collector can reject an oversized `pr_numbers` input, while the static
  # top-level group already provides the required cross-run serialization.
  job-discriminator: ${{ github.run_id }}

engine: copilot
timeout-minutes: 30

network:
  allowed:
    - defaults

pre-agent-steps:
  - name: Compact test-selection memory
    env:
      MEMORY_ROOT: /tmp/gh-aw/repo-memory/default
      RETENTION_DAYS: "14"
      AUDIT_DATE_PATH: ${{ runner.temp }}/gh-aw/safeoutputs/test-selection-audit/audit-date.txt
    run: python3 .github/workflows/test-selection-audit/compact_memory.py
  # The workspace is agent-writable; host validation must load a pre-agent
  # snapshot from the same read-only mount as the selection evidence. The
  # safeoutputs subdirectory is also visible to the memory-validation MCP tool.
  - name: Protect memory validator
    run: |
      install -m 444 .github/workflows/test-selection-audit/validate_memory.js \
        "${RUNNER_TEMP}/gh-aw/safeoutputs/test-selection-audit/validate_memory.js"
  - name: Collect test-selection evidence
    env:
      GH_TOKEN: ${{ github.token }}
      REPOSITORY: ${{ github.repository }}
      LOOKBACK_DAYS: ${{ github.event.inputs.lookback_days }}
      PR_NUMBERS: ${{ github.event.inputs.pr_numbers }}
      OUTPUT_PATH: ${{ runner.temp }}/gh-aw/safeoutputs/test-selection-audit/evidence.json
      PROCESSED_RUNS_PATH: /tmp/gh-aw/repo-memory/default/processed-runs.jsonl
      PROCESSED_BASELINE_PATH: ${{ runner.temp }}/gh-aw/safeoutputs/test-selection-audit/processed-runs-before.jsonl
      WATCHLIST_PATH: /tmp/gh-aw/repo-memory/default/watchlist.jsonl
      WATCHLIST_BASELINE_PATH: ${{ runner.temp }}/gh-aw/safeoutputs/test-selection-audit/watchlist-before.jsonl
      AUDIT_DATE_PATH: ${{ runner.temp }}/gh-aw/safeoutputs/test-selection-audit/audit-date.txt
    run: python3 .github/workflows/test-selection-audit/collect_evidence.py

  # A selection is fixed for a particular CI run/attempt, not for a PR
  # head: re-runs can replace its result. `processed-runs.jsonl` retains
  # each head and its counted path contributions so a newer attempt can
  # replace, rather than add to, its earlier counts. Deterministic compaction
  # bounds this raw ledger to the configured lookback window.
  #
  # `watchlist.jsonl` retains rolling counts plus settled dispositions, so
  # known correct, filed, in-flight, and fixed cases survive raw-row expiry.
  #
  # Repo memory, not cache memory: GitHub Actions evicts unused caches after
  # 7 days, which is exactly this workflow's period, so a cache would
  # routinely be gone by the next run. Repo memory is branch-backed and
  # retained indefinitely.
tools:
  # The audit must inspect structured evidence and write JSONL memory without
  # interactive approval. Keep network access behind the read-only GitHub MCP.
  bash: ["cat", "ls", "grep", "head", "tail", "wc", "jq", "python3", "node"]
  github:
    # Only GitHub MCP reads: repository source (selector implementation,
    # trigger map, docs), PR history, and issues (to reconcile a
    # `pending-filed` watchlist row against the real issue `create-issue`
    # produced, per step 1). CI selection evidence is collected by the
    # deterministic pre-agent step above, not by the agent. The `issues`
    # toolset also exposes `create_issue`, but the GitHub MCP server always
    # runs with `GITHUB_READ_ONLY: "1"` regardless of toolset -- write tools
    # are non-functional here. All writes go through safe-outputs instead.
    # The default "approved" integrity filter would hide fork PRs from
    # first-time/external contributors -- exactly the fork PRs this audit
    # is meant to inspect, so it is disabled here. GitHub mutations are
    # limited to one safe-output issue whose resulting PR still requires
    # human review before merging.
    toolsets: [repos, pull_requests, issues]
    min-integrity: none
  repo-memory:
    branch-name: memory/test-selection-audit
    description: "Resolved PR selections and the rule watchlist for the CI test-selection audit"
    # Both ledgers are JSONL so individual observations can be counted and
    # updated. gh-aw's push retry uses `git pull --no-rebase -X ours` (step
    # 13), not a JSONL-aware merge; even two appends can conflict and lose
    # rows. The workflow-level concurrency group protects these ledgers.
    file-glob: ["processed-runs.jsonl", "watchlist.jsonl"]
    allowed-extensions: [".jsonl"]
    # Defaults (100KB file / 10KB patch) are too small: the rolling ledger
    # retains one row per resolved PR head and a busy window covers hundreds.
    max-file-size: 2097152
    max-patch-size: 262144
    max-file-count: 10
    validation:
      timeout-minutes: 1
      # Use the same entry point for agent self-checks and host validation.
      script: |
        const validatorPath = path.join(process.env.RUNNER_TEMP, "gh-aw/safeoutputs/test-selection-audit/validate_memory.js");
        const isPushJob = path.resolve(memoryRoot) === path.resolve(process.env.GITHUB_WORKSPACE);
        if (!fs.existsSync(validatorPath)) {
          if (!isPushJob) throw new Error("Protected memory validator is unavailable");
          // The push job switches HEAD to the memory branch before validation.
          // Its fresh, single-ref source checkout remains in refs/remotes/origin;
          // load that trusted code even though sparse checkout omits the file.
          const { execFileSync } = require("node:child_process");
          // Lazy blob fetches must not alter .git: the framework snapshots the
          // entire memory workspace, including Git metadata, during validation.
          const objects = fs.mkdtempSync(path.join(process.env.RUNNER_TEMP, "test-selection-audit-objects-"));
          const gitOptions = {
            cwd: process.env.GITHUB_WORKSPACE,
            encoding: "utf8",
            env: {
              ...process.env,
              GIT_OPTIONAL_LOCKS: "0",
              GIT_OBJECT_DIRECTORY: objects,
              GIT_ALTERNATE_OBJECT_DIRECTORIES: path.join(process.env.GITHUB_WORKSPACE, ".git/objects"),
            },
          };
          // for-each-ref emits one full commit SHA per line, e.g. "abc...123\n".
          const refs = execFileSync("git", [
            "--no-pager", "for-each-ref", "--format=%(objectname)", "refs/remotes/origin/",
          ], gitOptions).trim().split(/\r?\n/);
          if (refs.length !== 1 || !/^[0-9a-f]{40}$/.test(refs[0]))
            throw new Error("Expected one trusted workflow checkout ref for memory validation");
          const source = execFileSync("git", [
            "--no-pager", "show", `${refs[0]}:.github/workflows/test-selection-audit/validate_memory.js`,
          ], gitOptions);
          fs.mkdirSync(path.dirname(validatorPath), { recursive: true });
          fs.writeFileSync(validatorPath, source, { mode: 0o444 });
        }
        require(validatorPath).validateMemory(memoryRoot, !isPushJob);

safe-outputs:
  steps:
    # gh-aw can run safe-output processing after the agent job fails. This
    # audit must not file an issue unless memory validation and every other
    # agent step completed successfully.
    - name: Require successful audit before safe outputs
      if: needs.agent.result != 'success'
      run: |
        echo "::error::Safe outputs are blocked because the audit agent did not complete successfully."
        exit 1
  create-issue:
    title-prefix: "[test-selection-audit] "
    labels: [area-testing, area-pipelines]
    # Assigning `copilot` starts a Copilot coding agent session on the filed
    # issue, which implements and validates the fix and opens a PR for human
    # review. This requires the `GH_AW_AGENT_TOKEN` fine-grained PAT secret;
    # without it the issue is still filed but assignment fails.
    assignees: [copilot]
    # One finding per run. Each filed issue starts a coding agent session and
    # ends in a PR a human must review, so the workflow surfaces only its
    # single highest-confidence finding rather than a batch of candidates.
    max: 1
    # A weekly schedule would otherwise re-file the same finding (and start a
    # duplicate agent session) every run. Titles name the offending rule, so
    # normalized exact matches against open and recently-closed issues are
    # dropped. Fuzzy matches could suppress an unrelated finding whose
    # issue title would not reconcile with the pending row.
    deduplicate-by-title: true

---

# Weekly CI test-selection audit

Audit Aspire PR CI's dynamic test selection and find the **single
highest-confidence** case where either:

- the selector ran **ALL tests** although a narrower safe selection exists, or
- a narrow selection missed a real runtime-only test or job consumer.

If one case clears the confidence bar, file an issue. The issue is assigned to
the Copilot coding agent, so it must be an actionable task specification, not a
speculative report. Do not change repository code yourself.

## Evidence and scope

- Analyze the explicit `${{ github.event.inputs.pr_numbers }}` list when set.
  Otherwise analyze the last `${{ github.event.inputs.lookback_days }}` days,
  defaulting to 14. Explicit PR scope may use older completed selections.
- Use only the deterministic collector output at
  `$RUNNER_TEMP/gh-aw/safeoutputs/test-selection-audit/evidence.json`. Summarize it with
  `jq` before reading individual records. Do not re-enumerate Actions runs,
  jobs, artifacts, or PR comments.
- A record is new evidence only when `selection.status` is `resolved` and
  `selection.creditable` is `true`. `recorded` means the exact run attempt is
  already in memory and must be reused unchanged.
- Treat every other status as a data gap. Never turn pending, missing, invalid,
  stale, ambiguous, truncated, or fork-authored evidence into a finding or a
  memory contribution. If enumeration or pagination was truncated, file no
  issue this run.
- Fork artifacts are untrusted because PR-authored workflow code produced them.
  Report `untrusted-fork-artifact` as a gap; it cannot support a finding or a
  persistent-memory edit.

## Persistent memory

Repo memory is mounted at `/tmp/gh-aw/repo-memory/default/`. Missing files are
normal on the first run.

### `processed-runs.jsonl`

This is the rolling contribution ledger. It contains one row per PR head:

```json
{"pr":20131,"sha":"<full SHA>","run":35802294466,"attempt":2,"all":false,"over_paths":[],"miss_edges":[{"path":"<literal path>","target":"job:extension-e2e"}],"seen":"2026-09-22"}
```

Rules:

- Key rows by `pr` plus full `sha`. `run` and `attempt` identify the selection
  job used as evidence.
- Set `seen` to the protected evidence file's `auditDate`.
- `over_paths` contains distinct literal input paths attributable to an ALL
  result. It must be empty for a narrow result and when selection-time diff
  attribution is unavailable.
- `miss_edges` contains distinct literal `(path, target)` pairs proven absent
  from a narrow result. It must be empty for an ALL result.
- A newer creditable attempt replaces the same head's prior contribution. A
  non-creditable newer attempt does not erase the prior row.
- Reuse `recorded` rows byte-for-byte. Do not manually prune retained rows;
  deterministic compaction already applied the rolling window.

### `watchlist.jsonl`

This is the durable decision ledger. Over-selection rows are keyed by
`(path, kind)` and use `all_runs`. Under-selection rows are keyed by
`(path, kind, target)` and use `miss_runs` plus `consumer_refs`.

Each row records:

- the literal `path`, matching `rule` (or null), `rule_ref`, and `path_ref`;
- `target` and `consumer_refs` for under-selection;
- `first_seen`, `last_seen`, up to three currently credited `example_prs`, and
  the exact rolling count derived from processed heads;
- one lifecycle `verdict`:

| Verdict | Meaning | `ref` / `note` |
| --- | --- | --- |
| `watch` | plausible but below the filing bar | `ref: null` |
| `correct-by-design` | settled intended behavior | `ref: null` |
| `pending-filed` | this run requested an issue | final prefixed title in `note`, `ref: null` |
| `filed` | issue confirmed | issue number in `ref` |
| `in-flight` | an open PR is fixing it | PR number in `ref` |
| `fixed` | the fix merged | PR number in `ref` |

Before analysis, reconcile every `pending-filed` row by searching issues in any
state for its exact normalized title and confirming the body names the same path
and target. Mark an unambiguous match `filed`. If search fails or is ambiguous,
leave it pending. Revert it to `watch` only on a later reliable search that finds
no match.

Carry settled verdicts forward only while their references are current:

- `rule_ref` must still identify the trigger-map source used for the decision.
- `path_ref` must still identify the triggering file version.
- Under-selection `consumer_refs` must still prove both the runtime edge and the
  target's eligibility in regular PR CI.
- If any reference changed, re-derive the decision. If an in-flight PR closed
  unmerged or a previous fix no longer applies, return the row to `watch`, clear
  `ref`, and remove `note`.

The validator independently checks provenance, identities, counters, examples,
dates, lifecycle transitions, and file limits. On any inconsistency or capacity
failure, leave both ledgers unchanged and file no issue.

## Audit stages

### 1. Reconcile memory and inventory evidence

1. Reconcile `pending-filed` rows as described above.
2. Summarize collector statuses, resolved ALL/narrow counts, recorded heads,
   replaced attempts, and data gaps.
3. Analyze every new creditable ALL result, then every new creditable narrow
   result. A quiet or incomplete window is not evidence that selection is
   correct.

### 2. Analyze over-selection

Group ALL results by literal triggering path and rule. Count distinct retained PR
heads and keep up to three examples.

Reject these as `correct-by-design`:

- the exact `run-full-ci` label kill switch reason;
- changes to `tools/SelectTests`, `eng/github-ci/test-trigger-map.yml`, or the
  select-tests action/workflow;
- these broad build inputs:
  `Directory.Packages.props`, `Directory.Build.props`,
  `Directory.Build.targets`, `NuGet.config`, `eng/Versions.props`,
  `eng/Version.Details.xml`, `src/Directory.Build.props`, and `global.json`.

That build-input list is exhaustive. Files such as `.gitattributes`,
`.editorconfig`, `.config/dotnet-tools.json`, `Aspire.slnx`, CI workflows, and
local actions still require consumer analysis.

For `.github/workflows/**` and `.github/actions/**`, inspect the specific file's
real effect. Release-only or issue-only automation can be a valid narrowing
candidate. Follow `.github/instructions/test-trigger-map.instructions.md` and verify
the current consumers rather than assuming every local action requires ALL.

Do not attribute a force-all or merge-base fallback to a path when the selection
artifact has no diff. Prefer safety over savings: narrow only when all consumers
are known and a focused guard can fail if that assumption changes. Byte-affecting
metadata requires an exhaustive reject-new-directive guard, not a test for only
today's directives.

### 3. Analyze under-selection

For each creditable narrow result, inspect both `changedFiles` and
`excludedFiles`. Determine the **effective** selected tests and jobs after Layer
1, conventions, `path_rules`, `affected_project_rules`, `derived_targets`,
`ignore`, and prefiltering.

Focus only on Layer 2 blind spots that the MSBuild project graph cannot see:

- packages loaded dynamically by `aspire add` or generated AppHosts;
- templates and fixtures copied into E2E workspaces;
- polyglot code-generation contracts;
- extension RPC, bootstrap, archive, and CI job inputs;
- other runtime-only file or package consumption.

Search repository source for the changed inputs and enumerate their real
consumers. Do not use trigger-map comments as proof. Prioritize
`src/Aspire.Hosting/**`, `src/Aspire.TypeSystem/**`,
`src/Aspire.Hosting.CodeGeneration.*/**`, `src/Aspire.Dashboard/**`,
`extension/**`, `src/Aspire.Cli/**`, acquisition scripts, and native archive
packaging.

Credit a missing edge only when historical source at the selection-time head
proves all of the following:

1. the input changed or was prefiltered;
2. the runtime consumer edge existed;
3. the omitted `test:<project>` or `job:<job>` was eligible for regular PR CI;
4. the effective selection omitted that target; and
5. the same gap still exists on current `main`.

If any historical element cannot be reconstructed, report an unverified
candidate without incrementing `miss_runs`. A static current gap without a
matching audited PR is worth reporting in the summary but cannot support filing.

### 4. Verify surviving candidates

For each candidate that remains:

- Read the selector implementation, `eng/github-ci/test-trigger-map.yml`,
  `docs/ci/test-trigger-map.md`, and the example PR's actual changed files.
- Check existing watch rows and issues.
- List open PRs and inspect changed files for
  `eng/github-ci/test-trigger-map.yml`; `search_pull_requests` alone cannot find
  unnamed map edits. Also search PR text for the path/rule.
- Read the trigger-map commits that last touched the candidate rule. Keep
  `list_commits` pages small (5-10), then inspect relevant commits and PR
  discussion.
- Match existing mechanisms: `prefilter`, `ignore`, `path_rules`,
  `affected_project_rules`, `derived_targets`, or `groups`. A file with no test
  effect normally belongs in `ignore`, not a fake narrowed target.
- Reject a fix that history shows was already tried and reverted. Look for
  sibling consumers that prior work may have missed.

### 5. Apply the filing bar

A candidate may be filed only when all are true:

- the exact responsible rule or selector code path is identified;
- real consumers are enumerated from source;
- the scoped map/selector change and mechanism are known;
- a focused regression test is named, including any existing guard whose
  contract must change;
- historical evidence supports the proposal and does not show a revert; and
- no open issue, open PR, or still-applicable merged fix already tracks it.

Choose at most one candidate. Prefer a proven missed consumer over comparable CI
savings, then prefer clearer evidence and greater retained impact. Filing none is
a successful result and is better than starting weak agent work.

### 6. Update memory

After both over- and under-selection analysis are complete:

1. Replace or append each new verified processed row using its exact PR, full
   SHA, run, attempt, distinct paths/edges, and protected audit date.
2. Recompute every watch count from retained processed identities. One head
   contributes at most once to each path or edge.
3. Rebuild examples from currently credited heads, cap them at three, and derive
   active first/last dates from those rows.
4. Remove zero-count `watch` rows. Retain settled rows at zero so known decisions
   are not rediscovered.
5. Preserve lifecycle state only after rechecking the current rule, path,
   consumer, target, and referenced issue/PR.

Keep JSONL rows one-line and schema-only. Verify both files fit configured file
and patch limits before emitting `create-issue`.

Writing memory is required even when no issue qualifies. Before calling
`noop` or `create-issue`, run:

```shell
node "$RUNNER_TEMP/gh-aw/safeoutputs/test-selection-audit/validate_memory.js" /tmp/gh-aw/repo-memory/default
```

If validation fails, correct the ledgers using the protected evidence and
rerun it. Do not finish with a success summary while required rows are missing.

## Filing an issue

If one candidate clears the bar, read
`.github/workflows/test-selection-audit/issue_instructions.md` and follow it
completely. It defines the task-specification body, title constraints, required
validation, prior-art section, PR-description requirement, and automated-analysis
caveat.

Write the matching watch row as `pending-filed`, not `filed`, because issue
creation happens after the agent job and may be deduplicated or fail.

## Run summary

Always report:

- analyzed scope, resolved heads, recorded heads, replaced attempts, and data
  gaps (including pending/no-job and outside-lookback counts);
- total selections, ALL count, and top retained ALL triggers;
- every watch path/edge, rolling count, and this run's change;
- rejected candidates with the exact failed confidence criterion, including
  prior reverts and work already in flight; and
- the issue filed, or that no candidate cleared the bar.
