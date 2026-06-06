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

### Iteration 1 — measurement (pending)

| Run | Total wall | New gate | Delta vs baseline | Link |
|---|---:|---|---:|---|
| _pending_ | | | | |

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
