# Evaluation Rubric — Test-Selection Redesign (data/policy split)

> **Companion to [`test-selection-redesign.md`](./test-selection-redesign.md)** (the design)
> and [`test-selection-redesign-plan.md`](./test-selection-redesign-plan.md) (the implementation
> plan). This is a review aid, not a spec: use it in a fresh session to judge the design and
> its eventual implementation. Line/file citations were grounded against the tree at authoring
> time — re-confirm before relying on a specific line number.

**Use this in a fresh session** to judge (a) the design spec
`docs/specs/test-selection-redesign.md` and (b) its eventual implementation in
`tools/TestSelector/`. Two scored sections: **Design Review** and
**Implementation Review**, then **Cases the schema still can't express**, then a
**Go/No-Go gate**.

Each criterion has: **Check** (what) · **Verify** (command/file/test/question) ·
**PASS/FAIL** · **Severity** (blocker / important / nice-to-have).

## System facts this rubric is grounded in (read these first)

- Engine: `tools/TestSelector/TestEvaluator.cs` — `EvaluateAsync` step order
  (ignore → trigger-all → categories → mappings → unmatched-fallback →
  dotnet-affected → `FilterAndCombineTestProjects` → `CheckMatchedButZeroProjects`
  → `AddCategoryForcedTestProjects`). Lines ~103–242.
- Suppress = `RestrictedProjectFilter.Apply` (`tools/TestSelector/Analyzers/RestrictedProjectFilter.cs:40-66`),
  called from `FilterAndCombineTestProjects` (`TestEvaluator.cs:746-760`).
- Force = `CollectCategoryForcedTestProjects` / `AddCategoryForcedTestProjects`
  (`TestEvaluator.cs:789-851`).
- Config model: `tools/TestSelector/Models/TestSelectorConfig.cs`
  (`RestrictedTestProjects` :75; `CategoryConfig.TestProjects` :229).
- `run_<category>` derivation: `Models/TestSelectionResult.cs:179-234`
  (`run_integrations` merged with `AffectedTestProjects.Count > 0`; other
  categories taken straight from `Categories[name]`).
- Configs: `eng/scripts/test-selection-rules.audit.json` (rich, dormant),
  `eng/scripts/test-selection-rules.json` (minimal active pilot),
  schema `eng/scripts/test-selection-rules.schema.json`.
- Dual enforcement (PowerShell): `eng/scripts/filter-test-matrix-by-scope.ps1`
  (`$restrictedSet` subtraction lines 280–320). **NB:** the active
  `filter_matrix` step in `.github/workflows/tests.yml:349-368` does **not** pass
  `-RestrictedTestProjects` — the PS restricted path is currently dormant.
- CI consumption: `.github/workflows/tests.yml` — `affected_test_projects`
  (:22, :233, :318, :364, :706+) and `run_<category>` (:229-232, :313-316).
- Regression net: `tests/Infrastructure.Tests/TestSelector/Integration/AuditFixtureTests.cs`
  — **hermetic, no dotnet-affected**; its `EvaluateAgainstRules` stops at
  categories + mappings + unmatched (lines 94-150). It does **not** exercise
  restricted suppression or forced/runtime edges today.
- cli_e2e gap is real: `tests/Aspire.Cli.EndToEnd.Tests/*.csproj` only
  `ProjectReference`s `Aspire.TestUtilities` (:65). It *does* `<Compile>` /
  `<EmbeddedResource>` `src/Aspire.Cli/Resources/*` — so resource changes are
  dotnet-affected-visible, but `src/Aspire.Cli/Commands/**` etc. are **not**.

---

## Design Review

### D1 — cli_e2e runtime-edge actually closes the gap *(blocker)*
- **Check:** The three `runtime` edges (`src/Aspire.Cli/**`,
  `src/Aspire.Hosting/**`, `eng/clipack/**` → cli_e2e) cause selection without a
  `ProjectReference`, and the engine treats `runtime` edges identically to
  declared edges for *selection* (spec §"Proposed model", lines 168-204).
- **Verify:** Read selection pseudocode (`test-selection-redesign.md:212-235`).
  Confirm step (1) declared-edge match adds `edge.to` regardless of dotnet-affected.
  Confirm a NON-resource path (`src/Aspire.Cli/Commands/RunCommand.cs`) is the
  worked example, not a resource file (resource files pass even without the edge —
  false green).
- **PASS:** Runtime edge is an ordinary edge in step (1); non-resource CLI change
  selects cli_e2e. **FAIL:** runtime edges only consulted after dotnet-affected, or
  gap only closed for resource files.

### D2 — straight vs broad source→test coupling both expressible *(blocker)*
- **Check:** 1:1 integration coupling (`src/Components/{name}` → one test) AND
  fan-out coupling (core hosting / CLI → many tests) are both first-class; the
  engine doesn't collapse one into the other.
- **Verify:** `mappings` generator (`:163-166`) covers the `{name}` 1:1 case;
  `edges` (`:170-174`) the explicit case. Confirm both feed the same internal edge
  list (`:196-200`) but a single broad source can name multiple `to` targets.
- **PASS:** both shapes representable without abusing the other. **FAIL:** fan-out
  requires N copy-paste mappings, or 1:1 must be expressed as broad edges.

### D3 — transitive / broad triggers expressible *(important)*
- **Check:** "triggered by all that" couplings — cli_e2e ← CLI ∧ Hosting ∧
  clipack; integration test ← its src; core change fans out — are expressible when
  the project graph can't see them.
- **Verify:** Confirm multiple edges may target the same test (`:170-174` shows 3→1).
  Ask: can a coupling that is *neither* in the project graph *nor* a glob edge be
  expressed? List any that still can't (feed §"Cases the schema can't express").
- **PASS:** every named coupling is a glob edge or a graph edge. **FAIL:** a known
  coupling has no representation.

### D4 — suppress (`inferDeps:false`) reproduces today's behavior exactly *(blocker)*
- **Check:** `inferDeps:false` for a test == today's `restrictedTestProjects`:
  drop dotnet-affected-inferred edges, keep explicitly-declared edges.
- **Verify:** Compare spec selection step (2) (`:225-227`, "only for tests that
  trust inference") against `RestrictedProjectFilter.Apply` semantics
  (`RestrictedProjectFilter.cs:57-63` — restricted kept only if mapping-resolved).
  Confirm equivalence: declared edge ⇔ "mapping-resolved"; inferred edge ⇔
  dotnet-affected.
- **PASS:** semantics provably identical (a declared/runtime edge still selects an
  `inferDeps:false` test; bare dotnet-affected does not). **FAIL:** subtle drift,
  e.g. `inferDeps:false` also drops declared edges, or keeps inferred ones.

### D5 — dual enforcement kept from drifting (single-sourced data) *(important)*
- **Check:** Design's answer to C# selective-scope vs PowerShell RunAll-scope
  enforcement of restricted projects (`filter-test-matrix-by-scope.ps1:280-320`).
- **Verify:** Read open-decision #3 (`:315-324`). Confirm it (a) does NOT
  consolidate the two engines (correct — would force C# to enumerate the full
  matrix under RunAll, a live behavior change) but (b) DOES require the PS filter's
  restricted input be derived from the same graph the C# engine reads. Note today
  the active path doesn't even wire `-RestrictedTestProjects` (`tests.yml:349-368`)
  — design must say how the data gets single-sourced.
- **PASS:** single-source-the-data plan is concrete (e.g. selector emits the
  restricted/`inferDeps:false` list; PS consumes that artifact). **FAIL:** "they'll
  stay in sync by convention", or silent consolidation that changes RunAll.

### D6 — over-approximation / conservative-fallback bias preserved *(blocker)*
- **Check:** All existing fallbacks survive and the new engine can't silently
  RUN_NOTHING when something changed.
- **Verify:** Map each fallback to a spec line — no git ref → all
  (`TestEvaluator.cs:419-430`), dotnet-affected failure → all (`:439-451`),
  unmatched file → all (`:556-572`), integrations matched-but-zero → all
  (`:603-634`). Confirm spec keeps them (`:289-298`, `:120-130`). Confirm
  `RUN_NOTHING` only when all files matched `ignore` (`:214-215`), never as a
  silent default.
- **PASS:** every fallback enumerated and retained; unexplained-file → RUN_ALL
  (`:229-231`). **FAIL:** any fallback dropped, or an unexplained file can yield
  an empty selection.

### D7 — `run_<category>` un-disagreeable with the matrix *(blocker)*
- **Check:** Deriving `run_<category>` from selected tests genuinely prevents the
  original cli_e2e boolean-vs-matrix disagreement.
- **Verify:** Read `:237-241`. Today `run_cli_e2e = Categories["cli_e2e"]`
  (`TestSelectionResult.cs:200-203`), set separately from matrix membership; they
  agree only because triggerPaths ⊇ testProjects. **Critical question:** to "group
  selected tests by category label" the schema needs an explicit test→category
  map. The proposed schema has none (cli_e2e test isn't tagged `cli_e2e`). Does the
  design add that tag, or does it silently fall back to the old triggerPath re-match
  (`UpdateCategoriesFromTestProjects`, `TestEvaluator.cs:769-787`)?
- **PASS:** design makes test→category explicit (edge carries a category label, or
  `jobCategories` list member tests) so boolean = derived purely from selection.
  **FAIL:** derivation still depends on a parallel glob re-match → can disagree
  again. *(See also §Cases-can't-express item 8.)*

### D8 — verb reduction is real, not cosmetic *(important)*
- **Check:** `force`, `restrict`, `exclude` actually collapse; the policy "verbs"
  in data shrink.
- **Verify:** Spec §"What collapses" (`:243-251`). Confirm `force`→runtime edge,
  `restrict`→`inferDeps:false`/negation, `excludePaths`→negation. Count post-design
  data verbs vs today (`ignore`, `triggerAll`, mapping, `restrictedTestProjects`,
  `testProjects`, `excludePaths`).
- **PASS:** net fewer policy verbs in JSON; remaining fields are relationships +
  facts. **FAIL:** verbs renamed, not removed (e.g. `inferDeps` + per-edge `!` +
  `jobCategories.when` exceptions reintroduce conditionals).

### D9 — schema simplification: redundant concepts merged *(nice-to-have)*
- **Check:** Spec's own open simplifications.
- **Verify:** Ask each: is `mappings` vs `edges` a real distinction (generator vs
  explicit) or redundant? Could `inferDeps:false` and per-edge `!` be one concept
  (open decision #1, `:304-308`)? Is `jobCategories` redundant with derived
  `run_<category>` (`:185-190` vs `:237-241`)?
- **PASS:** each kept distinction is justified by a concrete case; redundancy
  removed or explicitly deferred with reason. **FAIL:** two mechanisms for one job
  with no rationale.

### D10 — migration / rollback story is safe *(blocker)*
- **Check:** Land in audit config first, validate against real PRs, promote
  separately; no live CI change until promotion.
- **Verify:** Open-decision #5 (`:335-339`) + checklist (`:341-360`). Confirm the
  active config (`test-selection-rules.json`) stays minimal until promotion, and a
  rollback = revert the active-config promotion commit only.
- **PASS:** audit-first, reversible, blast radius bounded to the audit lane until
  promotion. **FAIL:** schema/engine change flips active behavior in one step.

### D11 — implementation language stays C# *(nice-to-have)*
- **Check:** Split done in existing C# tool; language change treated as separate.
- **Verify:** Open-decision #4 (`:326-333`). **PASS:** keeps NativeAOT + unit
  suite; JS rewrite deferred. **FAIL:** couples the split to a rewrite that drops
  the test suite.

---

## Implementation Review

> For every policy branch, the bar is: **name the test whose assertion goes red on
> a one-line revert.** "Covered by existing tests" is not accepted without naming
> the asserting line.

### I1 — runtime-edge selection has a revert-red test *(blocker)*
- **Check:** A test proving a NON-resource `src/Aspire.Cli/**` change selects
  cli_e2e and that deleting the runtime edge makes it red.
- **Verify:** Look for a new test in `EndToEndEvaluationTests.cs` /
  `AuditFixtureTests.cs` that feeds `src/Aspire.Cli/Commands/*.cs` (or `eng/clipack/**`)
  and asserts cli_e2e ∈ selection. Today only `CollectCategoryForcedTestProjects`
  unit tests exist (`EndToEndEvaluationTests.cs:1337-1357`) — they test the helper,
  not the end-to-end edge. Mentally revert the edge: does any test fail?
- **PASS:** named test asserts cli_e2e selection from a non-resource CLI path and
  is edge-load-bearing. **FAIL:** only the helper is tested, or the test uses a
  resource file (false green).

### I2 — `inferDeps:false` suppression has a revert-red test *(blocker)*
- **Check:** Acquisition.Tests & Infrastructure.Tests are suppressed when reached
  only via inference, kept when reached via a declared edge.
- **Verify:** Confirm `RestrictedProjectFilterTests.cs` (6 tests) port to the new
  mechanism AND that there's an `EvaluateAsync`-level test (not just the isolated
  filter) proving an inferred-only hit is dropped. Today no `EvaluateAsync`-level
  suppression test exists.
- **PASS:** named unit + integration test; flipping `inferDeps` to `true` makes
  the integration test red. **FAIL:** only the isolated filter is tested.

### I3 — derived `run_<category>` ↔ matrix consistency test *(blocker)*
- **Check:** A test asserts `run_cli_e2e` is true **iff** the cli_e2e test project
  is in `affected_test_projects` (the exact failure that caused the original bug).
- **Verify:** Extend `TestSelectionResultTests.cs` (which today covers
  `run_integrations` derivation, lines 156-282) with a cli_e2e case. Assert both
  channels together.
- **PASS:** named test couples boolean and matrix membership; they can't be set
  independently. **FAIL:** boolean and matrix asserted in separate tests that can
  pass while disagreeing.

### I4 — every fallback branch has a named test *(blocker)*
- **Check:** no-git-ref, dotnet-affected-failure, unmatched-file,
  matched-but-zero, all-ignored → each has a test.
- **Verify:** Existing: `Evaluate_UnmatchedFile_TriggersConservativeFallback`
  (`EndToEndEvaluationTests.cs:144`), `Evaluate_AllFilesIgnored_NoTestsRun` (:174),
  `Evaluate_AuditConfigOnlyChange...` (:105). **Find/confirm** tests for
  no-git-ref (`TestEvaluator.cs:419`), dotnet-affected-failure (`:439`),
  matched-but-zero (`CheckMatchedButZeroProjects`, `:603`). Flag any branch with
  no asserting test.
- **PASS:** all five named. **FAIL:** any branch (esp. matched-but-zero,
  dotnet-affected-failure) unasserted.

### I5 — AuditFixtureTests exercise the reshaped mechanisms *(blocker)*
- **Check:** The real-PR fixtures still pass AND now cover runtime-edge + suppress,
  not just categories+mappings.
- **Verify:** `AuditFixtureTests.EvaluateAgainstRules` (`:94-150`) is hermetic and
  stops before suppression/force/dotnet-affected. After the redesign it must either
  (a) extend the replicated pipeline to apply edge selection + `inferDeps`, or
  (b) add fixtures whose expected outcome depends on a runtime edge / suppression.
  Confirm the migrated `audit.json` keeps every fixture row green.
- **PASS:** fixtures cover reshaped paths and all rows green. **FAIL:** fixtures
  unchanged → reshaped mechanisms have zero real-PR coverage.

### I6 — dual-enforcement disagreement is test-catchable *(important)*
- **Check:** A test catches the C# selector and the PS RunAll filter disagreeing on
  the restricted/suppressed set.
- **Verify:** Look for a test that feeds the same `inferDeps:false` list to both
  the C# path and `filter-test-matrix-by-scope.ps1` and asserts equal exclusion.
  Confirm the PS input is now sourced from config, not a hand-passed param
  (`tests.yml` should wire it).
- **PASS:** single-sourced + a test asserting parity. **FAIL:** two lists, no
  parity test (today's state — easy to drift).

### I7 — schema + model migrated together, no manual API/generated edits *(important)*
- **Check:** `test-selection-rules.schema.json` and `TestSelectorConfig` gain
  `edges`/`inferDeps`/`jobCategories`; both configs validate; no edits to
  `*/api/*.cs`.
- **Verify:** `git --no-pager diff` the schema + model; run the selector against
  both JSONs. Confirm `mappings`/`ignore`/`runEverything`/`testProjectPatterns`
  retained (`:343-344`).
- **PASS:** schema/model/config coherent; selector loads both. **FAIL:** schema
  drift from model, or a config that fails to load.

### I8 — engine is the single decision site *(important)*
- **Check:** All `if`/precedence/force/suppress lives in one named, linear engine
  (spec §Layer 2); JSON carries no conditionals.
- **Verify:** Grep the migrated `audit.json` for anything conditional. Read the new
  `selectTests` implementation — confirm it matches the pseudocode order
  (`:212-235`) and each rule is a named helper (keep `RestrictedProjectFilter`
  semantics as a named function per `:347-349`).
- **PASS:** data hermetic; one engine, named branches. **FAIL:** policy leaked back
  into JSON (e.g. `when`/`unless`/precedence expressions).

### I9 — full suite + verification evidence *(blocker)*
- **Check:** `Infrastructure.Tests` (which hosts the TestSelector tests) passes;
  the PR/commit cites exact commands + output.
- **Verify:**
  `dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- --filter-namespace "*TestSelector*" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"`.
  Also run the PS Pester/standalone tests for `filter-test-matrix-by-scope.ps1` if
  present.
- **PASS:** green run with quoted output. **FAIL:** "tested manually" with no
  commands/output, or skipped TestSelector namespace.

### I10 — docs updated *(nice-to-have)*
- **Check:** `eng/scripts/test-selection-rules.README.md`,
  `docs/conditional-tests-run.md` updated; spec marked implemented (`:359-360`).
- **Verify:** Diff those files; confirm the `force`/`restrict`/`exclude` sections
  are replaced with edge/`inferDeps` guidance.
- **PASS:** docs match new schema. **FAIL:** docs still describe deleted verbs.

---

## Cases the schema STILL can't express (brainstorm — #7)

For each: does the **proposed** design handle it, and should it change the design?

1. **Shared test-helper fan-out (`tests/Shared/**`, `tests/Aspire.TestUtilities/**`).**
   A change to a shared helper should fan out to every dependent test. Today this
   rides dotnet-affected (real `ProjectReference`s) + the integrations category
   (`AuditFixtureTests.cs:73-87`). The new `mappings`/`edges` are source→test globs;
   a "this node fans out to all its graph dependents" relationship isn't expressible
   as data — it's still implicit in dotnet-affected. **Design handles:** partially
   (via inference). **Should change?** Document that shared-helper fan-out depends on
   inference, so `inferDeps:false` must never be set on a shared-helper consumer.

2. **`run X only if A AND B changed` (conjunction).** Edges are pure OR (any `from`
   matches → select). No AND. Probably *correct* (over-run bias), but if a future
   case needs it, it's a code change, not data. **Should change?** No — note
   explicitly that conjunction is intentionally a code-only policy.

3. **Path-scoped negation (vs node-scoped `inferDeps:false`).** `inferDeps:false`
   nukes *all* inferred edges for a test. If a test needs "ignore inferred edge from
   X but keep the one from Y", only per-edge `!` (open decision #1) does it. The two
   restricted projects today are genuinely "all inferred edges are noise", so
   node-scope suffices now — but the schema should reserve per-edge negation before
   a third case forces a migration.

4. **Many-to-one / one-to-many runtime edges.** Many-to-one is fine (3 edges → cli_e2e).
   One-to-many (one source → many runtime tests) means repeating the source across N
   edges. **Should change?** Allow `to` to be an array, or it gets verbose.

5. **Transitively-reachable runtime edges (A runtime→B build→C).** The engine does
   one hop: declared edges + one dotnet-affected pass. A runtime edge whose target
   itself pulls a build-graph subtree is NOT transitively expanded for the runtime
   side. cli_e2e doesn't need this today, but "runtime-depends on a thing that
   build-depends on others" is unmodeled. **Should change?** Call out that runtime
   edges are non-transitive by design; if transitivity is ever needed it's a new
   policy line.

6. **Per-OS / per-job-category selection.** Selection yields a flat test set; the
   matrix split into linux/windows/macos/cli-archive lanes happens later in
   `split-test-matrix-by-deps.ps1` + `filter-test-matrix-by-scope.ps1`. The graph
   can't say "run test T on Windows only." Today that's matrix metadata, not the
   selector's job — fine, but the design should state the boundary so no one tries
   to encode OS in edges.

7. **Ordering / priority.** No way to say "run cli_e2e before integrations" or
   prioritize. Almost certainly out of scope (CI orchestrates), but worth an
   explicit non-goal.

8. **Explicit test→category label (the D7/I3 gap).** This is the most
   consequential. To derive `run_<category>` from selected tests *without*
   re-matching globs, a selected test must carry its category. The proposed schema
   has no such tag — `jobCategories` only carry `when` globs for standalone jobs,
   and matrix categories (cli_e2e, integrations) have no member-test list. **Design
   handles:** NO — it still leans on the old triggerPath re-match
   (`UpdateCategoriesFromTestProjects`). **Should change?** **Yes** — see callout
   below.

9. **`runEverything` / `ignore` overlap precedence.** A path in both `ignore` and a
   category `when` (e.g. `.github/workflows/**` is ignored but polyglot triggers on
   it) needs the rescue logic (`TestEvaluator.cs:127-138, 257-292`). The proposed
   data model doesn't surface this precedence; it's engine policy. Confirm the new
   engine keeps an explicit rescue step, or polyglot silently breaks.

---

## ⚑ Genuinely-missing case that should change the design

**Item 8 — the schema has no explicit test→category label, so the headline claim
("derive `run_<category>` from selected tests → boolean and matrix can never
disagree", spec `:237-241`) is not actually achievable as written.**

Deriving the boolean from the selection still requires mapping
`tests/Aspire.Cli.EndToEnd.Tests` → `cli_e2e`. The only mechanism for that today is
re-matching the test project path against the category's `triggerPaths` globs
(`UpdateCategoriesFromTestProjects`, `TestEvaluator.cs:769-787`) — a *parallel*
computation, which is exactly the class of bug the redesign claims to kill. The
design should add a first-class label on the edge target (e.g.
`{ "to": "...cli_e2e.csproj", "category": "cli_e2e" }`) or have matrix
`jobCategories` enumerate their member test projects, so the boolean is a pure
projection of the selected set with no second glob pass. Without this, D7/I3 cannot
truthfully PASS.

---

## Go / No-Go Gate (all must PASS to ship)

| ID | Blocker criterion |
|----|-------------------|
| D1 | Runtime edge selects cli_e2e for non-resource CLI changes |
| D4 | `inferDeps:false` reproduces `restrictedTestProjects` exactly |
| D6 | All conservative fallbacks preserved; no silent RUN_NOTHING |
| D7 | `run_<category>` derivation is provably un-disagreeable with the matrix *(blocked by missing test→category label — see callout)* |
| D10 | Audit-first migration, single-step reversible rollback |
| I1 | Revert-red test for runtime-edge selection (non-resource path) |
| I2 | Revert-red test for `inferDeps:false` suppression at `EvaluateAsync` level |
| I3 | Test couples `run_cli_e2e` to cli_e2e matrix membership |
| I4 | Every fallback branch has a named asserting test |
| I5 | `AuditFixtureTests` exercise runtime-edge + suppress and stay green |
| I9 | Full TestSelector suite green, with cited commands + output |

**Production-CI blast-radius lens (apply throughout):** the worst outcome is a test
silently *not* running. Bias every ambiguous call toward over-select / over-run.
The single most valuable thing to land first is **D7/I3 + the test→category label**
(it both closes the original bug class and unblocks the gate). Migration safety
(D10) bounds blast radius to the dormant audit lane until an explicit promotion
commit — keep it that way.
