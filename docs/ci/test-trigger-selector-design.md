# Test trigger selector — design

A design for a tool that takes a PR's changed files and emits the set of test
projects and CI jobs to run, so PR CI runs a relevant subset instead of the full
matrix.

Companion documents:

- [`test-trigger-map.md`](./test-trigger-map.md) — the descriptive path → target map.
- [`test-trigger-map.yml`](./test-trigger-map.yml) — its machine-readable form.

**Status: proposed.** Nothing consumes this design yet. The numbers in the
[Selectivity](#measured-selectivity) section are measured against the live repo
(see [Reproducing the measurements](#reproducing-the-measurements)).

## Goal

Input: the list of files changed in a PR. Output:

- the subset of `test:<Project>` entries to run, and
- which non-.NET jobs to trigger (`job:polyglot`, `job:extension-e2e`, …).

Filter the existing full matrix down — do **not** rebuild it.

## Why not just consume `test-trigger-map.yml`?

The map mixes two kinds of rule. The large sections (`leaf_source`,
`core_source` fan-out, `aliases`, `shared_compiled_source`) are mechanically
derivable from the `.csproj` graph, and go stale the moment a project or a
`ProjectReference` changes. Hand-maintaining them is a maintenance trap.

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
  --solution-path Aspire.slnx \
  --from "$base_sha" --to "$head_sha" \
  --format json --output-dir "$out" --output-name affected
```

Output is `[{ "Name": "...", "FilePath": "..." }]`, where `Name` is exactly the
matrix `projectName`. The tool reads `.Name` and intersects it with the
enumerated matrix — no path-to-name mapping needed.

What Layer 1 covers (each verified against the `dotnet-affected` source and a
probe — see [evidence](#appendix-validation-evidence)):

- **`ProjectReference` reverse closure** over all graph edges.
- **Foreign linked `<Compile Include>`** of another project's `.cs` file: if only
  that linked file changes, the *consuming* project is reported. This is why
  `shared_compiled_source` does not need to be hand-maintained.
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
   `--solution-path Aspire.slnx` excludes them.
2. **The SDK must be resolvable by MSBuildLocator.** Ensure `DOTNET_ROOT` (or the
   `global.json` `paths` local SDK) points at the SDK the rest of CI uses.

`dotnet-affected` ships as a `dotnet tool` (latest `6.2.0`). Per repo NuGet
policy it must be mirrored to an approved internal feed (dnceng) before CI use;
it cannot be pulled from nuget.org in the internal build.

## Layer 2 — curated (the only hand-maintained part)

What the MSBuild graph provably cannot see. Roughly 80 lines of glob → target:

- **Non-.NET jobs:** `polyglot`, `typescript-sdk`, `typescript-api-compat`,
  `extension-unit`, `extension-e2e`, `cli-starter`, and the schedule-only
  `api-diffs` / `ats-diffs` / `deployment-e2e`. Today only `extension-e2e` is
  path-gated (`.github/workflows/tests.yml`, `extension_e2e_changes` job); the
  rest run unconditionally on PRs, so the selector *adds* gating for them.
- **Runtime loose-file reads** — tests that read files by path at runtime, with
  no MSBuild edge: `eng/clipack/**` (read by `Aspire.Cli.Tests`),
  `src/Aspire.ProjectTemplates/**` (template hive install by
  `Aspire.Templates.Tests`), `.github/workflows/**` (asserted by
  `Infrastructure.Tests`), `playground/**`, `eng/dcppack/**`,
  `eng/scripts/get-aspire-cli*`, `tools/QuarantineTools/**`, etc.
- **`run_all_globs`** catch-all → `ALL`, trimmed of what Layer 1 now covers
  (`Directory.Packages.props`, `Directory.Build.*`).

These are sourced from the existing `curated_jobs`, `loose_file_deps`, and
`run_all_globs` sections of `test-trigger-map.yml`.

## The tool (`tools/SelectTests`)

A small C# console tool, run as a step after `enumerate-tests` and before the
matrix split:

1. **Inputs:** changed files (or `--from`/`--to`), the `all_tests` matrix JSON,
   the curated YAML.
2. **Layer 1:** invoke `dotnet-affected`; read affected/changed project names.
3. **Layer 2:** apply curated globs additively. Any `run_all_globs` match
   short-circuits to `ALL`.
4. **Fail-open:** a `src/**` file matched by neither layer → `ALL`. A missed test
   is a silent regression; an extra run is just slower.
5. **Kill switch:** a `[full ci]` token in the PR or a `run-all-tests` label →
   `ALL`.
6. **Outputs:** the filtered matrix (`include[]` entries whose top-level
   `projectName` is selected) plus per-job booleans (`run_polyglot`,
   `run_extension_e2e`, …).

Selection only decides *which* projects survive. OS expansion, timeouts,
`requiresNugets` / `requiresCliArchive` flags, and the matrix split stay owned by
the existing scripts.

## Pipeline integration

The current flow:

```text
enumerate-tests (action)  ->  all_tests JSON {"include":[...]}
  ->  split-test-matrix-by-deps.ps1  (keys off entry.properties.requiresNugets / requiresCliArchive)
  ->  run-tests.yml (per-dependency matrices)
```

`SelectTests` inserts one step: filter `all_tests` `include[]` by `projectName`
**before** the split. The split, the per-OS/per-dependency bucketing, and
`run-tests.yml` are unchanged. The per-job booleans feed the `if:` conditions
that gate the non-.NET jobs in `tests.yml`.

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

- **Referential integrity:** every curated `test:` / `job:` target names a real
  test project / a real `tests.yml` job; every glob is valid.
- **Coverage:** every test project and every `src` project is reachable by some
  rule, so a newly added, unmapped project fails CI loudly instead of silently
  never running.
- **Oracle cross-check:** use `dotnet-affected --assume-changes <project>` as
  ground truth and assert the selector's output is a superset of the
  graph-affected set.

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

dotnet-affected --solution-path Aspire.slnx \
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
  `XmlEncodedProjectName.*` template placeholder projects; `--solution-path
  Aspire.slnx` is required.
- **Layer 2 is genuinely needed** — `dotnet-affected` models only MSBuild
  projects; it does not see non-.NET jobs or runtime-only file reads (verified:
  `Aspire.Cli.Tests` reads `eng/clipack/npm/aspire.js`, `Aspire.Templates.Tests`
  installs the template hive, `Infrastructure.Tests` reads `.github/workflows/**`
  — none are MSBuild items).
