# Test Selection Redesign Implementation Plan

> **Status: implemented.** Companion to [`test-selection-redesign.md`](./test-selection-redesign.md)
> (the design) and [`test-selection-redesign-eval.md`](./test-selection-redesign-eval.md) (the
> review rubric). Retained as the historical implementation record; the shipped behavior is
> documented in [`../conditional-tests-run.md`](../conditional-tests-run.md) and the
> [config README](../../eng/scripts/test-selection-rules.README.md). The open **D7** question
> below was resolved in favor of a first-class test→category label in *data* (the projected
> category edge), not the in-code membership check this plan sketched.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign Aspire test selection so JSON declares relationships and C# owns selection policy, while keeping CI green and rolling out through the audit selector first.

**Architecture:** Add a normalized relationship graph over existing config, then switch `TestEvaluator` to one policy path: ignored/run-all preflight, declared edges, inferred `dotnet-affected` edges gated by `inferDeps`, conservative fallback, and category output derivation. Keep the PowerShell matrix filter as a separate enforcement engine, but make it read suppression data from the same rules file instead of a copied list.

**Tech Stack:** C# 13 / .NET 10, `System.Text.Json`, `Microsoft.Extensions.FileSystemGlobbing`, `dotnet-affected`, PowerShell 7, xUnit v3 / Microsoft.Testing.Platform.

---

## Grounding: files and symbols read

- Design: `docs/specs/test-selection-redesign.md:1-394`.
- Core engine: `tools/TestSelector/TestEvaluator.cs:103-240`, `:506-576`, `:603-633`, `:706-850`, `:951-960`.
- Current restrict helper: `tools/TestSelector/Analyzers/RestrictedProjectFilter.cs:6-67`.
- Config model: `tools/TestSelector/Models/TestSelectorConfig.cs:12-95`, `:119-143`, `:201-231`.
- Matching helpers: `tools/TestSelector/CategoryMapper.cs:13-222`, `tools/TestSelector/Analyzers/ProjectMappingResolver.cs:14-125`, `:127-293`, `PatternNormalization.cs:11-44`.
- Outputs: `tools/TestSelector/Models/TestSelectionResult.cs:179-234`.
- Rules: `eng/scripts/test-selection-rules.audit.json:1-206`, `eng/scripts/test-selection-rules.json:1-40`, `eng/scripts/test-selection-rules.schema.json:1-125`.
- PowerShell filter: `eng/scripts/filter-test-matrix-by-scope.ps1:31-37`, `:66-67`, `:174-199`, `:280-319`.
- Workflow consumers: `.github/workflows/tests.yml:15-29`, `:41-95`, `:97-234`, `:347-397`, `:683-817`, `:946-958`.
- Tests: `EndToEndEvaluationTests.cs:1000-1446`, `AuditFixtureTests.cs:46-149`, `FilterTestMatrixByScopeTests.cs:27-297`, `TestSelectionResultTests.cs:155-397`.
- Docs: `eng/scripts/test-selection-rules.README.md:1-232`, `docs/conditional-tests-run.md:1-351`.

## Resolved recommendations for the five open decisions

| Decision | Recommendation | Confirm before coding? | Reasoning |
|---|---|---:|---|
| Negation granularity | Implement `inferDeps: { test.csproj: false }` now. Do not add per-edge `!` negation in this pass; keep existing `exclude` arrays for source-pattern exclusions. | **Yes** | Current restricted projects are all-inferred-edges-are-noise cases. Per-edge negation adds parser and precedence surface before there is a concrete Aspire case. |
| Edge direction | Use source-keyed `from` -> `to` edges. | No | Existing `sourceToTestMappings` and `category.triggerPaths` are already source-keyed. Reviewers can read a changed source pattern and see what it selects. |
| Dual enforcement | Keep C# selective selection and PowerShell RunAll matrix filtering separate; single-source suppression data from `inferDeps`. | No | Unifying would change RunAll matrix behavior and force C# to enumerate the full matrix. The safer fix is data de-duplication. |
| Implementation language | Keep the implementation in C#. | No | The current tool is NativeAOT-capable, tested by Infrastructure.Tests, and already integrated in workflow diagnostics. A JS rewrite is orthogonal risk. |
| Migration path | Land behind `test-selection-rules.audit.json` first with short-lived dual-read of old/new config fields; promote active config later. | **Yes** | Audit rollout avoids live CI behavior changes. Dual-read keeps every commit green while audit migrates, but should not become a permanent compatibility promise. |

## Back-compat and migration strategy

Recommended path: **short-lived dual-read, audit-first**.

- Add normalized accessors in `TestSelectorConfig` so the engine can read both shapes:
  - New: `ignore`, `runEverything`, `mappings`, `edges`, `inferDeps`, `jobCategories`.
  - Legacy: `ignorePaths`, `triggerAllPaths`, `sourceToTestMappings`, `categories`, `restrictedTestProjects`, category `testProjects`.
- Treat legacy fields as a transition input, not a durable public contract:
  - `restrictedTestProjects` normalizes to `inferDeps[testProject] = false` when `inferDeps` does not contain that project.
  - category `testProjects` normalizes to `runtime` edges from that category's `triggerPaths` to each listed test project, preserving `excludePaths` as edge excludes.
  - legacy `extension` / `polyglot` categories normalize to `jobCategories` until the audit config is migrated.
- Migrate `eng/scripts/test-selection-rules.audit.json` to the new shape first.
- Keep `eng/scripts/test-selection-rules.json` minimal and behavior-preserving until a separate promotion PR.
- After active promotion and one green CI cycle, remove old fields from configs and decide whether to remove dual-read code in a cleanup PR.

Rationale: a hard cutover is cleaner, but it couples schema, engine, audit config, active config, workflow, and PowerShell changes into one risky commit. The dual-read layer is small and falsifiable if every legacy normalization path has a test.

## Phase 0 — Add v2 schema and config model, no behavior change

**Commit subject:** `feat(test-selector): add relationship graph config model`

**Execution checklist:**

- [ ] Add v2 schema properties while keeping legacy properties.
- [ ] Add v2 model types and normalized accessors in `TestSelectorConfig`.
- [ ] Add config parsing tests for v2 fields, legacy projection, and new-field precedence.
- [ ] Run the Phase 0 validation command.
- [ ] Commit with the subject above.

**Files:**

- Modify: `eng/scripts/test-selection-rules.schema.json:7-125`.
- Modify: `tools/TestSelector/Models/TestSelectorConfig.cs:12-95`, `:119-143`, `:201-231`.
- Test: `tests/Infrastructure.Tests/TestSelector/Models/TestSelectorConfigTests.cs:11-593`.

**Exact change:**

- Add schema properties:
  - `ignore` string array.
  - `runEverything` string array.
  - `mappings` array of `{ "from": string|string[], "to": string, "exclude"?: string[] }`.
  - `edges` array of `{ "from": string|string[], "to": string, "type"?: "build"|"runtime", "exclude"?: string[] }`.
  - `inferDeps` object whose values are booleans.
  - `jobCategories` object whose values are `{ "when": string[], "exclude"?: string[] }`.
- Keep existing schema properties during migration.
- Add model types:
  - `SelectionMappingConfig` for new `mappings`.
  - `SelectionEdgeConfig` for new `edges`.
  - `JobCategoryConfig` for new `jobCategories`.
  - `SelectionEdgeType` can be a string-backed property initially; validate only `build`/`runtime` in normalization tests unless the repo already has a stricter converter pattern.
- Add normalized accessors or methods; suggested names:
  - `GetIgnorePatterns()` returns `Ignore` if non-empty, else `IgnorePaths`.
  - `GetRunEverythingPatterns()` returns `RunEverything` if non-empty, else `TriggerAllPaths`.
  - `GetRelationshipMappings()` returns new `Mappings` plus legacy `SourceToTestMappings` projected to `from`/`to` when new mappings are absent.
  - `GetInferDeps()` returns `InferDeps` plus legacy `RestrictedTestProjects` projected to `false` when absent.
  - `GetJobCategories()` returns new `JobCategories` plus legacy standalone categories.

**Tests to add/update:**

- Add `LoadFromJson_V2GraphFields_ParsesEdgesInferDepsAndJobCategories`.
  - Assert the first edge has `From == ["src/Aspire.Cli/**"]`, `To == "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj"`, `Type == "runtime"`.
  - Assert `config.InferDeps["tests/Infrastructure.Tests/Infrastructure.Tests.csproj"]` is `false`.
  - Assert `config.JobCategories["extension"].When` contains `"extension/**"`.
- Add `LoadFromJson_LegacyFields_ProjectToNormalizedConfig`.
  - Use legacy `sourceToTestMappings`, `restrictedTestProjects`, and `categories.cli_e2e.testProjects`.
  - Assert normalized mappings, `inferDeps:false`, and legacy runtime-edge projection are present.
- Add `LoadFromJson_NewFieldsTakePrecedenceOverLegacyAliases`.
  - If both `mappings` and `sourceToTestMappings` are present, assert normalized mappings use `mappings` only.

**Validation:**

```bash
./restore.sh

dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.TestSelectorConfigTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 1 — Build the internal typed edge graph

**Commit subject:** `feat(test-selector): build typed test selection graph`

**Execution checklist:**

- [ ] Create the graph builder and resolved edge/match types.
- [ ] Reuse or extract existing mapping compilation logic for `{name}` and excludes.
- [ ] Add graph builder tests for build edges, runtime edges, excludes, and de-duping.
- [ ] Run the Phase 1 validation command.
- [ ] Commit with the subject above.

**Files:**

- Create: `tools/TestSelector/Analyzers/TestSelectionGraphBuilder.cs`.
- Modify: `tools/TestSelector/Analyzers/ProjectMappingResolver.cs:14-125`, `:127-293` only if code reuse is simpler than duplicating glob-to-regex logic.
- Modify: `tools/TestSelector/Models/TestSelectorConfig.cs:119-143` if the mapping model needs shared `from`/`to` support.
- Test: create `tests/Infrastructure.Tests/TestSelector/Analyzers/TestSelectionGraphBuilderTests.cs` or add to `ProjectMappingResolverTests.cs:1-316`.

**Exact change:**

- Introduce an internal graph model:
  - `ResolvedSelectionEdge(string FromPattern, string ToProject, string Type, IReadOnlyList<string> ExcludePatterns)`.
  - `ResolvedSelectionMatch(string SourceFile, string SourcePattern, string TestProject, string Type)`.
  - `TestSelectionGraph.ResolveDeclaredEdges(IEnumerable<string> activeFiles)` returns selected projects, matched files, and match details.
- Expand config in two steps:
  1. Convert normalized `mappings` to `build` typed edges.
  2. Append explicit `edges`; default missing `type` to `build`.
- Preserve `{name}` capture behavior from `ProjectMappingResolver` for mappings.
- Normalize path separators and bare filename glob behavior via `PatternNormalization.NormalizeGlob`.
- Apply `exclude` before matching, same as `ProjectMappingResolver.CompiledMapping` does at lines `138-143`, `181-184`.

**Tests to add/update:**

- `BuildGraph_MappingWithNameSelectsConventionalTestProject`.
  - Changed file: `src/Components/Aspire.Redis/RedisExtensions.cs`.
  - Mapping: `src/Components/{name}/**` -> `tests/{name}.Tests/{name}.Tests.csproj`.
  - Assert selected project is `tests/Aspire.Redis.Tests/Aspire.Redis.Tests.csproj` and match type is `build`.
- `BuildGraph_ExplicitRuntimeEdgeSelectsCliE2eProject`.
  - Edge: `src/Aspire.Cli/**` -> CLI E2E test with `type: runtime`.
  - Assert selected projects contains only CLI E2E and match type is `runtime`.
- `BuildGraph_ExcludeSuppressesEdgeMatch`.
  - Edge/mapping from `src/Aspire.Hosting/**` excludes `**/api/*.txt`.
  - Changed file `src/Aspire.Hosting/api/Aspire.Hosting.ats.txt`.
  - Assert no selected projects and no matched files.
- `BuildGraph_DeduplicatesProjectsCaseInsensitively`.
  - Two edges resolve to casing variants of the same project.
  - Assert one selected project.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.TestSelectionGraphBuilderTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 2 — Add the single `selectTests` policy engine

**Commit subject:** `refactor(test-selector): centralize test selection policy`

**Execution checklist:**

- [ ] Add `TestSelectionPolicy` and the inferred-dependency filter helper.
- [ ] Wire `TestEvaluator` to preflight first, then the single policy path.
- [ ] Preserve all conservative fallbacks and the declared-edge fast path.
- [ ] Port restricted-project tests to `inferDeps` semantics and add policy tests.
- [ ] Run the Phase 2 validation command.
- [ ] Commit with the subject above.

**Files:**

- Create: `tools/TestSelector/TestSelectionPolicy.cs`.
- Modify: `tools/TestSelector/TestEvaluator.cs:103-240`, `:506-576`, `:603-633`, `:706-767`, `:880-905`.
- Replace or rename: `tools/TestSelector/Analyzers/RestrictedProjectFilter.cs:6-67` to `InferDepsFilter` / `InferredDependencyFilter`.
- Test: `EndToEndEvaluationTests.cs:1000-1446`, `RestrictedProjectFilterTests.cs:9-104` renamed to `InferDepsFilterTests.cs`.

**Exact change:**

- Keep preflight checks in `TestEvaluator`:
  - no changes (`:116-120`), ignore filtering (`:124-143`), non-applying audit-only (`:145-149`), run-everything (`:151-156`).
- Replace the current split path of category matching, source mappings, dotnet-affected, restrict filtering, and forced test projects with one policy call:
  1. Resolve declared edge matches from the graph.
  2. If all active files are explained by declared edges or standalone job categories, keep the existing mapping-only fast path and skip `dotnet-affected` when no git ref is available.
  3. Otherwise run `dotnet-affected` through existing `RunDotnetAffectedAsync` (`:636-704`).
  4. Filter affected projects to test projects with `TestProjectFilter` (`:712-733`).
  5. Add inferred test projects only when normalized `inferDeps` is absent or `true` for that project.
  6. Add declared-edge test projects regardless of `inferDeps`.
  7. Fall back to run-all when an active file is not explained by declared edges, standalone job-category `when`, or an accepted inferred selection.
- Fold `RestrictedProjectFilter.Apply` semantics into the new helper:
  - A project with `inferDeps:false` is dropped when it came only from inference.
  - The same project is kept when selected by a declared edge.
- Preserve conservative fallbacks:
  - no git ref and inference needed -> run all.
  - `dotnet-affected` failure -> run all.
  - unmatched active file -> run all.
  - matched-but-zero integrations guard remains until Phase 3 replaces category derivation.

**Tests to add/update:**

- `SelectTests_DeclaredEdgeSelectsTestProject`.
  - Assert selected tests contains the declared project and `RunAllTests == false`.
- `SelectTests_RuntimeEdgeSelectsCliE2eWithoutProjectReference`.
  - Changed `src/Aspire.Cli/Program.cs`; no affected projects.
  - Assert selected tests contains CLI E2E. If this regresses, CLI E2E silently drops.
- `SelectTests_InferredProjectAddedWhenInferDepsDefaultsTrue`.
  - Affected projects include `tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj`.
  - Assert selected tests contains it.
- `SelectTests_InferDepsFalseSuppressesInferredOnlyProject`.
  - Affected projects include `tests/Infrastructure.Tests/Infrastructure.Tests.csproj`; `inferDeps` false.
  - Assert selected tests does **not** contain Infrastructure.Tests.
- `SelectTests_DeclaredEdgeOverridesInferDepsFalse`.
  - Declared edge selects Infrastructure.Tests; `inferDeps` false; affected projects empty.
  - Assert selected tests contains Infrastructure.Tests.
- `SelectTests_UnmatchedFileFallsBackToRunAll`.
  - Changed `eng/unknown-tooling-file.txt` with no ignore/runEverything/edge/job match and no accepted affected tests.
  - Assert `RunAllTests == true` and `Reason` contains `Unmatched files`.
- `Evaluate_NoGitRefWhenInferenceNeededFallsBackToRunAll`.
  - Existing `fromRef: null`; active file not fully declared-edge explained.
  - Assert `RunAllTests == true` and reason contains `No git ref`.
- `InferDepsFilter_NormalizesSeparatorsAndCase`.
  - Port old `RestrictedProjectFilterTests.Apply_NormalizesSeparatorsAndCase`.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.EndToEndEvaluationTests" \
  --filter-class "*.InferDepsFilterTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 3 — Derive `run_<category>` outputs from selected tests and standalone jobs

**Commit subject:** `refactor(test-selector): derive category outputs from selection`

**Execution checklist:**

- [ ] Remove category-forced project injection.
- [ ] Derive matrix category booleans from selected tests.
- [ ] Derive standalone job booleans from `jobCategories.when`.
- [ ] Update output tests and CLI E2E runtime-edge tests.
- [ ] Run the Phase 3 validation command.
- [ ] Commit with the subject above.

**Files:**

- Modify: `tools/TestSelector/TestEvaluator.cs:165-239`, `:769-850`, `:853-878`, `:951-960`.
- Modify: `tools/TestSelector/Models/TestSelectionResult.cs:179-234` only if output derivation moves out of `WriteGitHubOutput`.
- Modify: `tools/TestSelector/CategoryMapper.cs:13-222` if it remains only for standalone `jobCategories` matching.
- Test: `EndToEndEvaluationTests.cs:1316-1443`, `TestSelectionResultTests.cs:155-397`.

**Exact change:**

- Remove the parallel category pass for matrix categories from final policy.
- Remove `CollectCategoryForcedTestProjects` and `AddCategoryForcedTestProjects`; runtime edges replace them.
- Derive matrix-backed booleans from selected tests:
  - `run_integrations = RunAllTests || AffectedTestProjects.Count > 0` (same contract already in `TestSelectionResult.WriteGitHubOutput:188-209`).
  - `run_cli_e2e = RunAllTests || AffectedTestProjects` contains `tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj`.
- Derive standalone job booleans from `jobCategories`:
  - `run_extension = RunAllTests || matchesAny(activeFiles, jobCategories.extension.when minus exclude)`.
  - `run_polyglot = RunAllTests || matchesAny(activeFiles, jobCategories.polyglot.when minus exclude)`.
- Keep GitHub output names stable: `run_integrations`, `run_cli_e2e`, `run_extension`, `run_polyglot`, `affected_test_projects`.
- Keep non-PR workflow behavior unchanged at `.github/workflows/tests.yml:58-67`, `:87-95`.

**Tests to add/update:**

- Replace `CollectCategoryForcedTestProjects_TriggeredCategory_ReturnsTestProjects` with `SelectTests_RuntimeCliEdgeSetsRunCliE2eAndAffectedProject`.
  - Assert `AffectedTestProjects` contains CLI E2E and captured output contains `run_cli_e2e=true`.
- Replace `Evaluate_CategoryTestProjects_MappingsOnly_ForcesProjectAdditively` with `Evaluate_RuntimeEdge_MappingsOnly_AddsCliE2eAdditively`.
  - Changed `src/Aspire.Cli/Program.cs`; declared build edge to CLI unit tests and runtime edge to CLI E2E.
  - Assert both test projects are selected.
- Add `SelectTests_StandaloneJobCategoryWhenSetsRunExtensionWithoutAffectedProjects`.
  - Changed `extension/package.json`; no selected .NET tests.
  - Assert `Categories["extension"] == true`, `AffectedTestProjects` empty, `run_integrations=false` in GitHub output.
- Add `SelectTests_IntegrationsDerivedFromSelectedTestProject`.
  - Selected `tests/Aspire.Redis.Tests/Aspire.Redis.Tests.csproj` with no old integrations category trigger.
  - Assert GitHub output contains `run_integrations=true`.
- Add `SelectTests_CliE2eBooleanCannotBeTrueWithoutSelectedCliE2eProject`.
  - Changed file matches old CLI trigger path but no runtime edge selected.
  - Assert `run_cli_e2e=false`; this catches reintroducing path-only boolean drift.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.EndToEndEvaluationTests" \
  --filter-class "*.TestSelectionResultTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 4 — Migrate the audit rules to the new shape

**Commit subject:** `chore(test-selector): migrate audit rules to relationship graph`

**Execution checklist:**

- [ ] Convert audit mappings to `mappings`.
- [ ] Add CLI E2E runtime edges and `inferDeps` canaries.
- [ ] Add `jobCategories` for extension and polyglot.
- [ ] Update `AuditFixtureTests` to exercise the graph/policy path.
- [ ] Run the Phase 4 validation command.
- [ ] Commit with the subject above.

**Files:**

- Modify: `eng/scripts/test-selection-rules.audit.json:1-206`.
- Keep unchanged unless needed for parser compatibility: `eng/scripts/test-selection-rules.json:1-40`.
- Test: `tests/Infrastructure.Tests/TestSelector/Integration/AuditFixtureTests.cs:46-149`.
- Test data: `tests/Infrastructure.Tests/TestSelector/TestData/audit-fixtures.json:1-` as needed only when expected behavior intentionally changes.

**Exact change:**

- Convert legacy `sourceToTestMappings` to `mappings` with `from` and `to`.
- Add explicit `runtime` edges for CLI E2E from current `cli_e2e.triggerPaths`:
  - `src/Aspire.Cli/**` -> `tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj`.
  - `src/Aspire.Hosting/**` -> same.
  - `eng/clipack/**` -> same.
  - `tests/Aspire.Cli.EndToEnd.Tests/**` -> same.
  - `tests/Shared/Hex1b*` -> same.
  - Apply `exclude: ["**/api/*.txt"]` where the old category used it.
- Add `inferDeps` false for:
  - `tests/Aspire.Acquisition.Tests/Aspire.Acquisition.Tests.csproj`.
  - `tests/Infrastructure.Tests/Infrastructure.Tests.csproj`.
  - `tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj` if the team agrees CLI E2E should run only from declared runtime/build edges.
- Add `jobCategories`:
  - `extension.when` from old extension trigger paths `extension/**`, `src/Aspire.Hosting/**`; preserve `exclude: ["**/api/*.txt"]`.
  - `polyglot.when` from old polyglot trigger paths `.github/workflows/polyglot-validation/**`, `.github/workflows/polyglot-validation.yml`, `tests/PolyglotAppHosts/**`, hosting polyglot source paths, and `src/Aspire.Hosting.CodeGeneration*/**`; preserve `exclude: ["**/api/*.txt"]`.
- Remove category `testProjects` from audit config once runtime edges cover the same cases.
- Keep active config minimal and live behavior-preserving. Do not promote broad audit rules to active in this phase.

**Audit fixture expectations:**

- Re-run `AuditFixtureTests` before changing fixture data.
- If a fixture changes outcome, do not blindly update it. Inspect the changed files and classify:
  - If the old fixture expected CLI E2E because a runtime edge now exists, the selected project list should include CLI E2E.
  - If an integrations fixture falls to `fallback_unmatched`, add a declared edge or mapping only when there is a real relationship; otherwise keep conservative RunAll.
  - If a skip fixture starts selecting tests, check `ignore`/`exclude` migration first.

**Tests to add/update:**

- Update `AuditFixtureTests.EvaluateAgainstRules` to use the new graph/policy components instead of `CategoryMapper + ProjectMappingResolver` directly.
- Add `AuditRules_CliE2eRuntimeEdge_SelectsCliE2eProject`.
  - For each of `src/Aspire.Cli/Program.cs`, `src/Aspire.Hosting/DistributedApplication.cs`, `eng/clipack/build.proj`, assert mapped/declared selected projects contains CLI E2E.
- Add `AuditRules_InferDepsFalse_ContainsRestrictedCanaries`.
  - Assert normalized `InferDeps` has Acquisition and Infrastructure false.
- Keep `AuditRules_SharedTestHelperChange_RoutedToIntegrations` but update assertion to the new policy result shape.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.AuditFixtureTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 5 — Single-source PowerShell RunAll suppression data

**Commit subject:** `fix(ci): read matrix suppression from test selection rules`

**Execution checklist:**

- [ ] Add `-SelectionRulesPath` parsing to the PowerShell filter.
- [ ] Read suppressed projects from `inferDeps:false` with legacy fallback.
- [ ] Pass active and audit rules paths from `tests.yml`.
- [ ] Add PowerShell tests for RunAll, selective, fallback, and invalid path cases.
- [ ] Run the Phase 5 validation command.
- [ ] Commit with the subject above.

**Files:**

- Modify: `eng/scripts/filter-test-matrix-by-scope.ps1:31-37`, `:66-67`, `:174-199`, `:280-319`.
- Modify: `.github/workflows/tests.yml:347-397`.
- Test: `tests/Infrastructure.Tests/PowerShellScripts/FilterTestMatrixByScopeTests.cs:27-297`, `:299-382`.

**Exact change:**

- Add a parameter to `filter-test-matrix-by-scope.ps1`:
  - `-SelectionRulesPath <path>`.
- When present, parse the rules JSON and build the suppression set from normalized config data:
  - New shape: all `inferDeps` keys whose value is `false`.
  - Legacy fallback: `restrictedTestProjects`.
- Keep `-RestrictedTestProjects` as a temporary direct override for local tests/backcompat, but make workflow pass `-SelectionRulesPath` only.
- Update both workflow calls:
  - Active matrix filter at `.github/workflows/tests.yml:347-369` passes `-SelectionRulesPath "${{ github.workspace }}/eng/scripts/test-selection-rules.json"`.
  - Audit matrix filter at `.github/workflows/tests.yml:370-397` passes `-SelectionRulesPath "${{ github.workspace }}/eng/scripts/test-selection-rules.audit.json"`.
- Note current workflow does **not** pass `-RestrictedTestProjects` anywhere; this phase is where RunAll suppression becomes actually wired to CI data.

**Tests to add/update:**

- `RunAll_FiltersInferDepsFalseProjectsFromSelectionRulesPath`.
  - Matrix includes `tests/Infrastructure.Tests/Infrastructure.Tests.csproj` and `tests/Normal.Tests/Normal.Tests.csproj`.
  - Rules file contains `"inferDeps": { "tests/Infrastructure.Tests/Infrastructure.Tests.csproj": false }`.
  - Run with `-RunAll` and `-SelectionRulesPath`.
  - Assert Infrastructure is removed and Normal remains.
- `RunAll_SelectionRulesPathFallsBackToRestrictedTestProjects`.
  - Rules file contains old `restrictedTestProjects` only.
  - Assert the restricted project is removed under RunAll.
- `SelectiveMode_DirectlyAffectedInferDepsFalseProjectStillRuns`.
  - Same rules file, `RunAll=false`, affected projects contains the suppressed project.
  - Assert the project remains; this preserves explicit declared-edge opt-in behavior.
- `InvalidSelectionRulesPath_FailsWithHelpfulError`.
  - Assert nonzero exit and output contains `SelectionRulesPath`.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.FilterTestMatrixByScopeTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Phase 6 — Complete tests, docs, and spec status

**Commit subject:** `docs(test-selector): document relationship graph selection`

**Execution checklist:**

- [ ] Update rules README and conditional-tests doc for the relationship graph.
- [ ] Remove force/restrict wording from docs.
- [ ] Mark the redesign spec implemented or implemented-behind-audit.
- [ ] Run the Phase 6 validation commands.
- [ ] Commit with the subject above.

**Files:**

- Modify: `eng/scripts/test-selection-rules.README.md:1-232`.
- Modify: `docs/conditional-tests-run.md:1-351`.
- Modify: `docs/specs/test-selection-redesign.md:1-14`, `:341-360`.
- Test: all TestSelector and PowerShell matrix tests.

**Exact change:**

- Update README terminology:
  - `ignore` / `runEverything` if the new names are accepted.
  - `mappings` and `edges` as relationship data.
  - `inferDeps` as a per-test fact, not a suppression verb.
  - `jobCategories` for standalone jobs.
- Remove docs that describe category `testProjects` as the way to force tests.
- Explain runtime edges with the CLI E2E example.
- Explain derived outputs:
  - `affected_test_projects` is selected tests.
  - `run_integrations` derives from selected matrix tests.
  - `run_cli_e2e` derives from selected CLI E2E project.
  - `run_extension` / `run_polyglot` derive from `jobCategories.when`.
- Mark `docs/specs/test-selection-redesign.md` status as implemented or implemented-behind-audit, depending on active promotion state.

**Validation:**

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-namespace "Infrastructure.Tests.TestSelector" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"

dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.FilterTestMatrixByScopeTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

## Final integration validation before PR

Run these after Phase 6:

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-namespace "Infrastructure.Tests.TestSelector" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"

dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.FilterTestMatrixByScopeTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"

dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.TestSelectionResultTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

Optional smoke commands with explicit files:

```bash
./dotnet.sh run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx \
  --config eng/scripts/test-selection-rules.audit.json \
  --changed-files "src/Aspire.Cli/Program.cs" \
  --verbose

./dotnet.sh run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx \
  --config eng/scripts/test-selection-rules.audit.json \
  --changed-files "extension/package.json" \
  --verbose
```

Expected smoke observations:

- CLI change: `affectedTestProjects` includes `tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj`; `run_cli_e2e=true` in GitHub output mode.
- Extension change: `affectedTestProjects=[]`; `run_extension=true`; `run_integrations=false`.

## Named falsifiable test plan

| Branch | Test name | Assertion that catches regression |
|---|---|---|
| Declared edge selection | `SelectTests_DeclaredEdgeSelectsTestProject` | `AffectedTestProjects` contains the edge target and `RunAllTests` is false. |
| Runtime edge selection for CLI E2E | `SelectTests_RuntimeEdgeSelectsCliE2eWithoutProjectReference` | CLI E2E project is selected even with no affected projects. |
| Inferred edge selection | `SelectTests_InferredProjectAddedWhenInferDepsDefaultsTrue` | A dotnet-affected test project is included when `inferDeps` has no entry. |
| `inferDeps:false` suppression | `SelectTests_InferDepsFalseSuppressesInferredOnlyProject` | Suppressed project is absent when it came only from inference. |
| Declared edge beats suppression | `SelectTests_DeclaredEdgeOverridesInferDepsFalse` | Suppressed project is present when selected by declared edge. |
| No git ref fallback | `Evaluate_NoGitRefWhenInferenceNeededFallsBackToRunAll` | `RunAllTests` true and reason contains `No git ref`. |
| dotnet-affected failure fallback | `SelectTests_DotnetAffectedFailureFallsBackToRunAll` | `RunAllTests` true and reason starts with `dotnet-affected failed`. |
| Unmatched file fallback | `SelectTests_UnmatchedFileFallsBackToRunAll` | `RunAllTests` true and reason contains `Unmatched files`. |
| Matched-but-zero fallback | existing `CheckMatchedButZeroProjects_IntegrationsMatchedZeroProjects_RunsAll` updated for new category derivation | RunAll true when integrations-relevant change selects zero projects and dotnet sees zero projects. |
| Derived `run_integrations` | `SelectTests_IntegrationsDerivedFromSelectedTestProject` | GitHub output has `run_integrations=true` whenever selected tests are non-empty. |
| Derived `run_cli_e2e` | `SelectTests_RuntimeCliEdgeSetsRunCliE2eAndAffectedProject` | GitHub output has `run_cli_e2e=true` only when CLI E2E project is selected. |
| Standalone job category | `SelectTests_StandaloneJobCategoryWhenSetsRunExtensionWithoutAffectedProjects` | `run_extension=true`, `affected_test_projects=[]`, `run_integrations=false`. |
| PowerShell single-source | `RunAll_FiltersInferDepsFalseProjectsFromSelectionRulesPath` | RunAll matrix removes the `inferDeps:false` project read from rules JSON. |
| Audit fixtures | `AuditFixture_ResolvesToExpectedOutcomeAndCategories` | Every curated PR keeps expected outcome, categories, and mapped/selected test projects. |

## Risks and blast radius

- **Under-selection is the dangerous failure mode.** A wrong edge, bad `inferDeps:false`, or mismatched `run_<category>` boolean can silently skip tests and let regressions merge.
- **`run_cli_e2e` drift is high risk.** If the boolean is path-derived but the matrix lacks CLI E2E, the CLI E2E image/job path can be skipped or built without tests.
- **RunAll suppression drift can hide coverage.** If PowerShell reads a different suppression list than C#, RunAll and selective scopes disagree.
- **Audit config has broad blast radius once promoted.** Today it is observational; after promotion it controls CI execution.
- **Legacy dual-read can mask malformed new config.** Tests must assert new fields, not only legacy projection.

## Rollback plan

- Before promotion, rollback is trivial: revert the audit-config/engine branch or ignore audit selector outputs; active config remains minimal.
- After promotion, fastest safe rollback is a PR that forces all tests while investigation happens:
  - Set active `runEverything` / `triggerAllPaths` to a catch-all pattern if supported by validation, or
  - change `.github/workflows/tests.yml` Apply conditional scope step to emit `run_all=true` and `affected_test_projects=[]`.
- Then revert the promotion commit separately.
- Keep audit diagnostics artifacts (`selector-active.json`, `selector-audit.json`, `matrix-active.json`, `matrix-audit.json`) for root cause.

## Promotion gate from audit to active

Do not promote audit rules to `eng/scripts/test-selection-rules.json` until all are true:

- Phase 6 validation commands pass locally.
- At least one PR run has successful `test-scope-diagnostics` artifacts with audit selector and matrix audit present.
- Audit miss report shows no missed failed tests.
- Spot-check changed-file classes: docs-only skip, template-only selection, CLI source -> CLI E2E, extension-only -> extension job, component source -> integration tests, RunAll critical file.
- The user explicitly approves replacing the active minimal config with the audit relationship graph.

## Open questions for the user before implementation

1. **Confirm negation scope:** implement only `inferDeps:false` now, with no per-edge `!` negation?
2. **Confirm migration compatibility:** use short-lived dual-read of old/new fields, or require a hard cutover of both configs in one branch?
3. **Confirm PowerShell data source:** should `filter-test-matrix-by-scope.ps1` read `inferDeps` directly from `-SelectionRulesPath`, rather than C# emitting a `suppressed_test_projects` output?
4. **Confirm CLI E2E inference:** should `tests/Aspire.Cli.EndToEnd.Tests` have `inferDeps:false` so it only runs from declared runtime edges?
5. **Confirm naming:** use spec names `ignore`, `runEverything`, `mappings`, or keep old names `ignorePaths`, `triggerAllPaths`, `sourceToTestMappings` plus only add `edges`/`inferDeps`/`jobCategories`?
