# Conditional Test Runs

Conditional test runs is a CI optimization: instead of running the full suite for
every pull request, CI runs only the test projects a change can actually affect.
This document explains how the feature works end to end. The field-by-field
config reference lives next to the rules file in
[`eng/scripts/test-selection-rules.README.md`](../eng/scripts/test-selection-rules.README.md).

## Motivation

Running the full test suite for every pull request is expensive:

- **CI efficiency** — integration tests run on three platforms with ~20 projects
  each. The full sweep is slow and burns runner capacity.
- **Faster feedback** — developers get results sooner when only relevant tests
  run.
- **Resource optimization** — fewer GitHub Actions minutes per PR.

The risk is the opposite failure: if selection is too aggressive it can silently
*skip* a test that should have run, and a real regression sails through green.
So the system is deliberately biased toward over-running. Every ambiguous case
falls back to running everything.

## Design: data vs. engine

The system is split into two pieces with a hard line between them:

- **Data** — `eng/scripts/test-selection-rules.json` is a relationship graph. It
  records *which source paths couple to which test projects*, which paths matter
  to everything or nothing, and which standalone jobs a path fans out to. It
  contains no policy.
- **Engine** — `tools/TestSelector` is a single fixed selection algorithm. All
  decisions (ordering, fallbacks, how a category boolean is computed) live here.

This split is the whole point of the design: adding a new coupling is a data
edit, not a code change, and the policy that keeps selection safe is reviewed and
tested in one place.

### Active and audit configs

Two rule files share the schema:

- **`test-selection-rules.json`** (active) drives the live scope. Kept minimal.
- **`test-selection-rules.audit.json`** (audit) is the rich rule set. It runs on
  every PR in observational mode (`continue-on-error`, separate artifacts) so
  rules can be validated against real PRs before being promoted into the active
  file. The audit config never changes what tests run.

## How it works

For a pull request, the engine:

1. **Drops ignored files** (`ignore`). Files that match a `jobCategory.when` or an
   `edge.from` are rescued back in even if an `ignore` glob also matched them.
2. **Checks critical paths** (`runEverything`). Any match runs the full suite.
3. **Matches `jobCategories`, `mappings`, and `edges`** against the changed files.
4. **Checks for unmatched files** — any active file claimed by none of those
   forces a conservative full run.
5. **Runs `dotnet-affected`** for the MSBuild transitive-dependency graph, filters
   to test projects (`testProjectPatterns`), and unions in the mapping-resolved
   projects.
6. **Applies `inferDeps`** — projects whose inferred edges are declared false
   positives are dropped unless a mapping/edge explicitly selected them.
7. **Guards the matched-but-zero gap** — if integration-relevant files matched but
   the change resolved to zero projects (e.g. a non-MSBuild-input file), runs the
   full suite.
8. **Unions edge-resolved projects** and **projects each `run_<category>` boolean**
   from the final selected set.

The two key ideas:

- **Conventions over enumeration.** `mappings` use naming conventions
  (`src/Components/{name}/**` → `tests/{name}.Tests/`) so one entry covers a
  family, while `dotnet-affected` supplies MSBuild's transitive graph for
  everything reachable by `ProjectReference`.
- **Runtime edges for invisible couplings.** A test that consumes a *built
  artifact* rather than a project reference is invisible to `dotnet-affected`. A
  `runtime` edge declares that dependency so the test is still selected. CLI
  end-to-end tests, which run against a built CLI archive, are the motivating
  case.

## Category booleans are projected, never re-matched

A `run_<category>` boolean (e.g. `run_cli_e2e`) is **derived from whether the
category's test project ended up in the selected set** — not from re-matching the
changed paths against a second set of globs. Because the boolean and the matrix
read the same selected set, they cannot disagree: if `run_cli_e2e` is `true`, the
cli-e2e project is in the matrix, and vice versa. This removes a class of bug
where a standalone boolean and the matrix were computed by parallel path-matching
and could drift apart.

`run_integrations` is the one count-derived boolean: it is `true` when `run_all`
is set or at least one test project was selected. The `integrations` entry in
`jobCategories` is therefore never a standalone job flag — instead its `when` set
is the **accounting net** that decides whether a changed source file is "known"
(matched by some rule) or "unmatched" (forces a full run).

## Architecture

### Test selector tool (`tools/TestSelector`)

A NativeAOT C# CLI that reads the config, gets changed files from git (or
`--changed-files`), runs the selection algorithm, and emits both a JSON result
and GitHub Actions outputs. Key components:

- `Analyzers/IgnorePathFilter`, `CriticalFileDetector`, `ProjectMappingResolver`
  (mappings **and** edges), `DotNetAffectedRunner`, `TestProjectFilter`,
  `InferDepsFilter`, `NuGetDependentTestDetector`.
- `CategoryMapper` — matches files to `jobCategories`.
- `TestEvaluator` — the engine that orchestrates the fixed flow above.
- `Models/TestSelectorConfig`, `Models/TestSelectionResult`.

### Configuration files (`eng/scripts/`)

`test-selection-rules.json` (active) and `test-selection-rules.audit.json`
(audit), validated by `test-selection-rules.schema.json`. See the
[config README](../eng/scripts/test-selection-rules.README.md) for every field.

### GitHub Actions integration

`.github/workflows/tests.yml` runs the active and audit selectors in parallel,
reconciles them in the `setup_for_tests` job, and uses the outputs to gate jobs.
The matrix filter `eng/scripts/filter-test-matrix-by-scope.ps1` narrows the test
matrix to `affected_test_projects` and subtracts `suppressed_test_projects`.

## Tool usage

### CLI reference

```bash
# Compare against origin/main
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx --config eng/scripts/test-selection-rules.json --from origin/main

# Verbose, to see why each file did or didn't trigger
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx --config eng/scripts/test-selection-rules.audit.json \
  --from origin/main --verbose

# Explicit changed files (bypasses git)
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx --config eng/scripts/test-selection-rules.json \
  --changed-files "src/Aspire.Dashboard/App.razor"

# GitHub Actions output format
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx --config eng/scripts/test-selection-rules.json \
  --from origin/main --github-output
```

### Options

| Option | Short | Description |
|--------|-------|-------------|
| `--solution` | `-s` | Path to the solution file (required) |
| `--config` | `-c` | Path to the rules file (optional; without it, only `dotnet-affected` is used) |
| `--from` | `-f` | Git ref to compare from (required unless `--changed-files` is given) |
| `--to` | `-t` | Git ref to compare to (default: `HEAD`) |
| `--changed-files` | | Comma-separated file list (bypasses git) |
| `--non-applying-paths` | | Comma-separated paths that yield a non-applying result when they are the only changes |
| `--output` | `-o` | Write the JSON result to a file |
| `--github-output` | | Emit GitHub Actions outputs |
| `--verbose` | `-v` | Verbose diagnostics |

### JSON result

```json
{
  "runAllTests": false,
  "reason": "selective",
  "categories": {
    "cli_e2e": false,
    "extension": true,
    "polyglot": false
  },
  "affectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "integrationsProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "suppressedTestProjects": [],
  "changedFiles": ["src/Aspire.Dashboard/App.razor", "extension/Extension.proj"],
  "dotnetAffectedProjects": [],
  "ignoredFiles": []
}
```

`categories` never contains `integrations` — its boolean is count-derived (see
above).

### GitHub Actions outputs

| Output | Description |
|--------|-------------|
| `run_all` | `true` on critical-path or any conservative fallback |
| `selection_reason` | Why the decision was made (`selective`, `critical_path`, `all_ignored`, unmatched-files text, etc.) |
| `run_integrations` | Count-derived: `run_all` OR at least one test project selected |
| `run_cli_e2e`, `run_extension`, `run_polyglot` | Projected category booleans |
| `affected_test_projects` | JSON array of selected test `.csproj` paths |
| `suppressed_test_projects` | JSON array of `inferDeps:false` paths to subtract from sweeps |
| `run_nuget_tests`, `nuget_test_projects` | NuGet-dependent test gating |

## Testing

```bash
# All test selector tests
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- --filter-namespace "Infrastructure.Tests.TestSelector" \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Different MTP filter *types* are ANDed, so run each in its own invocation rather
than combining `--filter-namespace` with `--filter-class`. Coverage spans the
analyzers, the config/result models (including GitHub-output generation), the
`CategoryMapper`, path normalization, and audit-fixture tests that replay
captured real PRs through the audit config.

## Troubleshooting

### All tests running unexpectedly

A changed file matched `runEverything`, or a file is unmatched by every rule
(conservative fallback). Run with `--verbose` to see the trigger. For an
unmatched file, either add it to `ignore`, cover it with a `mapping`/`edge`, or
extend the `integrations.when` accounting net.

### Tests not running when expected

The coupling is invisible to `dotnet-affected` (no `ProjectReference`), the file
is ignored, or no mapping/edge resolves to the project. Check `ignore`, confirm a
`mapping`/`edge` resolves to the project, and for built-artifact couplings add a
`runtime` edge. If the project carries `inferDeps:false`, it runs only when a
declared mapping/edge selects it.

### Non-.NET files not triggering tests

Files such as extension or polyglot sources can't be analyzed by
`dotnet-affected`. Add a `jobCategories` entry with matching `when` patterns and
gate the job on the projected `run_<category>` output.

## Dependencies

- .NET 10.0 SDK
- `dotnet-affected` global tool: `dotnet tool install --global dotnet-affected`
- Git
