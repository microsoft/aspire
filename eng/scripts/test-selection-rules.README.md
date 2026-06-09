# Test Selection Rules Configuration

`test-selection-rules.json` decides which tests CI runs for a pull request, based
on the changed files. Running fewer tests when a change is small keeps CI fast;
the rules here are what make that safe.

The file holds **only relationships** — which source paths couple to which test
projects, which paths matter to everything (or nothing), and which standalone
jobs a path fans out to. All selection *policy* (fallbacks, ordering, how a
category boolean is computed) lives in the `tools/TestSelector` engine, not in
this file. When in doubt about what a field does, the engine is the source of
truth; this file only supplies data.

## Two configs: active and audit

There are two rule files:

- **`test-selection-rules.json`** (active) — drives the live CI scope. Kept
  deliberately minimal so its blast radius is small.
- **`test-selection-rules.audit.json`** (audit) — the rich rule set. It runs in
  parallel on every PR in **observational mode** (`continue-on-error`, separate
  artifacts) so we can validate it against real PRs before promoting rules into
  the active file. Changing the audit file does not change what tests run.

Both files share the same schema (`test-selection-rules.schema.json`). The
examples below are drawn from the audit config, which exercises every field.

## Configuration reference

### `ignore`

Glob patterns for files that are completely ignored: they trigger no tests **and**
do not count toward the conservative fallback. Use this only for files that can
never affect any test outcome (docs, editor config, other CI workflows).

```json
"ignore": [
  ".editorconfig",
  "**/*.md",
  "docs/**",
  ".github/workflows/**",
  "**/api/*.txt"
]
```

When **every** changed file is ignored, the selector produces no active files and
the managed test matrix is empty. Whole-workflow skipping is handled earlier by
the lightweight `prepare_for_ci` gate (`eng/testing/github-ci-trigger-patterns.txt`);
the selector outputs are consumed later in `tests.yml`.

### `runEverything`

Critical files where any change runs the full suite. A match short-circuits
everything else and reports `reason = critical_path`.

```json
"runEverything": [
  "global.json",
  "Directory.Build.props",
  "Directory.Build.targets",
  "Directory.Packages.props",
  "NuGet.config"
]
```

### `testProjectPatterns`

Identifies which affected projects are test projects (used to filter
`dotnet-affected` output). `include` is required; `exclude` is optional.

```json
"testProjectPatterns": {
  "include": ["tests/**/*.Tests.csproj"],
  "exclude": ["tests/testproject/**", "tests/**/TestFixtures/**"]
}
```

### `mappings`

The compact form for the conventional source→test case. Each entry is an edge
generator: `from` glob(s) couple to a `to` test project, and the optional
`{name}` placeholder lets one entry cover a whole family.

```json
"mappings": [
  {
    "from": "src/Components/{name}/**",
    "to": "tests/{name}.Tests/{name}.Tests.csproj"
  },
  {
    "from": "playground/**",
    "to": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"
  }
]
```

`{name}` captures part of the matched path and substitutes it into `to`.

When several source patterns map to the **same** test project, pass an array to
`from` so they share one entry instead of duplicating `to`:

```json
{
  "from": [
    "eng/Publishing.props",
    "eng/Signing.props",
    "eng/scripts/pack-cli-npm-package.ps1"
  ],
  "to": "tests/Infrastructure.Tests/Infrastructure.Tests.csproj"
}
```

The string and array forms are interchangeable (a string is a one-element array).
`exclude`, when present, applies uniformly to every `from` pattern in the entry.
Mappings always generate **build**-typed edges (see below).

### `edges`

Explicit source→test couplings the mapping convention can't express, tagged by
`type`:

- **`build`** (default) — a coupling `dotnet-affected` can also see through a
  `ProjectReference`. Listing it makes intent explicit but adds nothing the build
  graph wouldn't already find.
- **`runtime`** — a coupling with **no** `ProjectReference`, invisible to the
  build graph. This is the field that fixes the silent-skip class of bug: a test
  that consumes a *built artifact* (not a project reference) would never be
  selected by `dotnet-affected`. A runtime edge declares the dependency so the
  test is selected anyway.

```json
"edges": [
  {
    "from": ["src/Aspire.Cli/**", "src/Aspire.Hosting/**", "eng/clipack/**"],
    "to": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
    "type": "runtime",
    "category": "cli_e2e",
    "exclude": ["**/*.md", "**/api/*.txt"]
  }
]
```

`category` is optional. When set, it projects a `run_<category>` boolean
(`run_cli_e2e` here) whose value is derived from whether `to` ends up in the
selected test set — so the boolean **can never disagree** with the matrix. The
engine does not re-match paths to compute the boolean; it reads the selected set.
Category edges must use a literal `to` (no `{name}`).

### `inferDeps`

A per-test fact: does this test project trust the edges `dotnet-affected` infers
from the build graph? Default is `true`. Set a `.csproj` path to `false` to
declare those inferred edges are false positives — the project then runs **only**
when a declared `mappings`/`edges` entry resolves to it.

```json
"inferDeps": {
  "tests/Aspire.Acquisition.Tests/Aspire.Acquisition.Tests.csproj": false,
  "tests/Infrastructure.Tests/Infrastructure.Tests.csproj": false,
  "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj": false
}
```

This replaces the old `restrictedTestProjects` list. A declared mapping or edge
to an `inferDeps:false` project still selects it — declaring the coupling is the
explicit opt-in that overrides the opt-out.

The selector also emits these as `suppressed_test_projects`. The matrix filter
subtracts them from broad pass-through / run-all sweeps (a project that opted out
of inferred edges should not be dragged in by a blanket sweep either). In
selective runs they are already excluded unless a declared edge resolved them in.

### `packageOrArchiveProducingProjects`

Globs for projects that produce NuGet packages or archives but may not carry
`IsPackable=true` in a csproj (so the package-aware logic can't detect them from
project metadata alone).

```json
"packageOrArchiveProducingProjects": ["eng/clipack/**"]
```

### `jobCategories`

Boolean `run_<name>` gates for standalone jobs that do **not** flow through the
`affected_test_projects` matrix (for example the VS Code extension and polyglot
validation jobs). `when` declares the paths that fan out to the job; `exclude`
carves out paths that should not.

```json
"jobCategories": {
  "extension": {
    "when": ["extension/**", "src/Aspire.Hosting/**"],
    "exclude": ["**/api/*.txt"]
  },
  "polyglot": {
    "when": [".github/workflows/polyglot-validation/**", "src/Aspire.Hosting.Python/**"],
    "exclude": ["**/api/*.txt"]
  }
}
```

**`integrations` is a reserved name.** Its `run_integrations` boolean is *not*
taken from `when`; it is derived from the selected test count (`run_all` OR at
least one affected test project). Its `when` set instead doubles as the
**known-source-areas accounting net**: any active file matched by no mapping, no
edge, and no `jobCategory.when` is "unmatched" and forces a full run. Keep
`integrations.when` broad enough to cover every source area that has tests, or
unrelated changes will conservatively run everything.

## How the engine uses this data

The `tools/TestSelector` engine walks the data in a fixed order (simplified):

1. Drop `ignore` files. Ignored files that match a `jobCategory.when` or an
   `edge.from` (minus their `exclude`) are *rescued* back in.
2. If every file was ignored → no tests. If any file matches `runEverything` →
   run all (`critical_path`).
3. Match files to `jobCategories`, resolve `mappings`, resolve `edges`.
4. Any active file matched by none of those → run all (unmatched fallback).
5. Run `dotnet-affected`; filter to test projects via `testProjectPatterns`;
   union the mapping-resolved projects; apply the `inferDeps` filter.
6. If the `integrations` net matched but the change resolved to zero projects
   (likely a non-MSBuild-input file) → run all (matched-but-zero guard).
7. Union the `edges`-resolved projects (edges bypass `inferDeps` — they are
   declared opt-ins). Derive each `run_<category>` boolean by projecting the
   category's `to` set onto the final selected set.

Every ambiguous case biases toward running more, never less: under-selection
would silently skip tests.

## Workflow integration

`.github/workflows/tests.yml` runs the selector in `setup_for_tests` and exposes
these outputs (consumed by downstream jobs):

- `run_all` — `true` on critical-path or any conservative fallback.
- `run_integrations`, `run_cli_e2e`, `run_extension`, `run_polyglot` — category
  booleans. `run_integrations` is count-derived; the rest are projected from
  `jobCategories`/category `edges`.
- `affected_test_projects` — JSON array of selected `.csproj` paths for the
  matrix filter.
- `suppressed_test_projects` — JSON array of `inferDeps:false` paths, subtracted
  from pass-through / run-all matrix sweeps.

Standalone jobs (`polyglot_validation`, `extension_tests_win`) use the category
booleans as `if` guards. Matrix test jobs use `affected_test_projects` (and
subtract `suppressed_test_projects`) for per-project filtering.

## Adding a new coupling

Pick the field that matches the *kind* of relationship:

- **A component/test that follows the naming convention** → add a `mappings`
  entry (use `{name}` if it generalizes).
- **A test coupled to sources with no `ProjectReference`** (consumes a built
  artifact) → add a `runtime` `edges` entry, with a `category` if it gates a
  named boolean.
- **A test that produces false-positive inferred edges** (runs far too often) →
  add an `inferDeps` entry set to `false`, and make sure a mapping/edge declares
  the couplings it *should* run for.
- **A standalone job not in the test matrix** → add a `jobCategories` entry and
  wire a `run_<name>` output + `if:` guard in `tests.yml`.

When adding a category boolean, also add the matching `run_<name>` to the non-PR
event branch in `tests.yml` (non-PR events run everything) and to the results
summary step.

## Testing changes

Run the selector locally to see what a config would select:

```bash
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx \
  --config eng/scripts/test-selection-rules.audit.json \
  --from origin/main \
  --verbose
```

Unit and end-to-end coverage lives in `tests/Infrastructure.Tests` (namespace
`Infrastructure.Tests.TestSelector`). Audit-fixture tests validate the audit
config against captured real PRs.
