# Dependency review evidence

Use this procedure for dependency-update assessment. It extends `pr-testing`
and its CI-infrastructure reference; it is not a separate testing framework.

1. Record base/head SHA, changed manifests, resolved versions, and relevant
   documented constraints. Check the head again before presenting results.
   Run `python3 eng/dependency-review/check.py` and its focused tests
   (`python3 -m unittest discover -s eng/dependency-review -p 'test_*.py'`).
   Reuse the extension's exact-feed check:
   `(cd extension && node scripts/validate-lockfile-registry.cjs)`.
   Preserve nonzero results; do not remove holds or switch feeds to make CI green.
2. Identify **required consumers** from project references, runtime-only package
   loading, templates, generators, deployment outputs, host versions, and supported
   OS/architectures. Use the existing test-trigger map and workflow graph; this
   consumer list is independent of what the selector actually chooses.
3. Collect the selector output and **selected jobs**. Then inspect job steps,
   test results and artifacts at this head, recording **executed successfully**,
   **blocked** (with failing prerequisite and log), or **unexercised** (not selected,
   skipped, pending, cancelled, or no relevant scenario). A successful wrapper
   with skipped test steps does not count as successful scenario execution.
   Classify each consumer separately: a queued/pending/skipped job is
   **unexercised**, not **blocked**, unless evidence identifies its failed or
   denied prerequisite. Do not copy an aggregate row's state to every consumer.
4. For generated workflows, use the installed gh-aw CLI and compilation command
   in `validate-agentic-workflows.yml`; compare committed outputs after compiling
   in an isolated workspace. Wrapper SHA and explicit CLI version need not match;
   report generated drift only with compiler/diff evidence. Follow the existing
   action allow-list rules: denied policy access means **unverified**, not a
   confirmed violation. Privileged or main-only lanes remain unexercised unless
   equivalent execution evidence is available; never run PR code with elevated
   credentials to bypass a feed or policy restriction.
5. Review resolved dependency graphs/advisories and upstream migration guidance
   for relevant changes. A version-range selector is not an installed version.
   An advisory lookup or model review does not certify upstream safety.
   Installation HTTP 401 is a prerequisite blockage, not proof of incompatible
   TypeScript, ESLint, JSON-RPC, or other packages.
6. Present an evidence table with columns **Consumer / Required scenario /
   Selected job / Execution state / Evidence (SHA, run, step, artifact) / Gap**.
   Keep objective constraint findings separate from compatibility hypotheses.
   Do not assign numeric confidence or certify compatibility from aggregate CI.
   State precisely which behavior was exercised and what remains unknown.
   For dependency reports, replace the generic `PR VERIFIED` verdict with
   objective findings and the consumer execution/gap table, even when CI is green.
   Record a prerequisite gap once in that table; do not duplicate it as several
   review defects. A bounded advisory caveat does not erase a successful tested
   scenario or establish an advisory finding.

For experimental copies, preserve source PR/head provenance, mark every draft
`IGNORE — DO NOT MERGE`, and label reduced reproductions versus full diff copies.
Do not post on or modify original PRs. If requesting Copilot review on a draft,
verify a completed review exists and record its head/author; a request or skipped
organization-funded review workflow is not a completed review. Distinguish that
review from your own assessment.
