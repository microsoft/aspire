# Test trigger map

A map of **repo path → CI targets that must run** when a matching file changes,
covering the .NET test projects and the validation/polyglot jobs in
[`tests.yml`](../../.github/workflows/tests.yml).

The machine-readable form lives at
[`eng/github-ci/test-trigger-map.yml`](../../eng/github-ci/test-trigger-map.yml). The tool that consumes it and
the rollout plan are in
[`test-trigger-selector-design.md`](./test-trigger-selector-design.md).

## Two layers

Selective CI is split by **who can know a dependency**:

- **Layer 1 — derived (zero maintenance).** `tools/SelectTests` builds an
  MSBuild `ProjectGraph` from `Aspire.slnx` at the PR head and computes the
  reverse `ProjectReference` closure in process. It reports the full affected
  set — **production and test** projects.

  The graph indexes evaluated project inputs (`Compile`, `Content`, `None`,
  `EmbeddedResource`, `AdditionalFiles`, registered `AvailableItemName` types,
  and imports) by resolved `FullPath`. Linked/shared files therefore map to
  every consuming project. Deleted files and rename old paths fall back to
  longest-prefix project directory containment.

- **Layer 2 — curated (this file).** Only what the project graph provably cannot
  see. The selector unions Layer 1's result with the rules here.

Projects not represented by `Aspire.slnx` are Layer-1 blind spots and therefore
Layer 2's responsibility. That includes template placeholders, `playground/**`,
and RID-specific packaging projects under `eng/**`.

## What stays curated here

Only five selector matchers exist; a section is its own key only when the
selector treats it differently. Everything that is "a path glob set → a target
set" lives in one section (`path_rules`); the groupings inside it are comments.

`groups` are reusable bundles, not a matcher. `affected_project_rules` is
separate because it keys off the affected-**project** set by name, not file
paths.

| Section | What it is |
|---------|------------|
| `prefilter` | `{ patterns_file, keep_routed }`. Changed files matched by a pattern in `patterns_file` are **dropped before both layers run** (reaching neither Layer 1 nor Layer 2), except those carved out by `keep_routed` (files the selector routes to a target — `.github/workflows/**`, `eng/pipelines/**`, and the patterns file itself → `Infrastructure.Tests`). `patterns_file` is `eng/github-ci/ci-skip-entirely-patterns.txt`, the same list the top-level CI skip gate uses, read at runtime so the two can't drift; its glob syntax is the check-changed-files action's (ported in `ChangedFileFilter`). Why this — not `ignore` — is what stops the `README.md` fan-out: see [test-trigger-selector-design.md](./test-trigger-selector-design.md) §Layer 2. |
| `conventions` | `<name>`-capture pattern → target template, emitted only if the derived test exists (existence guard). Additive. Covers a test's own folder and the Hosting/Components integration dirs as a backstop for non-MSBuild files the graph cannot attribute. |
| `ignore` | globs Layer 2 accounts for with **no** target, so they do not trip the run-all fallback. Link-compiled `src/Shared` / `tests/Shared` / `Components/Common` files are attributed by Layer 1; the curated entries cover only paths that still need an explicit exemption. See [test-trigger-selector-design.md](./test-trigger-selector-design.md) §Layer 2. |
| `path_rules` | a path glob set → a target set (`test:` / `job:` / a group / `ALL`). The single general path matcher: catch-all-to-`ALL`, convention misses, non-.NET job loose-file triggers, and loose-file reads all live here under comment headers |
| `affected_project_rules` | an affected **production** project, matched by project-name glob against Layer 1's affected set, → a target set. Matched against production names **only** — affected matrix test projects are excluded, so a test-only change cannot fire production jobs via a glob like `Aspire.Hosting*`. Follows the graph's transitive closure |
| `derived_targets` | "if any of these tests is selected, also run these jobs/tests" — a *test-set* relationship, not a file edge |
| `groups` | named, reusable bundles of `test:`/`job:` targets that expand recursively |

The map stays small by keeping each dependency in the layer that can prove it:

- broad run-all paths are `path_rules` entries whose target is `ALL`;
- a test project's own folder is a convention;
- override/job/loose-file buckets are all `path_rules`, because they have no
  distinct selector behavior;
- linked-file, `Components/Common`, and `src/Shared` / `tests/Shared` `*.cs` edges
  are owned by Layer 1 at runtime (the attributed-paths set), so they need no
  curated entry;
- docs and other no-CI files are dropped up front by `prefilter`, reading the
  CI skip-gate patterns file.

## Target vocabulary

| Target | Maps to |
|--------|---------|
| `test:<Name>` | a .NET test project `tests/<Name>` (a `run-tests.yml` matrix entry) |
| `job:polyglot` | `tests.yml` → [`polyglot-validation.yml`](../../.github/workflows/polyglot-validation.yml) (py/go/java/rust/ts) |
| `job:typescript-sdk` | `tests.yml` → [`typescript-sdk-tests.yml`](../../.github/workflows/typescript-sdk-tests.yml) |
| `job:typescript-api-compat` | `tests.yml` → [`typescript-api-compat.yml`](../../.github/workflows/typescript-api-compat.yml) |
| `job:extension-unit` | `tests.yml` `extension_tests_win` + `extension_bootstrap_linux` |
| `job:extension-e2e` | `tests.yml` → [`extension-e2e-tests.yml`](../../.github/workflows/extension-e2e-tests.yml) |
| `job:winget-installer` | `tests.yml` `prepare_winget_installer_artifacts` |
| `job:homebrew-installer` | `tests.yml` `prepare_homebrew_installer_artifacts` |
| `job:nix-package` | `tests.yml` `nix_package` |
| `job:deployment-e2e` | [`deployment-tests.yml`](../../.github/workflows/deployment-tests.yml) — *schedule/dispatch-only today* |
| `ALL` | every selector target; PR CI runs the full PR test matrix and all PR-gated jobs, while independently scheduled, dispatched, or outerloop targets remain advisory |
| `<GROUP_NAME>` | a named group (see `groups:`) expanding **recursively** to its `test:`/`job:` members |

Unconditional baseline builds are not selector targets. Gated reusable
workflows are routed to the target they implement or serve.

## Rule categories

Rules are **additive**: a changed file activates the union of targets from every
matching rule, plus the Layer 1 projects. A `path_rules` entry whose target is
`ALL` selects the whole matrix.

Group names in a rule's targets expand recursively to their members.

### Named groups (`groups`)

Reusable bundles of `test:` and/or `job:` targets, so a rule can map to a named
set instead of repeating it. Example:

```yaml
groups:
  CLI_BUNDLE: [test:Aspire.Cli.EndToEnd.Tests, job:extension-e2e, job:winget-installer, job:homebrew-installer]
```

Group members may themselves be group names; expansion is recursive and
cycle-safe.

### Conventions (`conventions`)

A `<name>` capture (one path segment) → a target template, emitted only when the
derived test exists in the matrix (existence guard), and additive. This includes
a test project's own folder and the integration/component backstop:

```text
tests/<name>/**               -> test:<name>
src/Aspire.Hosting.<name>/**  -> test:Aspire.Hosting.<name>.Tests
src/Components/<name>/**      -> test:<name>.Tests
```

`tests/Shared/**`, `src/Aspire.Hosting.Azure.CosmosDB/**`, `Orleans`, and other
dirs with no same-named test produce nothing here. They fall through to
`path_rules` / Layer 1.

### Catch-all → `ALL`

A single `path_rules` entry whose target is `ALL`. Build infrastructure and
broadly shared code re-run everything. Examples:

```text
global.json, NuGet.config, .config/dotnet-tools.json
Directory.Build.*, Directory.Packages.props, Aspire.slnx
eng/*.props, eng/*.targets, eng/common/**, eng/OuterPreBuild.proj
tests/Shared/**/*.props, tests/Shared/**/*.targets, tests/Shared/Dockerfile*
.github/workflows/tests.yml, run-tests.yml, build-packages.yml, ...
```

`Directory.Packages.props` is intentionally here. Layer 1 uses a HEAD-only graph
and does not attempt two-commit central-package diffing, so central package
changes run `ALL`.

Note `eng/OuterPreBuild.proj` (build-wide project-name validation) is here, but
`eng/Bundle.proj` is **not** — it assembles only the CLI bundle, so it maps to
`CLI_BUNDLE`, not `ALL`.

### Ignore (`ignore`)

Files Layer 2 deliberately accounts for with **no** target, so they do not trip
the run-all fallback. Entries are either covered precisely by Layer 1 or
intentionally outside GitHub PR CI. Shared source examples:

```text
src/Components/Common/**                          # link-compiled into many components; Layer 1 covers
src/Vendoring/OpenTelemetry.Instrumentation.*/**  # glob-compiled into Redis/Kafka components; Layer 1 covers
```

This also includes paths already exercised by unconditional or dedicated
workflows, schedule/dispatch-only inputs, and paths with no PR-CI consumer.
Each entry's YAML comment records which case applies.

### Path rules (`path_rules`)

The one general path-glob → targets matcher. `targets` may be `test:` / `job:` /
a group name / `ALL`. Comment headers in the YAML group the entries by intent;
the selector treats them all identically.

Highlights:

- **convention misses** — `src/Aspire.Hosting.Azure.*/**` →
  `test:Aspire.Hosting.Azure.Tests`, and
  `src/Aspire.Hosting.Integration.Analyzers/**` →
  `test:Aspire.Hosting.Analyzers.Tests`.
- **non-.NET job loose triggers** — only the paths the project graph cannot
  attribute, such as `tests/PolyglotAppHosts/**`, checked-in `*.ats.txt` /
  `*.tscompat.suppression.txt` baselines, `tools/TypeScriptApiCompat/**`, and
  `extension/**`. A `src/Aspire.Hosting*/api/*.ats.txt` baseline fans out to
  **both** `job:typescript-api-compat` (baseline diff) and `job:polyglot`,
  because the polyglot playground regenerates and compiles that exported surface
  in every language.
- **loose-file deps** — `eng/clipack/**`, `eng/winget/**`, `eng/homebrew/**`,
  `src/Aspire.ProjectTemplates/**`, `playground/**`, `.github/workflows/**`,
  and `eng/Bundle.proj`.

### Project rules (`affected_project_rules`)

An affected **production** project → a target set, matched by project-**name**
glob against Layer 1's affected set. Matrix test projects are handled by the
Layer 1 intersection.

This is keyed by project identity rather than literal production-project path
globs, so it follows the graph's transitive closure and survives project
directory moves. It is additive and inert when Layer 1 is explicitly skipped
with `--skip-layer1`; the loose-file `path_rules` still cover those triggers.

Project name means the `.csproj` base name, which is what Layer 1 emits.

```yaml
- projects: [Aspire.Hosting*, Aspire.Cli]
  targets: [job:typescript-api-compat]
```

### Derived targets (`derived_targets`)

"If **any** of these test projects is selected by either layer, also run these
targets." Applied to the union of Layer 1 and Layer 2 selected tests, to a
fixpoint.

A `test → test` edge whose target has its own rule is followed; cycles terminate.
This is how a job fires based on *which tests run*, not on which file changed:

```yaml
- tests: [test:Aspire.Acquisition.Tests]
  targets: [job:winget-installer, job:homebrew-installer]
```

## Maintenance

The map is hand-curated; there is no generator. Do not add a rule only because a
file changed or a verifier failed. First trace the file to the work that consumes
it.

1. **Decide which layer owns the dependency.**
   - If a project in the `Aspire.slnx`-rooted `ProjectGraph` evaluates the file
     and MSBuild expresses the dependency, Layer 1 owns it. Do not add a manual
     rule for a new `ProjectReference`, linked file, or ordinary project input.
   - Otherwise it is a Layer 2 blind spot. Common examples are workflow
     implementations, scripts invoked by CI, and configuration read directly by
     a test or job.
2. **Identify the actual consumer.** Follow script invocations, reusable-workflow
   calls, project imports, and job `needs:` edges. Determine whether the consumer
   is a selector-gated PR test/job, an unconditional or dedicated workflow, a
   schedule/dispatch-only workflow, or has no CI consumer.
3. **Choose the narrowest accurate representation.**
   - use `path_rules` for a path with specific `test:` or `job:` consumers;
   - use `ALL` only for inputs shared broadly enough to invalidate precise
     routing;
   - use `ignore` when Layer 2 must account for a path but the selector has no
     target to schedule; record why in the adjacent YAML comment;
   - add a top-level skip pattern only when the path cannot affect main CI, or a
     dedicated workflow fully validates it; preserve any `keep_routed` carve-out;
   - use `affected_project_rules` only when an affected production project
     implies work beyond the graph-selected tests, and `derived_targets` only
     when selecting one test inherently requires another target.
4. **Wire jobs end to end.** A selector-gated `job:` target needs a matching
   `run_*` output in `tests.yml`, a gate that consumes that output, and a route
   from any reusable workflow that implements the job. Targets outside those PR
   gates must remain advisory rather than appearing as PR-runnable work.
5. **Add a regression that would catch both under- and over-selection.**
   - curated routing changes use the real map in `TestTriggerMapTests.cs` and
     compare the complete selected target sets;
   - selector-engine behavior uses focused synthetic maps in
     `SelectTestsAcceptanceTests.cs`;
   - CLI rendering and side-channel behavior belongs in
     `SelectTestsCliTests.cs`;
   - action or workflow-gate contracts belong in `SelectTestsWorkflowTests.cs`
     or an exact real-workflow invariant.

After restoring the repository, run the focused selector suite:

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-namespace "Infrastructure.Tests.TestTriggerMap" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

Finally, inspect the PR audit comment or job summary. Confirm that every changed
file is attributed, the selected PR tests/jobs are the expected complete sets,
and independently scheduled targets appear only as advisory impact.

The hand-owned knowledge (`conventions`, `ignore`, `path_rules`,
`derived_targets`) encodes dependencies a fresh codebase read cannot recover, so
carry it forward. Never silently regenerate it.

## Caveats

- **Convention is a backstop, not the primary signal.** Normal compiled changes
  are attributed by Layer 1. The convention exists for non-MSBuild files the
  graph cannot see, and to keep the common case selective instead of falling to
  the run-all fallback.
- **Run-all fallback.** A changed file (anywhere, not just `src/**`) that no
  Layer 2 rule matched, that is not ignored, and that is not Layer-1-owned (under
  no project in `Aspire.slnx`) forces the full matrix. A missed test is a silent
  regression; an extra run is just slower. Files that need no CI are dropped by
  the `prefilter` before this point.
- **Rename handling.** Renames are not special-cased. Both the old and new path
  of a git-detected rename (`git diff -M`, default 50% content-similarity
  threshold) are evaluated exactly like any other changed path, including
  against the run-all fallback above — an unmatched old path forces `ALL` just
  like an unmatched deletion would.

  An earlier version of this rule exempted a rename's old path from forcing
  `ALL` when its new path was itself present in the (post-`prefilter`)
  changed-file set, on the theory that the new path's own evaluation already
  accounted for the moved content. That exemption was removed after an audit
  found two ways it could silently under-select tests:
  - **Shared-directory moves.** A `path_rules` glob scoped to the old
    directory (e.g. `tests/Shared/Logging/**`) can have consumers beyond the
    file that moved. Moving one file out of that directory into an unrelated
    project's own directory exempted the old path once the new path matched
    *its* rule — but any other file still left behind in the old directory,
    and depending on the moved content, got no signal that anything changed.
  - **Ignore/prefilter laundering.** A rename whose new path lands on an
    `ignore`d glob, or is dropped by the `prefilter` before Layer 2 ever runs,
    "satisfied" the exemption's presence check without the destination
    contributing any real coverage — silently treating the old path as
    accounted for when nothing had actually evaluated it.

  Removing the exemption reopens
  [microsoft/aspire#19486](https://github.com/microsoft/aspire/pull/19486)'s
  own motivating case — a same-commit rename that also repoints the map's own
  rule at the new name now correctly forces `ALL` again, because the stale old
  name matches nothing. That is the accepted, safe tradeoff: an unnecessary
  full run is strictly better than a silently skipped test, and it matches the
  fallback's existing philosophy of failing safe to `ALL` for anything it
  cannot positively account for.
- **Layer 1 failure is fatal.** Audit mode returns run-all only after a
  successful selection. A graph-computation failure fails the selector in every
  mode.
- **Safety vs. selectivity.** The catch-all `ALL` rule, the run-all fallback, and
  the kill switch err toward `ALL`; otherwise the selector relies on Layer 1 for
  `src` coverage and the convention backstop for non-MSBuild files.
- **Independent workflow targets.** `deployment-e2e`,
  `Aspire.Deployment.EndToEnd.Tests`, `Aspire.EndToEnd.Tests`, and
  `Aspire.Oracle.EntityFrameworkCore.Tests` are not in the regular PR matrix
  today; their rules give the *would-be* trigger paths. Their own schedules,
  dispatches, or narrow PR workflow triggers decide whether they run. Comments
  and job summaries list them separately from work the PR selector can actually
  run. The API/ATS baseline regeneration workflows are independently scheduled
  or dispatched and ignored as selector targets, as described in `ignore` above.
- **Integration dirs with no test.** `src/Aspire.Hosting.Orleans`,
  `Aspire.Hosting.AppHost`, and `Aspire.Hosting.Tasks` have no dedicated test
  project. Their MSBuild files are owned by Layer 1, and their non-MSBuild files
  fall through to curated rules or the fallback.
