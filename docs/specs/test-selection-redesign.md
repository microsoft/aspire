# Test Selection Redesign: Relationship Graph + Policy Engine

> **Status:** Proposed design — not yet implemented. This document is meant to be
> reviewed in a fresh session and then implemented. It supersedes the structural
> approach in [`test-selection-by-changed-paths.md`](./test-selection-by-changed-paths.md)
> (the original spec) and the current behavior documented in
> [`../conditional-tests-run.md`](../conditional-tests-run.md).
>
> Nothing here changes live CI behavior on its own. The override mechanisms this
> redesign reshapes (`restrictedTestProjects`, category `testProjects`) currently
> live **only** in the audit config (`eng/scripts/test-selection-rules.audit.json`),
> dormant in production. The active config
> (`eng/scripts/test-selection-rules.json`) is a minimal templates-only pilot.
>
> **Companion docs (read alongside this for the fresh-session review):**
> [`test-selection-redesign-eval.md`](./test-selection-redesign-eval.md) — the rubric
> for judging this design and its implementation (grounded in real line numbers); and
> [`test-selection-redesign-plan.md`](./test-selection-redesign-plan.md) — the phased
> implementation plan. The eval flags one design gap (no explicit test→category label,
> §D7) that the plan resolves differently than the eval recommends — that fork is open.

## TL;DR

The config is slowly turning into a badly-typed programming language. Each new
use case has added a JSON field + schema entry + an interpreter branch:
`restrictedTestProjects` (suppress), category `testProjects` (force),
`excludePaths` (exclude). Those are **verbs** (policy) living in **data**.

This redesign draws one line:

- **DATA = relationships only.** A typed-edge graph: "a change to *this path*
  should run *this test*." Declarative, conditional-free. Hot-editable, no tool
  rebuild.
- **CODE = decisions only.** One fixed selection engine: map changed files →
  reachable tests → apply a *small, fixed* set of policy switches (conservative
  fallback-to-all, force-run classes). All `if` / precedence / force / suppress
  logic lives here, named and unit-tested.

Two named verbs disappear as config knobs:

- **`force`** (category `testProjects`) becomes an ordinary `runtime`-typed edge.
  A test with no `ProjectReference` (e.g. CLI E2E, which consumes a built CLI
  archive) just declares its real runtime inputs; the normal selection picks it
  up. No "force" concept needed.
- **`restrict`** (`restrictedTestProjects`) becomes either per-edge negation or a
  single per-node fact (`inferDeps: false`) meaning "my build-graph edges are
  false positives — trust only my declared edges."

The choice of implementation language (the current C# tool vs. a JS rewrite) is
**orthogonal** to this split and should be decided separately. Recommendation:
do the split in the existing C# tool to keep the unit suite and NativeAOT speed.

## Why redesign now

The cli_e2e gap that motivated the most recent change is a direct symptom of the
current shape. `tests/Aspire.Cli.EndToEnd.Tests` has **no `ProjectReference`** to
`src/Aspire.Cli` — it consumes a built CLI archive at runtime. `dotnet-affected`
therefore never selects it, so under any selective scope it was silently dropped.
The fix was to add a category `testProjects` "force" knob and inject those
projects into `affected_test_projects`.

That fix is correct, but it is the *third* bespoke override mechanism, and the
precedence between the three is implicit and spread across two languages:

| Current field | What it really means | The leaking verb |
|---|---|---|
| `sourceToTestMappings` | edge: "path → test" | none — clean data |
| `restrictedTestProjects` | "suppress inferred edges for this test" | **suppress** |
| category `testProjects` | "force this test when category fires" | **force** |
| category `excludePaths` | "exclude these paths from the edge" | **exclude** |

When data starts encoding "if A changed and not B, then run C", you have crossed
from data into a half-built interpreter. The redesign moves those decisions to
code where they can be read in one place and unit-tested.

## The principle: relationships as data, decisions as code

This is the long-established **mechanism-vs-policy** separation, and the prior
art below shows every mature monorepo test-selection tool draws the same line.

- *"Separate policy from mechanism… policy and mechanism tend to mutate on
  different timescales, with policy changing much faster than mechanism…
  hardwiring policy and mechanism together makes policy rigid and harder to
  change."* — ESR, *The Art of Unix Programming*, Rule of Separation.
- *"Fold knowledge into data so program logic can be stupid and robust."* — ESR,
  Rule of Representation. **Boundary:** knowledge (relationships) → data;
  *decisions* → code.
- The opposite failure mode is the **inner-platform effect** / Greenspun's tenth
  rule: a config so configurable it becomes a poor reimplementation of a
  programming language. The practical signal: the moment the JSON needs an
  `if` / `unless` / precedence expression, that logic belongs in code.

## Prior art (what the established tools do)

Researched against primary sources (see [References](#references)). Two of the
backbone claims were verified verbatim this session and are quoted below; the
rest should be spot-checked during review.

| Tool | DATA (declared) | CODE (fixed policy) | Relevance |
|---|---|---|---|
| **Nx** | project graph JSON; edges typed `direct`/`implicit`; `implicitDependencies` for non-derivable edges; `!name` negation | one fixed `affected` algorithm: changed files → owning projects → reverse reachability | **Closest to the literal "JSON graph + code applies fixed selection" framing** |
| **Pants** | targets + `dependencies`; inference auto-derives edges, explicit deps **augment by union**; `!`/`!!` negation; `resource`/`file` targets for non-code inputs | inference engine + transitive closure | **Closest to Aspire's reality:** `dotnet-affected` auto-graph **+ JSON supplemental edges** |
| **Bazel** | `srcs` / `deps` / **`data`** attributes; `data` = runtime-only coupling | `rdeps` / target-determinator content-hash diff + `ignore-and-build-all` fallback | runtime couplings (Aspire's cli_e2e) modeled as ordinary tagged edges |
| **dorny/paths-filter** | named glob lists → boolean outputs; no graph, no transitivity | minimal | the pure-declarative extreme; demonstrates the ceiling of "globs only" |
| **Develocity PTS / Azure TIA** | test↔code map **learned/recorded at runtime**, not declared | coverage/ML model + coarse policy dials | the *opposite* philosophy — opaque but zero edge-maintenance |

### Key lessons folded into this design

1. **Model non-derivable edges in the same shape as derived edges, tagged by
   type.** Nx tags edges `implicit` vs `direct`; Bazel distinguishes `data`
   (runtime) from `deps` (build). So a runtime coupling is a normal edge with a
   `runtime` tag — not a separate config dialect. This lets the policy treat it
   differently (select for tests, ignore for build ordering) without a new field
   per case.

2. **Opt-out is edge negation, not a per-case knob.** Nx writes `"!anotherlib"`;
   Pants writes `!dep` (ignore inferred) / `!!dep` (transitive exclude). One
   uniform operator. Aspire's `restrictedTestProjects` and `excludePaths` should
   collapse into negation.

3. **Force / run-all is a *handful* of global policy switches in code, never a
   per-test config knob.** Azure TIA has `DisableTestImpactAnalysis` + periodic
   run-all; Develocity has selection *profiles* + "always run flaky";
   target-determinator has `ignore-and-build-all`. None invent a per-test "force"
   field. This validates deleting category `testProjects`.

4. **Overapproximate, and fall back conservatively.** Bazel (verified verbatim):

   > *"the graph of actual dependencies A must be a subgraph of the graph of
   > declared dependencies D… D is an overapproximation of A. BUILD file writers
   > must explicitly declare all of the actual direct dependencies… and no more.
   > Failure to observe this principle causes undefined behavior."*

   Translated to Aspire's two failure directions — *missed test* (bad) vs.
   *extra test* (cheap) — when in doubt the **data should over-connect** and the
   **fallback should over-run**. Aspire's existing fallbacks (no git ref → all;
   matched-but-zero → all; unmatched file → all) already follow this; keep them.

5. **Keep the data layer hermetic.** Pants (verified verbatim):

   > *"BUILD files are very hermetic in nature with no support for using `import`
   > or other I/O operations."*

   The Aspire config should likewise stay pure data — no embedded conditionals.

## Proposed model

### Layer 1 — DATA: a typed relationship graph

The config declares nodes (test projects, job categories) and edges
(`source-glob → node`, tagged by type). It states relationships and never "if
changed then…".

```jsonc
{
  "$schema": "./test-selection-rules.schema.json",

  // Global short-circuits (unchanged in spirit).
  "ignore":        [ "**/*.md", "docs/**", ".github/workflows/**" ], // matters to nothing
  "runEverything": [ "global.json", "Directory.Build.props" ],        // matters to everything

  // How to recognize test projects in dotnet-affected output (unchanged).
  "testProjectPatterns": {
    "include": [ "tests/**/*.Tests.csproj" ],
    "exclude": [ "tests/testproject/**", "tests/**/TestFixtures/**" ]
  },

  // Concise edge GENERATORS for conventional couplings (keep the {name} placeholder).
  // These expand to many `build`-typed edges; dotnet-affected usually also catches them.
  "mappings": [
    { "from": "src/Components/{name}/**",      "to": "tests/{name}.Tests/...csproj" },
    { "from": "src/Aspire.Hosting.{name}/**",  "to": "tests/Aspire.Hosting.{name}.Tests/...csproj" }
  ],

  // Explicit edges for the NON-conventional cases, tagged by type.
  // `runtime` = a coupling dotnet-affected cannot see (no ProjectReference).
  "edges": [
    { "from": "src/Aspire.Cli/**",     "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" },
    { "from": "src/Aspire.Hosting/**", "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" },
    { "from": "eng/clipack/**",        "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" }
  ],

  // Per-node FACT (not a verb): does this test trust dotnet-affected's inferred edges?
  // Default true. false == "my build-graph edges are false positives"
  // (this replaces restrictedTestProjects).
  "inferDeps": {
    "tests/Aspire.Acquisition.Tests/...csproj": false,
    "tests/Infrastructure.Tests/...csproj":     false,
    "tests/Aspire.Cli.EndToEnd.Tests/...csproj": false
  },

  // Boolean run_<name> gates for STANDALONE jobs that don't flow through the
  // affected_test_projects matrix (extension, polyglot run as dedicated jobs).
  "jobCategories": {
    "extension": { "when": [ "extension/**", "src/Aspire.Hosting/**" ] },
    "polyglot":  { "when": [ "tests/PolyglotAppHosts/**", "src/Aspire.Hosting.*/**" ] }
  }
}
```

Notes:

- **`mappings` vs `edges` is itself the Pants split, in miniature.** `mappings`
  is the concise generator for the conventional component case (one rule covers
  all components — do **not** explode this into per-component edges). `edges` is
  the explicit augmentation for couplings the convention can't express. Both
  produce the same internal edge list.
- **Edge `type`** is `build` (default) or `runtime`. The selection engine treats
  them the same for *test selection*; the tag is there so future policy (e.g.
  build ordering, or "runtime edges always run even under aggressive scope") can
  distinguish them without a schema change.

### Layer 2 — CODE: one fixed selection engine

The entire decision policy is small, linear, and lives in one place. Every rule
is named; adding a behavior is a new line here (reviewed as code), not a new JSON
field interpreted in two languages.

```
selectTests(changedFiles, graph, dotnetAffected):
    files = changedFiles \ matching(graph.ignore)
    if files is empty:                    return RUN_NOTHING
    if matchesAny(files, graph.runEverything): return RUN_ALL          // nuclear

    edges = expand(graph.mappings) ++ graph.edges                       // build the graph
    selected = {}

    // (1) declared edges: a test runs if any of its declared sources changed
    for edge in edges:
        if matchesAny(files, edge.from):  selected += edge.to

    // (2) inferred edges: dotnet-affected — but only for tests that trust inference
    for test in dotnetAffected(files):
        if graph.inferDeps.get(test, true): selected += test

    // (3) safety: a changed file that no edge or dotnet-affected explained
    if exists f in files where not explained(f, edges, dotnetAffected):
        return RUN_ALL                                                  // conservative fallback

    jobs = { name : matchesAny(files, cat.when) for name, cat in graph.jobCategories }
    return Selection(tests = selected, jobs = jobs)
```

`run_<category>` booleans for matrix-backed categories (e.g. `cli_e2e`,
`integrations`) are **derived** from which tests were selected (group test
projects by category label), so the boolean and the matrix can never disagree —
which is exactly the failure that produced the cli_e2e gap. Standalone job
categories (`extension`, `polyglot`) derive their boolean from `when` directly.

### What collapses

| Today | Becomes | Why |
|---|---|---|
| `sourceToTestMappings` (with `{name}`) | `mappings` (unchanged role) | already clean data — keep it |
| category `testProjects` (force) | a `runtime`-typed `edge` | "force" was an epiphenomenon of a missing edge; declare the edge instead |
| `restrictedTestProjects` (suppress) | `inferDeps: false` (or per-edge `!` negation) | "restrict" = "my inferred edges are false positives" |
| category `excludePaths` (exclude) | negation on the edge / category `when` | exclusion is edge negation, the Nx/Pants pattern |
| `run_<category>` parallel computation | derived from selected tests / `when` | one source of truth (the graph) feeds both output channels |

## Worked before/after (real audit config)

**Before** (`test-selection-rules.audit.json`, today):

```jsonc
"categories": {
  "cli_e2e": {
    "triggerPaths": [ "src/Aspire.Cli/**", "src/Aspire.Hosting/**", "eng/clipack/**", ... ],
    "excludePaths": [ "**/api/*.txt" ],
    "testProjects": [ "tests/Aspire.Cli.EndToEnd.Tests/...csproj" ]   // force
  }
},
"restrictedTestProjects": [
  "tests/Aspire.Acquisition.Tests/...csproj",
  "tests/Infrastructure.Tests/...csproj"                              // suppress
]
```

**After:**

```jsonc
"edges": [
  { "from": "src/Aspire.Cli/**",     "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" },
  { "from": "src/Aspire.Hosting/**", "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" },
  { "from": "eng/clipack/**",        "to": "tests/Aspire.Cli.EndToEnd.Tests/...csproj", "type": "runtime" }
],
"inferDeps": {
  "tests/Aspire.Cli.EndToEnd.Tests/...csproj": false,   // was both "force" target and ProjectReference-less
  "tests/Aspire.Acquisition.Tests/...csproj":  false,   // was restricted
  "tests/Infrastructure.Tests/...csproj":      false    // was restricted
}
```

The `**/api/*.txt` exclusion becomes a negated source on the relevant edges (or a
global `ignore` entry, since generated API files are already ignored repo-wide).

## What this does NOT change (scope guardrails)

- **The `{name}` placeholder mappings stay.** They are genuinely declarative data
  and the compact form for the conventional component case.
- **Conservative fallbacks stay** (no git ref → all; dotnet-affected failure →
  all; unmatched file → all; integrations matched-but-zero → all). They are the
  correct overapproximation bias.
- **`testProjectPatterns` stays** (how test projects are recognized).
- **The two CI consumers stay** — `affected_test_projects` (matrix) and
  `run_<category>` (standalone job gates). See the dual-enforcement note below.

## Open design decisions (for review)

These are genuine forks the implementing session should resolve, not settled:

1. **Negation granularity.** Per-node `inferDeps: false` (terse; "ignore all my
   inferred edges") vs. per-edge `!` negation (finer; "ignore *this* inferred
   edge"). Nx/Pants use per-edge; Aspire's two restricted projects are genuinely
   "all inferred edges are noise", so the node-level boolean is a defensible
   shorthand. Could support both.

2. **Edge association direction.** Source-keyed edges (`from` → `to`, shown above;
   good for "what does this path affect") vs. test-centric (each test declares its
   `sources`; good for ownership — the test owner lists their inputs). Trade-off
   is which query is cheap to read in a diff.

3. **Dual enforcement (C# selective + PowerShell RunAll).** The rule is enforced
   twice today: C# drops suppressed projects from `affected_test_projects` in
   selective scope; `filter-test-matrix-by-scope.ps1` subtracts them from the full
   matrix under RunAll (the two scopes produce different data shapes, so the rule
   lives in two engines). **Recommendation: do NOT consolidate** — fully unifying
   would force the C# selector to enumerate the entire matrix even under RunAll,
   a real behavioral change to a path that's currently dormant in production.
   Instead, **single-source the data**: derive the PowerShell filter's input from
   the same graph the C# engine reads, so the two consumers can't drift. The
   data/policy split does not by itself fix this; it's a separate, smaller change.

4. **Implementation language.** The current tool is C# (`tools/TestSelector`),
   NativeAOT, with a unit suite and `AuditFixtureTests` validating against real
   PRs. A JS rewrite would gain inline-in-Actions execution and lose the test
   suite + AOT speed; the data/policy split needs neither. **Recommendation:**
   implement the split in C#; treat a language change as a separate proposal with
   its own justification. (All four prior-art tools keep this split in their
   native language — Nx in TS, Bazel in Go/Starlark, Pants in Python/Rust — so
   the split is language-agnostic.)

5. **Migration path.** Land the new schema + engine behind the **audit** config
   first (where the override mechanisms already live, dormant), validate with the
   existing `AuditFixtureTests` against real PRs, then promote to the active
   config as a separate, reviewable step. No live CI behavior changes until
   promotion.

## Implementation checklist (for the implementing session)

1. Add the v2 schema (`edges`, `inferDeps`, `jobCategories`, keep `mappings` /
   `ignore` / `runEverything` / `testProjectPatterns`) to
   `test-selection-rules.schema.json`; update `TestSelectorConfig` model.
2. Build the internal edge list from `mappings` (expanded) + `edges`.
3. Rewrite the selection core as the single `selectTests` policy above; fold
   `RestrictedProjectFilter` semantics into the `inferDeps` check (keep it a
   named, unit-tested helper).
4. Derive `run_<category>` booleans from selected tests (matrix categories) and
   `jobCategories.when` (standalone), eliminating the parallel category pass.
5. Migrate `test-selection-rules.audit.json` to the new shape; keep
   `test-selection-rules.json` (active) minimal until promotion.
6. Single-source the PowerShell RunAll filter's restricted/suppressed input from
   the same `inferDeps` data (open decision #3).
7. Update `AuditFixtureTests` and add unit tests for: runtime-edge selection,
   `inferDeps:false` suppression, derived category booleans, conservative
   fallback on unexplained files.
8. Update `eng/scripts/test-selection-rules.README.md` and
   `docs/conditional-tests-run.md`; mark this spec implemented.

## References

Verified verbatim this session:

- Bazel — *Dependencies* (overapproximation invariant; `data` runtime edges):
  <https://bazel.build/concepts/dependencies>
- Pants — *Targets and BUILD files* (hermetic, no-I/O data layer):
  <https://www.pantsbuild.org/stable/docs/using-pants/key-concepts/targets-and-build-files>

From the prior-art research pass (spot-check during review):

- Nx — *Affected* and *Project configuration* (`implicitDependencies`, `!`
  negation, fixed affected algorithm):
  <https://nx.dev/docs/features/ci-features/affected>,
  <https://nx.dev/docs/reference/project-configuration>
- Pants — dependency inference + `!` / `!!` negation (same Targets doc).
- Bazel — target-determinator (content-hash diff; `ignore-and-build-all`
  fallback): <https://github.com/bazel-contrib/target-determinator>
- dorny/paths-filter (globs → booleans extreme):
  <https://github.com/dorny/paths-filter>
- Develocity Predictive Test Selection:
  <https://docs.gradle.com/develocity/current/using-develocity/predictive-test-selection/>
- Azure DevOps Test Impact Analysis:
  <https://learn.microsoft.com/azure/devops/pipelines/test/test-impact-analysis>

Design principles:

- ESR, *The Art of Unix Programming* — Rules of Separation and Representation:
  <http://www.catb.org/~esr/writings/taoup/html/ch01s06.html>
- *Separation of mechanism and policy*:
  <https://en.wikipedia.org/wiki/Separation_of_mechanism_and_policy>
- *Inner-platform effect*: <https://en.wikipedia.org/wiki/Inner-platform_effect>
- *Greenspun's tenth rule*: <https://en.wikipedia.org/wiki/Greenspun%27s_tenth_rule>
