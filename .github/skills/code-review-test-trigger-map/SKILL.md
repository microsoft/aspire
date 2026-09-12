---
name: code-review-test-trigger-map
description: "Review pull requests that change Aspire's selective-CI test trigger map or its inputs. Use when a PR touches eng/github-ci/test-trigger-map.yml, eng/github-ci/ci-skip-entirely-patterns.txt, selector code under tools/SelectTests/**, selector/workflow gates, reusable workflows, run_* outputs, loose CI/test inputs, or adds, removes, or renames test projects, CI jobs, or scripts in a way that may require trigger-map maintenance. Checks for missed test coverage (under-selection), wasted CI (over-selection), broken run_* wiring, wrong skip/glob semantics, and weak regression tests."
---

# Reviewing test-trigger-map changes

Apply this skill when the diff under review touches any of:

- `eng/github-ci/test-trigger-map.yml`
- `eng/github-ci/ci-skip-entirely-patterns.txt`
- selector code under `tools/SelectTests/**`
- `.github/workflows/**` gates, reusable workflows, job `needs:` edges, or `run_*` outputs
- test projects added, removed, or renamed
- scripts, configuration, baselines, or other loose files consumed by CI or a test but
  not expressed as an MSBuild project input

If none of those are touched, this skill does not apply.

Follow the Aspire review policy: **only high-confidence, diff-anchored comments**. No style
nits, no praise, no speculative "consider" suggestions.

## Authoritative sources — read before commenting

Do not review from memory. Open what the specific diff needs:

| Question | Read |
|---|---|
| Map vocabulary, layers, maintenance rules | `docs/ci/test-trigger-map.md` |
| Selector engine behavior, prefilter vs. ignore | `docs/ci/test-trigger-selector-design.md` |
| Current rules and their `reason:` comments | `eng/github-ci/test-trigger-map.yml` |
| Skip-gate patterns and their glob semantics | `eng/github-ci/ci-skip-entirely-patterns.txt`, `.github/actions/check-changed-files/action.yml`, `tools/SelectTests/ChangedFileFilter.cs` |
| Gates, `run_*` outputs, reusable workflow calls | `.github/workflows/tests.yml` and the called workflow |
| Existing regression coverage | `tests/Infrastructure.Tests/TestTriggerMap/` |

## Step 1 — classify every changed input

For each path added, removed, or re-routed by the diff, decide which bucket it belongs to:

1. **Layer 1 (ProjectGraph-owned)** — a project in the `Aspire.slnx` graph evaluates the file
   (`Compile`, `Content`, `None`, `EmbeddedResource`, `AdditionalFiles`, imports, or a
   `ProjectReference` edge). **Do not ask for a manual map rule here**; requesting one for an
   ordinary project input or a new `ProjectReference` is a false positive.
2. **Layer 2 blind spot** — workflow implementations, scripts invoked by CI, checked-in
   baselines, configuration read at runtime, `playground/**`, template placeholders, and
   projects outside `Aspire.slnx`. These need a curated rule.
3. **Broad shared input** — invalidates precise routing, so `ALL` is correct.
4. **Prefiltered / no PR-CI consumer** — dropped by `prefilter` or accounted for by `ignore`
   with a comment saying which case applies.
5. **Dedicated or unconditional workflow** — validated outside the selector; must not be
   turned into a selector target.

## Step 2 — trace the real consumer

Never accept a rule (or its removal) because a file "looks related". Follow the chain:

- project inputs and the `ProjectReference` closure for Layer 1 claims;
- script invocations (`run:` steps, `uses:` of local actions) for loose files;
- `uses:` of reusable workflows and job `needs:` edges for job targets;
- the `run_*` output that gates the job in `tests.yml`;
- for tests: which test class actually reads the file.

Name that consumer in any comment you post.

## Step 3 — check the routing

- Rules are **additive**; a new rule must not be justified by "another rule already covers it"
  unless that rule's globs actually match (check them literally).
- The target set must be the **narrowest complete** one: every real consumer present, nothing
  extra. `ALL` only for broadly shared inputs.
- A gated `job:` target needs, end to end: a map route → a `run_*` output in `tests.yml` → a
  gate that consumes exactly that output → the job or reusable workflow that implements it. A
  change to a reusable workflow file must route to the job it implements.
- Removing or narrowing a rule is the highest-risk edit in this area. Enumerate what the old
  rule selected and what the new state selects; any dropped target is a coverage regression
  unless another rule provably covers it.

## Step 4 — check glob and skip-pattern semantics literally

Skip patterns and map globs are **not** shell globs. Per
`.github/actions/check-changed-files/action.yml` (ported verbatim in
`tools/SelectTests/ChangedFileFilter.cs`): `**` → any characters including `/`, `*` → any
characters **except** `/`, `.` is literal, and the pattern is anchored.

For every added or edited pattern, evaluate boundary cases by hand:

- a nested path the author probably intended to match (e.g. `src/*/api/**` does **not** match
  `src/Components/Aspire.Foo/api/...` because `*` cannot cross `/`);
- a neighbouring file that must **not** match (e.g. a `*.tscompat.suppression.txt` or
  `*.ats.txt` baseline sitting in the same `api/` folder, which routes to
  `job:typescript-api-compat` / `job:polyglot` and would be silently dropped before both
  layers if the pattern swallows it);
- whether a `keep_routed` carve-out is still required for files the selector routes.

A prefilter/skip match drops the file before **both** layers, so an over-broad skip pattern
silently disables jobs — treat it as a correctness defect, not a cost issue.

## Step 5 — weigh under- vs. over-selection

- **Under-selection** (a change that no longer runs a test or job that validates it) is a
  silent regression. Always report it.
- **Over-selection** (unnecessary `ALL`, needless fan-out) costs CI time. Report it only when
  the diff makes it concrete and the narrower target set is obvious.

## Step 6 — demand a real regression test

Selector/route changes need focused coverage in the right class:

| Change | Test file |
|---|---|
| curated routing in the real map | `TestTriggerMapTests.cs` |
| selector engine behavior | `SelectTestsAcceptanceTests.cs` (synthetic maps) |
| CLI rendering / side channels | `SelectTestsCliTests.cs` |
| action or workflow-gate contracts | `SelectTestsWorkflowTests.cs` |

The assertion must state the **exact invariant**: the complete expected target set, or the
specific required job (for example `Assert.Contains("job:polyglot", targets)`), plus a negative
case where relevant. Flag weak assertions — `Assert.NotEmpty`, "some target was selected", or
asserting only a count — because they stay green while the invariant is lost.

## Step 7 — write the comment

Post only findings you can defend. Each comment must contain:

1. the changed path in the diff it is anchored to;
2. the actual consumer (test project, job, workflow, script);
3. the missing or extra target;
4. a concrete regression scenario: "after this change, a PR touching X runs/doesn't run Y".

Skip anything already raised by an existing review comment on the PR, anything Layer 1 already
owns, and anything you cannot back with a file you actually read.

## Evaluation

Curated cases and the scoring loop for this skill live in [`evals/README.md`](./evals/README.md)
and [`evals/cases.json`](./evals/cases.json).
