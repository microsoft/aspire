# Test trigger selector — design

A design for a tool that takes a PR's changed files and emits the set of test
projects and CI jobs to run, so PR CI runs a relevant subset instead of the full
matrix.

Companion documents:

- [`test-trigger-map.md`](./test-trigger-map.md) — the descriptive path → target map.
- [`test-trigger-map.yml`](./test-trigger-map.yml) — its machine-readable form.

**Status: wired in audit mode.** `tests.yml`'s `setup_for_tests` runs `SelectTests`
and emits the advisory summary, but it runs in audit mode (`ENFORCE_SELECTION:
'false'`), so the selector returns run-all and the full matrix and all jobs still
run. The numbers in the
[Selectivity](#measured-selectivity) section are measured against the live repo
(see [Reproducing the measurements](#reproducing-the-measurements)).

## Goal

Input: the list of files changed in a PR. Output:

- the subset of `test:<Project>` entries to run, and
- which non-.NET jobs to trigger (`job:polyglot`, `job:extension-e2e`, …).

Filter the existing full matrix down — do **not** rebuild it.

## Why not just consume `test-trigger-map.yml`?

The map mixes two kinds of rule. The large graph-derived edges (a leaf
integration → its own test, the core fan-out, and foreign `<Compile Include>`
consumers) are mechanically derivable from the `.csproj` graph and go stale the
moment a project or a `ProjectReference` changes. Hand-maintaining them is a
maintenance trap.

So the design splits by **who can know the dependency**:

- **Layer 1 — derived (zero maintenance):** changed `src` file → owning project
  → reverse-dependency closure → affected test projects. Computed from the live
  MSBuild graph every run, so it can never drift.
- **Layer 2 — curated (~80 lines):** only what the MSBuild graph cannot see —
  the non-.NET jobs and runtime/loose-file reads.

## Layer 1 — derived, via `dotnet-affected`

[`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected) builds an
MSBuild `ProjectGraph`, maps changed files to their owning projects, and returns
the reverse-dependency closure. It replaces the graph-derived sections of the
map outright.

Invocation (run **after** restore, since `ProjectGraph` evaluation needs the
Arcade MSBuild SDKs from `global.json` restored):

```bash
dotnet-affected \
  --filter-file-path Aspire.slnx \
  --from "$base_sha" --to "$head_sha" \
  --format json --output-dir "$out" --output-name affected
```

(`--filter-file-path` is the current flag for scoping discovery to a solution;
the older `--solution-path` alias is marked obsolete in `dotnet-affected` 6.2.0.)

Output is `[{ "Name": "...", "FilePath": "..." }]`, where `Name` is the project
name (the `.csproj` base name; for a test project, exactly the matrix
`projectName`). The tool keeps the **full** affected set: it intersects the test
names with the enumerated matrix (the selected test projects) and matches the
**production** names against `affected_project_rules` (the job/test triggers that used to be
hand-written `src/<Project>/**` path globs).

What Layer 1 covers (each verified against the `dotnet-affected` source and a
probe — see [evidence](#appendix-validation-evidence)):

- **`ProjectReference` reverse closure** over all graph edges.
- **Foreign linked `<Compile Include>`** of another project's `.cs` file: if only
  that linked file changes, the *consuming* project is reported. This is why the
  map no longer carries a `shared_compiled_source` section (nor a
  `src/Components/Common` rule) — the graph attributes those edges live.
- **Central Package Management** (`Directory.Packages.props`) version bumps →
  consuming projects.
- **`Directory.Build.props` / `.targets`** → dependent projects.

The tool must take the **union** of `dotnet-affected`'s *changed* and *affected*
sets: a project that only link-compiles a foreign file shows up as *changed*,
not *affected*.

### Two hard operational constraints

1. **Scope to `Aspire.slnx`.** Raw filesystem discovery (`-p <repo>`) crashes
   loading the template placeholder projects under
   `src/Aspire.ProjectTemplates/templates/.../XmlEncodedProjectName.*.csproj`,
   whose `ProjectReference`s only resolve when a template is instantiated.
   `--filter-file-path Aspire.slnx` excludes them.
2. **The SDK must be resolvable by MSBuildLocator.** Ensure `DOTNET_ROOT` (or the
   `global.json` `paths` local SDK) points at the SDK the rest of CI uses.

`dotnet-affected` ships as a `dotnet tool` (latest `6.2.0`). Per repo NuGet
policy it must be mirrored to an approved internal feed (dnceng) before CI use;
it cannot be pulled from nuget.org in the internal build.

## Layer 2 — curated (the only hand-maintained part)

What the MSBuild graph provably cannot see, plus a convention backstop. Exactly
five matchers — a section is a key only when the selector treats it differently:

- **`conventions`**: `<name>`-capture pattern → target template, additive and
  existence-guarded. Covers a test's own folder (`tests/<name>/**` →
  `test:<name>`) and the integration backstop (`src/Aspire.Hosting.<name>/**`,
  `src/Components/<name>/**`) for non-MSBuild files the graph can't attribute.
- **`ignore`**: shared dirs Layer 1 already covers (`src/Components/Common`, the
  vendored OTel instrumentation dirs) or that are inert
  (`src/Vendoring/OpenTelemetry.Shared`), listed so they don't trip the fallback.
- **`path_rules`**: the one general path-glob → targets matcher (`test:` / `job:` /
  group / `ALL`). Comment headers group its entries by intent — they have no
  distinct selector behavior:
  - the **catch-all** entry (target `ALL`), trimmed of what Layer 1 now covers;
  - **convention misses** (`src/Aspire.Hosting.Azure.*/**` → `Aspire.Hosting.Azure.Tests`);
  - **non-.NET job loose triggers** — only the paths the graph can't attribute
    (`tests/PolyglotAppHosts/**`, the `*.ats.txt` / `*.tscompat.suppression.txt`
    baselines, `tools/TypeScriptApiCompat/**`, `extension/**`); the jobs'
    *production-project* triggers live in `affected_project_rules`;
  - the **linked-compiled** `src/Aspire/Cli/**` (no owning project);
  - **loose-file reads** (`eng/clipack/**`, `eng/winget/**`, `eng/homebrew/**`,
    `src/Aspire.ProjectTemplates/**`, `.github/workflows/**`, `playground/**`, …).
- **`affected_project_rules`**: an affected **production** project (matched by project-name
  glob against Layer 1's affected set) → a target set. Replaces the duplicated
  `src/<Project>/**` job globs and follows the graph's transitive closure (a
  dependency change marks the project affected). Inert when Layer 1 is skipped.
- **`derived_targets`**: "if **any** of these tests is selected, also run these
  targets", applied to the union of both layers to a fixpoint (e.g. CLI tests →
  `cli-starter`; acquisition → the installer jobs).

These are sourced from the corresponding sections of `test-trigger-map.yml`.

## The tool (`tools/SelectTests`)

A small C# console tool, run as a step after `enumerate-tests` and before the
matrix split:

1. **Inputs:** changed files (or `--from`/`--to`), the `all_tests` matrix JSON,
   the curated YAML, and the `Aspire.slnx` project dirs (for attribution).
2. **Layer 1:** invoke `dotnet-affected`; read the full affected project set
   (production + test). Test names ∩ matrix are selected; production names feed
   `affected_project_rules`.
3. **Layer 2:** per changed file — apply `conventions` (existence-guarded),
   `path_rules` (expanding groups recursively; a `targets: [ALL]` rule selects the
   whole matrix), and `ignore` (accounts for a file with no target).
4. **`affected_project_rules`:** for each affected production project, add the targets of
   every rule whose project-name glob it matches.
5. **Derived pass:** for each selected test (Layer 1 ∪ Layer 2 ∪ affected_project_rules),
   add its `derived_targets`, to a fixpoint (cycle-safe).
6. **Run-all fallback:** a `src/**` file matched by no Layer 2 rule, not ignored,
   and *not* under a project in `Aspire.slnx` (so `dotnet-affected` didn't
   attribute it) → `ALL`. A missed test is a silent regression; an extra run is
   just slower. Non-`src` leftovers are audit-only.
7. **Kill switch:** a `[full ci]` token in the PR or a `run-all-tests` label →
   `ALL`.
8. **Outputs:** the filtered matrix (`include[]` entries whose top-level
   `projectName` is selected) plus per-job booleans (`run_polyglot`,
   `run_extension_e2e`, …).

Selection only decides *which* projects survive. OS expansion, timeouts,
`requiresNugets` / `requiresCliArchive` flags, and the matrix split stay owned by
the existing scripts.

## Pipeline integration

The flow in `tests.yml`'s `setup_for_tests` job:

```text
enumerate-tests (action)  ->  all_tests JSON {"include":[...]}
  ->  SelectTests  (--from base --to head; reads all_tests + the curated map; Layer 1 via
                    dotnet-affected)  ->  selected_matrix.json + run_* outputs + audit summary
  ->  split-test-matrix-by-deps.ps1  (keys off entry.properties.requiresNugets / requiresCliArchive)
  ->  run-tests.yml (per-dependency matrices)
```

`SelectTests` runs as one step after `enumerate-tests` and before the split. The
split, per-OS/per-dependency bucketing, and `run-tests.yml` are unchanged. The
`run_*` step outputs become `setup_for_tests` job outputs that gate every non-.NET
job — `polyglot_validation`, `typescript_sdk_tests`, `typescript_api_compat`,
`extension_tests_win`, `extension_bootstrap_linux`, `extension_e2e_tests`,
`cli_starter_validation_windows`, and the WinGet/Homebrew installer-prepare jobs —
via their `if:`, combined with each job's existing event conditions. (The .NET test
jobs need no `run_*` gate: they are already gated by their matrix bucket being
empty once SelectTests filters the matrix. Base builds stay ungated — they are
upstream `needs:` that run whenever a dependent does.) This replaces the old
hand-rolled `extension_e2e_changes` regex job.

The extension-unit jobs (`extension_tests_win` / `extension_bootstrap_linux`) gate
on `run_extension_unit` **or** `run_extension_e2e`, because `extension_e2e_tests`
`needs:` them — gating them off while e2e runs would skip e2e via need-propagation.

**Audit vs. enforce is a single knob in the `select_tests` step,
`ENFORCE_SELECTION`** (default `'false'`). The rest of `tests.yml` just consumes
what the selector returns — every gate reads `run_X == 'true'`, and the split
consumes the emitted matrix. In audit (`'false'`, no `--enforce`) the selector
**returns run-all**: the full matrix plus `run_* = true`, with the advisory summary
showing what enforcing *would* have selected. Flipping it to `'true'` makes the
selector return the selective matrix and `run_*`, and the same gates then narrow.
The tool fails **open by design** when it *decides* to run everything (the kill
switch, or a non-PR event with no base — see below): it emits the full matrix and
sets every `run_*` to true. A non-zero exit therefore means the selector itself
crashed, and the step lets that **fail the run** rather than masking the bug behind
a silent fallback — surfacing it is the point of the audit phase.

The kill switch is wired in the same step: a `[full ci]` token in the PR body or a
`run-all-tests` label passes `--force-all`. Non-PR events (no base SHA at all, e.g.
a push to `main`) also force the full set. A PR *with* a base SHA that cannot be
fetched **fails the step** instead of forcing run-all: `base.sha` is always
reachable on origin, so a fetch failure is a real problem, and masking it with
run-all would teach the audit nothing.

dotnet-affected adds an MSBuild `ProjectGraph` evaluation to the critical-path
`setup_for_tests` job; it is a local tool restored via `dotnet tool restore` and
must be available on the CI NuGet feeds (dnceng), not nuget.org.

## Measured selectivity

Measured with `dotnet-affected --assume-changes <project>` scoped to
`Aspire.slnx`:

| Change                            | Affected test projects |
|-----------------------------------|------------------------:|
| `Aspire.Cli`                      | 1                       |
| `Aspire.Dashboard`                | 7                       |
| `Aspire.Hosting.Yarp` (integration) | 36                   |
| `Aspire.Hosting.PostgreSQL` (integration) | 36            |
| `Aspire.Npgsql` (data component)  | 37                      |
| `Aspire.Hosting` (core)           | 44                      |

Two structural "god edges" make any hosting-integration or data-component change
fan out to ~36 hosting tests:

- `tests/Aspire.Hosting.Tests` `ProjectReference`s several integrations and is
  itself referenced by ~34 hosting test projects (the hub).
- `tests/testproject` (`TestProject.AppHost`, `IntegrationServiceA`) references a
  broad component set, bridging data-component changes into the hosting cluster.

**This fan-out is accepted, not a defect to fix.** Running ~36 hosting tests for
a hosting-integration change is still far cheaper than the full matrix, and
pruning those edges would change what "affected" means for the test owners. The
selector deliberately does not prune them.

The clean wins remain large and safe:

- CLI-only (1), Dashboard-only (7), and extension / TypeScript / polyglot-only
  changes stay tightly scoped.
- Component ↔ component isolation holds: an `Aspire.Npgsql` change does **not**
  pull the Redis / RabbitMQ / MongoDB / Milvus component tests.

## Audit mode

The default mode at rollout. The selector computes the subset and writes a
`$GITHUB_STEP_SUMMARY`, but CI **still runs the full matrix**. The summary shows:

- selected test projects and triggered jobs, grouped by the rule that selected
  them;
- the **would-have-been-skipped** list — the whole point: what selective CI would
  have dropped;
- any `ALL` / fail-open / kill-switch escalation and why.

Any audit run where a would-be-skipped test would have failed is a map bug, fixed
before enforcing. Once audit data shows the skip set is consistently safe, flip
to enforcing; keep the `[full ci]` kill switch.

## Verifier test

A test under `Infrastructure.Tests` (which already asserts on workflow files)
keeps the curated layer honest:

- **Referential integrity:** every curated `test:` / `job:` target (including
  `affected_project_rules` and `derived_targets`) names a real test project / a known
  `tests.yml` job; every path glob is valid; every `affected_project_rules` project-name
  glob matches at least one project in `Aspire.slnx` (so a renamed project fails
  loudly); every `conventions` pattern carries a `<name>` placeholder that its
  target substitutes.
- **Coverage:** every test project and every `src` project is reachable by some
  rule (or `Aspire.slnx`), so a newly added, unmapped project fails CI loudly
  instead of silently never running.

A convention-miss dir with no same-named test (the `Azure.<service>` family,
`Orleans`, `AppHost`, `Tasks`) is intentionally not asserted: its MSBuild files
are owned by Layer 1, and a non-MSBuild change there safely hits the run-all
fallback (or the convention backstop). A specific `path_rules` entry (like the
Azure one) is a selectivity *nicety*, not a correctness requirement, so it is not
verifier-enforced.

## Rollout

1. Land `SelectTests` + the trimmed curated YAML + the verifier test, in **audit
   mode** only.
2. Watch the audit summaries; fix any unsafe skips in the curated layer.
3. Flip to enforcing. Keep the kill switch and fail-open defaults.

## Future refinement

Refinement is about the curated layer staying accurate, not about changing the
graph:

- new non-.NET jobs or runtime file dependencies get curated rules;
- the verifier catches new unmapped projects;
- audit data flags any rule that under- or over-selects.

God-edge pruning is explicitly out of scope.

## Reproducing the measurements

```bash
./restore.sh                       # ProjectGraph needs the Arcade SDKs restored
dotnet tool install -g dotnet-affected
export DOTNET_ROOT=<sdk-root>      # so MSBuildLocator resolves the SDK

dotnet-affected --filter-file-path Aspire.slnx \
  --assume-changes Aspire.Hosting.Yarp \
  --format json --output-dir /tmp/affected --output-name a
# count *.Tests entries in /tmp/affected/a.json
```

## Appendix: validation evidence

- **Linked-`<Compile Include>` detection** — a project that only link-compiles a
  foreign `.cs` (no `ProjectReference`) is reported when that file alone changes.
  Confirmed with a minimal two-project repro.
- **Output shape** — `--format json` emits `[{Name, FilePath}]`; `Name` equals
  the matrix `projectName`.
- **Union semantics** — link-compile consumers appear in the *changed* set, not
  *affected*; take the union.
- **Solution scoping** — filesystem discovery crashes on the
  `XmlEncodedProjectName.*` template placeholder projects; `--filter-file-path
  Aspire.slnx` is required (the older `--solution-path` alias is obsolete).
- **Layer 2 is genuinely needed** — `dotnet-affected` models only MSBuild
  projects; it does not see non-.NET jobs or runtime-only file reads (verified:
  `Aspire.Cli.Tests` reads `eng/clipack/npm/aspire.js`, `Aspire.Templates.Tests`
  installs the template hive, `Infrastructure.Tests` reads `.github/workflows/**`
  — none are MSBuild items).
