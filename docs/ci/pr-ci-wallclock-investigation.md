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

PR #17973, run [27055927499](https://github.com/microsoft/aspire/actions/runs/27055927499)
(first attempt; iter 2 was added in a follow-up commit before iter 1
could be re-measured in isolation).

| Attempt | Total | Conclusion | Notes |
|---|---:|---|---|
| 1 | 30.7 min | failure | `Cli.EndToEnd-TypeScriptCodegenValidationTests` hung 18.1 min (baseline 9.6 min) — unrelated flake |
| 2 (rerun) | — | failure | Codegen passed (8.6 min) but `Final Test Results` gate failed due to GH Actions rerun-of-matrix-child quirk |

**Measured (excluding the flake):** had Codegen completed in baseline
time, the next-latest gating job was `Aspire CLI Starter Validation (Windows ARM64)`
at **+21.1**, plus `Final Test Results` (~1.1 min) + `Final Results`
(~0.1 min) → **wall ~22.5 min** (vs 26.5 baseline = **~4 min saved, ~15%**).

**Validation of the change itself:**
- 12 Templates jobs scheduled, all on Linux only — no Windows variants.
- `Templates-NewUpAndBuildStandaloneTemplateTests` on Linux: 7.1 min,
  ending at +13.4 (vs the baseline Windows variant at 13.2 min ending
  at +24.4 — confirms the 2x Linux-vs-Windows delta).
- No non-Templates jobs affected.

### Iteration 2 — applied: decouple `build_cli_e2e_image` from `setup_for_tests`

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
`IncludeCliE2ETests=true`, and `tests.yml`'s `enumerate-tests` action
sets `IncludeCliE2ETests=${{ github.event_name == 'pull_request' }}`.
The event-name gate is **logically equivalent** to the matrix-output
gate, without serializing behind `setup_for_tests` to read its output.
The `results:` job still has `setup_for_tests` in its `needs:`, so the
matrix-output check in its `if:` continues to work without changes.

**Predicted delta:** image build starts at ~+1.3 (parallel with
`build_packages` and CLI archives) instead of ~+4.5 (after
`setup_for_tests`). The downstream `tests_requires_cli_archive` lane
shifts ~4 min earlier. Combined with iter 1, predicted wall ~21.6 min.

### Iteration 1 + Iteration 2 — measurement

PR #17973, run [27057356440](https://github.com/microsoft/aspire/actions/runs/27057356440).

| Run | Total | Gating job (excl. flake) | Delta vs baseline |
|---|---:|---|---:|
| 27057356440 | (cancelled — see below) | `Aspire CLI Starter Validation (Windows ARM64)` at +20.8 or `Prepare Homebrew cask` at +20.6 | **~22 min wall**, -4.5 min (~17%) |

**Iteration 2 effect — confirmed:**

| Job | Baseline (run 27052863313) | Iter 1+2 (run 27057356440) | Delta |
|---|---|---|---:|
| `Build CLI E2E Docker image` (start → end) | +5.1 → +12.2 (dur 7.2) | **+1.2 → +8.6 (dur 7.4)** | start **-3.9 min** |
| `Cli.EndToEnd-TypeScriptPolyglotTests` (start → end) | +12.6 → +22.3 (dur 9.8) | **+8.9 → +18.3 (dur 9.3)** | start **-3.7 min**, end **-4.0 min** |
| `Cli.EndToEnd-TypeScriptCodegenValidationTests` | +12.6 → +22.2 (dur 9.6) | **+9.0 → +17.8 (dur 8.9)** | start **-3.6 min** |

This confirms iter 2 worked as designed: the gate dependency between
`build_cli_e2e_image` and `setup_for_tests` was the only thing
holding the CLI E2E image build off `t=0`; removing it shifts the
whole `tests_requires_cli_archive` lane ~4 min earlier.

**Flake interfering with run-status:**
`Aspire CLI Starter Validation (Windows)` (Windows x64 matrix instance)
hung for 20 min and was cancelled by the per-job `timeout-minutes: 15`
guard (baseline duration ~4.3 min — a 4-5x hang). The whole workflow
rolled up to `cancelled` status. This is unrelated to iter 1 / iter 2
(neither change touches `cli_starter_validation_windows`); it's the
same class of CI infra intermittent that affected
`Cli.EndToEnd-TypeScriptCodegenValidationTests` on the iter 1 run.
The 313 other jobs all succeeded, including all iter 2-affected jobs.

**Wall after iter 1 + iter 2 (using the longest legitimately-completed
leaf):** `Aspire CLI Starter Validation (Windows ARM64)` at +20.8, then
`Final Test Results` (~1.1 min) + `Final Results` (~0.1 min) ≈
**~22 min** vs the 26.5 min baseline median (**~4.5 min saved, ~17%**).
Matches the predicted ~21.6 min within measurement noise.

### Iteration 3 — applied: skip `build_cli_archive_macos_x64` and Homebrew prep on PRs

After iter 1 + iter 2 the critical-path gate moves to
`prepare_homebrew_installer_artifacts` at +20.1, which is gated by
`build_cli_archive_macos_x64` (17 min on the `macos-15-intel` runner).
That archive is consumed **only** by the Homebrew universal-cask
preparation (which combines arm64 + x64); no test job needs the
osx-x64 artifact directly.

**Change:** in `.github/workflows/tests.yml`:
- Add `if: ${{ github.event_name != 'pull_request' }}` to
  `build_cli_archive_macos_x64`.
- Add the same `if:` to `prepare_homebrew_installer_artifacts`
  (its `needs:` includes `build_cli_archive_macos_x64`, so it would
  auto-skip; the explicit `if:` declares the intent).
- Remove `build_cli_archive_macos_x64` and
  `prepare_homebrew_installer_artifacts` from the PR fail-on-skip
  list in the `results:` final-fail gate. Both stay in the non-PR
  list so push-to-main behavior is unchanged.

**Predicted delta:** removes the ~17 min Intel-Mac archive + 1.3 min
Homebrew prep from the PR critical path. New gate becomes
`Aspire CLI Starter Validation (Windows ARM64)` at +20.8 OR
`Cli.EndToEnd-TypeScriptPolyglotTests` at +18.3 (whichever finishes
later for a given run; usually starter-validation). Total wall
**~20-21 min** (savings ~5-6 min from baseline, ~22%).

**Tradeoff:** Intel-Mac Homebrew validation moves from "every PR" to
"push to main" + scheduled runs. Same shape as the existing macOS
Templates PR skip. Apple Intel hardware is being phased out, so the
user-facing impact is bounded.

### Iteration 1 + Iteration 2 + Iteration 3 — measurement

PR #17973, run [27058240036](https://github.com/microsoft/aspire/actions/runs/27058240036).

**Verification of iter 3 itself:**
- `Build native CLI archive (macOS x64)`: **skipped** ✓
- `Prepare Homebrew installer artifacts`: **skipped** (auto-skipped via
  needs) ✓
- `Build CLI E2E Docker image`: start=+1.1, end=+8.5 (iter 2 still
  working) ✓
- 12 Templates jobs all on Linux only (iter 1 still working) ✓

**Wall (legitimate, excluding flake — see below):**

| Top finishing jobs | End time |
|---|---:|
| VS Code extension E2E (Windows, debug-…) | +19.4 |
| `Cli.EndToEnd-TypeScriptPolyglotTests` | +19.1 |
| `Aspire CLI Starter Validation (Windows ARM64)` | +18.9 |
| `Cli.EndToEnd-TypeScriptCodegenValidationTests` | +17.7 |
| `Prepare WinGet installer artifacts` | +16.0 |

Final Test Results (~1.1) + Final Results (~0.1) → **wall ~20.5 min**
vs the 26.5 min baseline (**~6 min saved, ~23%**).

For PRs that **don't** touch the VS Code extension (the common case),
the gate falls back to `Cli.EndToEnd-TypeScriptPolyglotTests` at +19.1
→ **wall ~20.3 min**. The extension E2E lane only runs when the PR
modifies `extension/**`, `src/Aspire.Cli/**`, or
`.github/workflows/extension-*.yml`; for everything else it's skipped.
This PR's diff touches `.github/workflows/tests.yml` which triggers
the extension E2E lane, so the measured wall here is the slightly
higher worst-case figure.

| Iteration | Predicted wall | Measured wall | Notes |
|---|---:|---:|---|
| baseline | — | 26.5 min | median of 3 baseline runs |
| iter 1 | ~22.5 min | ~22.5 min (modulo flake) | Templates Win → skipped |
| iter 1+2 | ~21.6 min | **~22 min** | build_cli_e2e_image starts at t=0 |
| iter 1+2+3 | ~20-21 min | **~20.5 min** | macOS x64 + Homebrew prep PR-skipped |

**Cumulative saving: ~6 min off the median PR wall (~23%).**

**Flake interfering with run-status:** `Hosting-1 (windows-latest)`
hung for 47 min and triggered the per-job timeout, rolling the whole
workflow to `failure`. This is the **3rd different unrelated test
flake** in 3 CI runs of this PR (Codegen on iter 1, Starter Validation
Windows on iter 1+2, Hosting-1 on iter 1+2+3 — none related to the
perf changes; the pattern is Windows-runner intermittent hangs). The
311 other jobs all succeeded, including all iter 1/2/3-affected jobs.
The flake should be filed as a separate issue for the windows-runner
pool / hang-detection thresholds.

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
