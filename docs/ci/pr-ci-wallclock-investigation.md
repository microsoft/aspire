# PR CI wall-clock investigation

This is a living log of an investigation into the wall-clock time of the
GitHub Actions PR-gating CI for `microsoft/aspire`. Numbers are linked to
concrete run IDs so they can be re-checked.

Started 2026-06-06.

## Baseline (before any change in this investigation)

| Run | Type | Total wall | Critical-path gate | Critical-path end | Link |
|---|---|---:|---|---:|---|
| 27035802122 | push to main | 28.8 min | `Templates-NewUpAndBuildStandaloneTemplateTests` (windows) 14.6min | +26.0 min | [run](https://github.com/microsoft/aspire/actions/runs/27035802122) |
| 27052863313 | PR | 26.4 min | same (windows) 13.2min | +24.4 min | [run](https://github.com/microsoft/aspire/actions/runs/27052863313) |
| 27049781487 | PR | 26.3 min | same (windows) 12.7min | +24.6 min | [run](https://github.com/microsoft/aspire/actions/runs/27049781487) |

**Median PR total ≈ 26.5 min.** Critical path is consistently the same Templates test class on `windows-latest`.

## Critical-path chain (consistent across runs)

```
prepare_for_ci                                    ~0.3 min  t=0.0 → 0.3
  (all in parallel from t≈1.3:)
  ├─ Setup for tests                              ~3.3 min  →  4.6
  ├─ build_packages                               ~3.5 min  →  4.8
  ├─ build_cli_archive_linux (8-core)             ~5.7 min  →  7.0
  ├─ build_cli_archive_linux_arm64                ~6.8 min  →  8.2
  ├─ build_cli_archive_macos (arm64)              ~6.8 min  →  8.1
  ├─ build_cli_archive_windows                    ~9.6 min  → 10.9  ← critical-path predecessor
  ├─ build_cli_archive_windows_arm64              ~12.2 min → 13.5
  ├─ build_cli_archive_macos_x64 (Intel)          ~17.4 min → 18.7  ← longest single job, NOT on critical path
  ├─ stabilization_check (PR only)                ~6.0 min  →  7.3
  ├─ build_cli_e2e_image                          ~7.2 min  → 12.0
  └─ ...

After build_cli_archive_windows finishes (~t=11):
  └─ tests_requires_nugets_windows matrix
       └─ Templates-NewUpAndBuildStandaloneTemplateTests (windows)
                                                  ~13 min  → 24-26  ← GATE
After all leaf tests finish (~t=24-26):
  ├─ Prepare WinGet manifests                     ~2 min   → 16
  ├─ Prepare Homebrew cask                        ~1.5 min → 22
  └─ Final Test Results                           ~1.1 min → 26
       └─ Final Results                                       → 26-28
```

### Counterintuitive observation

The **longest single job** is `build_cli_archive_macos_x64` at 17min — but it
is **not** on the critical path. It only gates `prepare_homebrew_installer_artifacts`,
which finishes at ~+22min, while the actual gate finishes later at +24-26min.
Optimizing macos-x64 doesn't shorten wall time (it does save runner-minutes).

### The Windows-vs-Linux asymmetry

The same Templates test class runs on **both** `ubuntu-latest` and `windows-latest`
(default per `eng/Testing.props`). Windows is consistently **~2x slower**:

| Templates test class | Linux (min) | Windows (min) |
|---|---:|---:|
| NewUpAndBuildStandaloneTemplateTests | 6.7 | 12.7 |
| MSTest_NewUpAndBuildSupportProjectTemplatesTests | 4.8 | 8.4 |
| NUnit_NewUpAndBuildSupportProjectTemplatesTests | 4.8 | 8.3 |
| XUnit_V3MTP_NewUpAndBuildSupportProjectTemplatesTests | 4.3 | 7.7 |
| XUnit_Default_NewUpAndBuildSupportProjectTemplatesTests | 4.6 | 7.1 |

The Linux variants finish at ~+11-15 min; the Windows variants finish at ~+19-25 min.
**The slow Windows variants are what gate the PR.**

(Numbers from run 27049781487. `windows-latest` is a 2-core hosted runner; per-template
`dotnet new` + `dotnet build` is the dominant cost, and is markedly slower there.)

## Hypotheses & iterations

### Iteration 1 — skip Windows for `Aspire.Templates.Tests` on PRs

**Change:** add `<RunOnGithubActionsWindows Condition=" '$(IsGithubPullRequest)' == 'true' ">false</RunOnGithubActionsWindows>` to
`tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj`. Mirrors the existing
`RunOnGithubActionsMacOS` PR-skip pattern in the same file.

**Predicted delta:** removes all Windows Templates jobs from PR matrix. New critical
path becomes the **next-tier** test:

- After Templates Windows is gone, the next-longest jobs by end-time (from
  run 27052863313) are:
  - `Cli.EndToEnd-TypeScriptPolyglotTests` (linux): ~22.3 min end
  - `Cli.EndToEnd-TypeScriptCodegenValidationTests` (linux): ~22.2 min end
  - VS Code extension E2E (windows): ~20.0 min end
  - Templates-MSTest (windows): would have been ~19.6 min end — also gone now
- So predicted new gate ≈ +22.3 min, plus ~1.1 min for `Final Test Results` + final aggregate ≈ **~24 min total**.

**Predicted savings: 26.5 → ~24 min (~2.5 min off median PR wall, ~10%).**
Plus very large runner-minute savings (every Templates Windows job — ~50+
runner-min per PR — is removed).

**Tradeoff:** Windows Templates coverage shifts from "every PR" to "every push to
main" + scheduled runs. This is the same tradeoff already accepted for macOS
Templates testing in the same file. Risk: a Windows-specific template regression
slips into main and is detected on the merge run rather than on the PR.

**Mitigations:**
- The push-to-main `ci.yml` still runs Windows Templates (the condition is
  PR-only).
- A future iteration can add a single sampled Windows Templates job (one test
  class) back to PR as a smoke gate without re-introducing the full cost.

**Verification plan:** push this change → trigger a PR CI run → measure
- Wall-clock total (compare against 26.5 min baseline median).
- New critical-path gate (should shift to a non-Templates job).
- Runner-minute savings on the Windows pool.
Target ≥ 3 runs for statistical confidence per `perf-investigation` skill
methodology.

### Iteration 1 — measurement

PR #17973, run [27055927499](https://github.com/microsoft/aspire/actions/runs/27055927499).

| Run | Total wall | Conclusion | New gate (excl. flake) | Delta vs baseline | Notes |
|---|---:|---|---|---:|---|
| 27055927499 (initial) | 30.7 min | failure | `Cli.EndToEnd-TypeScriptCodegenValidationTests` hung 18.1 min (baseline 9.6 min) | n/a | one flake; not caused by this change |
| 27055927499 (rerun-failed) | _running_ | _tbd_ | _tbd_ | _tbd_ | rerun the 1 flaky test job |

**Measurement modulo the flake:** if the flaky job had completed at its baseline ~9.6 min,
the new gating job would have been `Aspire CLI Starter Validation (Windows ARM64)` at
**+21.1 min**, plus `Final Test Results` (~1.1 min) + `Final Results` (~0.1 min), for a
total wall of **~22.5 min** (vs 26.5 min baseline = **~4 min saved, ~15%**).

**Validation of the change itself:**
- All 12 Templates jobs ran on Linux only (no Windows variants — iter 1 working as
  designed).
- `Templates-NewUpAndBuildStandaloneTemplateTests` on Linux: 7.1 min, ended at +13.4
  (vs the baseline Windows variant at 13.2 min ending at +24.4 — confirms the 2x
  Linux-vs-Windows delta).
- No non-Templates jobs were affected.

**Flake note:** `Cli.EndToEnd-TypeScriptCodegenValidationTests` hung (caught by MTP
`--hangdump-timeout 10m`). No matching open issue in microsoft/aspire. This is the
same class iter 5 plans to split — its size and high fixed-cost setup may contribute
to flakiness. Not caused by iter 1.

### Iteration 2 — decouple `build_cli_e2e_image` from `setup_for_tests`

**Change:** in `.github/workflows/tests.yml`, replace

```yaml
build_cli_e2e_image:
  uses: ./.github/workflows/build-cli-e2e-image.yml
  needs: setup_for_tests
  if: ${{ fromJson(needs.setup_for_tests.outputs.tests_matrix_requires_cli_archive).include[0] != null }}
```

with

```yaml
build_cli_e2e_image:
  uses: ./.github/workflows/build-cli-e2e-image.yml
  if: ${{ github.event_name == 'pull_request' }}
```

**Why it's safe:** `tests_matrix_requires_cli_archive` is non-empty iff
`IncludeCliE2ETests=true`, and `tests.yml`'s `enumerate-tests` action sets
`IncludeCliE2ETests=${{ github.event_name == 'pull_request' }}`. The
event-name gate is **logically equivalent** to the matrix-output gate,
without serializing behind `setup_for_tests` to read its output. The
`results:` job still has `setup_for_tests` in its `needs:`, so the
existing matrix-output check in the `if:` of the final fail-step
continues to work without changes.

**Predicted delta:** `build_cli_e2e_image` currently starts at +4.5 min
(after `setup_for_tests`) and ends at +12 min. With the gate removed it
starts at ~+1.3 min (alongside `build_packages`, archives) and ends at
~+8.5 min. The downstream `tests_requires_cli_archive` lane is gated by
the latest of `setup_for_tests` (+4.5), `build_packages` (+4.8),
`build_cli_archive_linux` (+7.0), `build_cli_e2e_image` (+8.5 after iter 2)
— so its earliest-start drops from +12.6 to +8.5, a **~4 min** shift.

After iter 1 + iter 2, `Cli.EndToEnd-TypeScriptPolyglotTests` would end at
~+18.3 min (down from +22.3 in the baseline). But the **new gate** then
becomes `Prepare Homebrew cask` at **+20.1** (1.3 min after the 17-min
`build_cli_archive_macos_x64` finishes), because the `results:` job
requires `prepare_homebrew_installer_artifacts`. Predicted wall after
iter 1 + iter 2 is therefore **~22 min**, not ~20 min — about
**~4.5 min saved** from the 26.5 baseline (~17%).

To go below ~22 min wall, iteration 3 needs to either skip
`build_cli_archive_macos_x64` on PRs (and its dependent Homebrew prep)
or remove those from the `results:` PR-required set. That is now the
next candidate hypothesis.

### Iteration 3 (queued) — skip `build_cli_archive_macos_x64` and Homebrew prep on PRs

After iter 1 + iter 2 the critical-path gate moves to
`prepare_homebrew_installer_artifacts` at +20.1, which is gated by
`build_cli_archive_macos_x64` (17 min on the `macos-15-intel` runner).
That archive is consumed **only** by the Homebrew universal-cask
preparation (which combines arm64 + x64); no test job needs the
osx-x64 artifact directly.

**Change:** gate `build_cli_archive_macos_x64` and
`prepare_homebrew_installer_artifacts` on `github.event_name != 'pull_request'`,
and remove their `'skipped' == failure` entries from the `results:`
final-fail check's PR list (`tests.yml` lines ~459-460).

**Predicted delta:** removes ~17 min macOS Intel job + 1.3 min Homebrew
prep from the PR critical path. New gate becomes
`Cli.EndToEnd-TypeScriptPolyglotTests` at +18.3 (after iter 2), giving
total wall **~20 min** (savings ~6.5 min from baseline, ~25%).

**Tradeoff:** Intel-Mac Homebrew validation moves from "every PR" to
"push to main". Same shape as the macOS Templates skip already in the
codebase. Apple Intel hardware is also being phased out, so the
end-user impact is bounded.

### Iteration 4 (on deck) — skip `build_cli_archive_windows_arm64` and WinGet prep on PRs

Symmetric to iter 3: `build_cli_archive_windows_arm64` (12.2 min) gates
`prepare_winget_installer_artifacts` (2 min), `cli_starter_validation_windows`
(Windows ARM64 instance, 5.8 min), and feeds the `results:` gate. After
iter 3, the next bottleneck is the win-arm64 chain (Starter Validation
Windows ARM64 ends at +19.3, WinGet prep ends at +16.2).

**Caveat:** Windows ARM64 starter validation is a real PR check that
exercises the CLI on Windows ARM. Skipping it on PRs is more visible
than skipping installer prep. Needs more discussion before applying.

### Iteration 5 (on deck) — split `Cli.EndToEnd-TypeScript*` test classes

If after iter 3 the new gate is back to `Cli.EndToEnd-TypeScript*`
(~9-10 min each on ubuntu-latest):

- `TypeScriptPolyglotTests`: 10 cases across 4 methods (two `[Theory]`
  with `SupportedToolchains = {npm, bun, yarn, pnpm}` × 4 each, plus
  two `[Fact]`s). Splittable into 2-4 classes / partitions.
- `TypeScriptCodegenValidationTests`: 6 cases across 3 methods (one
  `[Theory]` × 4, plus two `[Fact]`s). Splittable into 2 classes
  (restore-heavy vs `aspire start` runtime).

**Caveat:** both classes have high per-case fixed cost (Docker terminal
prep, CLI install, channel prep, `aspire init`, npm/vite install).
Splitting pays that fixed cost more times. A 2-way split is likely the
sweet spot; predicted savings **2-4 min** per class family.

This iteration touches C# test code (not just config) — slightly higher
risk than iter 1 / iter 2 / iter 3.

## Hypotheses on deck (for future iterations)

| # | Step | Change | Predicted delta |
|---|---|---|---:|
| H2 | `build_cli_archive_windows` | Larger Windows runner (if org has 4/8-core windows pool) | -2 to -4 min |
| H3 | `build_cli_archive_macos_x64` | Skip on PRs (Homebrew installer prep skipped too) | 0 min wall, ~17 min runner savings |
| H4 | `build_cli_archive_windows_arm64` | Skip on PRs (WinGet installer prep skipped too) | 0 min wall, ~12 min runner savings |
| H5 | `Cli.EndToEnd-TypeScript*` tests | Investigate ~9-10 min duration; possibly split or remove from PR | -2 to -3 min if they become the new gate |
| H6 | NuGet restore cache (`actions/cache`) on `tests_*` jobs | Save 30-60s/job × many jobs | <1 min wall (parallel), large runner savings |

## How to read this doc

Each iteration:
1. States the hypothesis with predicted delta.
2. Identifies how to verify.
3. After the change is pushed and CI runs, fills in actual measurements.
4. Decides: kept / reverted / inconclusive — and queues the next hypothesis.

## Related docs

- [ci-pipeline-optimizations.md](ci-pipeline-optimizations.md) — existing
  rationale for the current parallel-job structure.
- [ci-trigger-patterns.md](ci-trigger-patterns.md) — skip-CI patterns for
  doc-only / non-build PRs.
- [TestingOnCI.md](TestingOnCI.md) — how test partitioning and matrix
  generation work.
- [azdo-public-pipeline.md](azdo-public-pipeline.md) — the AzDO pipeline that
  runs weekly (not on PRs); don't add to GH Actions what AzDO already covers.

## Related PRs

- **microsoft/aspire#17760** — `perf(ci): cut microsoft-aspire pipeline
  wall-clock from 121min to ~57min` — parallel work on the **AzDO internal
  pipeline** (`eng/pipelines/azure-pipelines*.yml`). Same methodology
  (break unnecessary `dependsOn`, start parallel stages with `dependsOn: []`,
  pull installer prep out from behind assemble), but on different YAML files
  (`eng/pipelines/*` vs this PR's `.github/workflows/*`). Orthogonal — no
  merge overlap.
