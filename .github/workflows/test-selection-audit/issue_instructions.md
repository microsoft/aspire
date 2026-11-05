## The issue you file

Write the issue as a task specification for the Copilot coding agent that
will be assigned to it — it should be able to start work from the issue
alone, without re-doing your analysis.

Title it so it identifies the offending input and effect, for example
`Narrow build.yml selection to affected CI jobs` for over-selection, or
`Select extension e2e for CLI archive changes` for under-selection.
Use a stable, plain ASCII title of at most 100 characters **including**
the `[test-selection-audit] ` prefix, with only letters, digits, spaces,
periods, and hyphens. Do not put Markdown, mentions, or a second copy
of the prefix in the title passed to `create-issue`. Store the same
prefixed final title in the pending row's `note`; mention full file
paths, target names, and other details in the body. If two missing
targets share a path, distinguish them in the title and body so
deduplication does not collapse different findings.
Titles are deduplicated against open and recently-closed issues, so a stable,
specific title prevents re-filing the same finding on a later run.

The body must contain:

- **Symptom**: the concrete finding — which PR(s)/run(s), what changed,
  and either that the selector ran ALL tests as a result (over-selection)
  or which real consumer's tests the narrow selection missed
  (under-selection). For an ALL result, quote the actual escalation
  reason/log line verbatim in a fenced code block. A narrow result has
  no escalation reason: instead quote its actual selected tests/jobs
  from the deterministic evidence and identify the omitted
  target. Spell out the literal input path and `test:`/`job:` target in
  the body so a pending issue can be reconciled with the right watchlist
  edge. Never fabricate a reason for a narrow result.
- **Evidence**: real example PR(s) that hit this rule, each with the
  file(s) it touched and the before/after **selected test-project and
  job sets**, with counts for each. A missing job can change no test
  count; do not present a zero test delta as no impact. You cannot
  run `tools/SelectTests` yourself in this sandbox, so derive the
  proposed sets from the existing result and map targets (including
  derived targets), label the after-set explicitly as an estimate,
  and do not claim a precise count when it cannot be established;
  the assigned agent establishes exact sets and counts under Required
  validation below. **One clear, unambiguous example is enough** — do not
  pad the issue with additional PRs just to hit a count. Reach for more
  than one only when a single example leaves genuine room for doubt (for
  example, it could plausibly be a one-off rather than a repeating
  pattern); in that case, pull the extra examples and the rolling count
  from `watchlist.jsonl`'s history for this rule rather than searching for
  new ones. A rule that has been climbing for weeks is stronger evidence
  than one seen once — say which case this is. Link the specific
  trigger-map rule or code path responsible, with file and line.
- **Root cause**: why the current rule is broader (or narrower/missing)
  than necessary, in one or two plain-language sentences. This is the
  sentence a reviewer should be able to quote back to explain the change
  in one breath — write it so it stands on its own, without the reader
  needing the rest of the issue.
- **Suggested fix**: the specific, scoped change to
  `eng/github-ci/test-trigger-map.yml` (or the selector) — not a rewrite of
  the selection design. Say which mechanism it uses (`prefilter`, `ignore`,
  `path_rules`, `affected_project_rules`, `derived_targets`, or `groups`)
  and cite a prior commit that used the same shape, so the assigned agent
  follows established convention rather than inventing one. Name the test to
  add or update under `tests/Infrastructure.Tests/TestTriggerMap/` to pin the
  new behavior, and note whether `docs/ci/test-trigger-map.md` needs a
  matching update.
- **Prior art**: the commits you consulted for this rule and what they
  establish. If history shows a related change that was reverted, say so
  and explain why this proposal is different.
- **Required validation** (the assigned agent must do this before opening a
  PR, and must not claim success without it):
  - Run `tools/SelectTests` against the changed-file lists from **every**
    example PR named in Evidence, not just one, and report selected test
    projects **and jobs** before and after the change for each.
  - Run the guard tests:
    `dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- --filter-namespace "*.TestTriggerMap" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"`
  - Open the result as a **draft** PR that links back to this issue.
- **PR description requirement**: tell the assigned agent explicitly that
  the PR it opens must carry this issue's Root cause sentence and the
  Evidence examples forward into its own description, not just a diff
  summary — state it in the issue body as an instruction the assigned
  agent will follow, e.g. "Your PR description must restate why the old
  rule was wrong and name the real PRs this would have helped, with their
  before/after test-project and job sets, so a reviewer can judge the
  change without re-deriving your analysis." A reviewer approving a
  trigger-map change should not have to re-open this issue to find out why.
- **Unvalidated-analysis caveat**: state that this issue came from an
  automated audit and the suggested fix has not been validated by running
  the selector or tests. If validation contradicts the analysis here, the
  assigned agent should say so on the issue and not force the change
  through.
- Footer: `<sub>Automated by the weekly CI test-selection audit workflow.</sub>`
