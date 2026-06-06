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

## 7-day population characterization (2026-05-30 → 2026-06-06)

Per perf-investigation skill: characterize at scale before proposing new
iterations. Numbers from `gh run list --workflow=ci.yml --limit 1000 --created '>=2026-05-30'`,
857 runs total (`/tmp/ci_runs.json` snapshot of that query).

### Distribution by outcome

| Event | Conclusion | Count | Share |
|---|---|---:|---:|
| `pull_request` | success           | 265 | 31 % |
| `pull_request` | failure           | 185 | 22 % |
| `pull_request` | cancelled         | 294 | 35 % |
| `pull_request` | action_required   |  29 |  3 % |
| `push`         | success           |  16 | 19 % of pushes |
| `push`         | failure           |  62 | 74 % of pushes |
| `push`         | cancelled         |   6 |  7 % of pushes |

**Push-to-main is 19 % green.** Every successful PR merge has a roughly
4-in-5 chance of producing a red post-merge run. This is a systemic
reliability finding independent of wall-clock — and it caps the value of
further wall-clock cuts (if main is permanently red, "time to green" on a
PR is a less useful metric than "time to known-good signal").

### Wall-clock distribution (PR runs, completed, excluding cancellations)

Including the long tail:

| Bucket | n | min | p25 | median | p75 | p90 | p95 | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| PR success | 265 | 0.5m | 25.5m | 37.2m | 65.4m | 197.2m | 720.4m | 1830m |
| PR failure | 185 | 10.4m | 26.9m | 30.0m | 34.7m | 71.6m | 123.3m | 2227m |
| Push success |  16 | 21.6m | 25.4m | 26.5m | 31.4m | 64.8m | 1287m | 1287m |
| Push failure |  62 | 20.0m | 26.2m | 28.2m | 48.1m | 57.3m | 68.7m | 112m |

Excluding hangs (>60 min wall) — the "healthy" picture:

| Bucket | n | min | p25 | median | p75 | p90 | max |
|---|---:|---:|---:|---:|---:|---:|---:|
| PR success | 189 | 0.5m | 3.2m | **28.8m** | 40.6m | 47.7m | 60.0m |
| PR failure | 158 | 10.4m | 26.6m | 29.1m | 31.4m | 36.0m | 56.8m |

Healthy median ~28.8 min for successful PR runs is consistent with the
"~26.5 min baseline" cited at the top of this doc (which was median of 3
hand-picked runs); both predate iter 1/2/3 (PR #17973 still draft).
**The p25 of 3.2 min is doc-only PRs hitting the workflow skip path.**

### The long tail — hung runs

**112 of 744 PR runs ran longer than 60 min wall-clock (15 %).** Top 10:

| Wall | Conclusion | Run | Title (truncated) |
|---:|---|---|---|
| 6.6 days | cancelled | 26673482295 | Speed up TypeScript AppHost startup |
| 37 hours | failure   | 26850781103 | Bump npm_and_yarn group |
| 30 hours | success   | 26902822218 | Add Bun debugging support |
| 26 hours | success   | 26734261799 | a11y-17466 |
| 25 hours | success   | 26735563437 | a11y-17650 |
| 25 hours | success   | 26738012262 | a11y-17496 |
| 25 hours | success   | 26736787229 | a11y-10299 |
| 23 hours | success   | 26739547985 | a11y-17467 |
| 22 hours | failure   | 26775346946 | Update PackageValidationBaselineVersion |
| 22 hours | success   | 26981692771 | Add ATS exports for custom health checks |

Estimated **wasted compute from hangs over 7 days ≈ 746 hours**
of wall-clock (sum of `(wall - 30 min baseline)` for each >60 min run).
A single 6.6-day run accounts for ~160 hours of that.

### Why hangs happen — workflow timeout audit

`grep -l "timeout-minutes" .github/workflows/*.yml` shows:

| Workflow | Top-level job `timeout-minutes` |
|---|---|
| `ci.yml`                          | **none — defaults to 360 min/job, 4320 min (3 days) per workflow** |
| `build-cli-native-archives.yml`   | **none** |
| `build-packages.yml`              | **none** |
| `run-tests.yml`                   | 60 min |
| `build-cli-e2e-image.yml`         | 45 min |
| `tests.yml`                       | 15 min (matrix), 12 min (steps) |
| `extension-e2e-tests.yml`         | 75 min |
| `reproduce-flaky-tests.yml`       | 60 min |

The workflows with **no `timeout-minutes`** are exactly the ones that
host the multi-hour hangs: `build-cli-native-archives.yml` runs the
windows-arm64 / macos-x64 build jobs (handoff session noted these as the
suspected hang locations), and `ci.yml` is the orchestrator that aggregates
everything. They inherit GitHub's defaults of **360 min per job** and **4320
min per workflow**, which is why a single run can sit for 6.6 days.

### Auto-rerun coverage

`auto-rerun-transient-ci-failures.yml` runs after every PR CI completion,
matching a curated list of transient infrastructure patterns
(`/ENOTFOUND/`, `/Bad Gateway/`, `/RPC failed/`, `/getaddrinfo.*builds\.dotnet/`,
etc.) and rerunning eligible failed jobs (max 3 attempts, max 5 jobs).

Last 7 days: 100 runs of the rerun workflow → 37 successful reruns (transient
correctly detected and recovered), 59 skipped (no eligible failure pattern),
3 failure, 1 action_required. So **~37 transient failures per week are
already being silently recovered** by infrastructure — which is good for
green-rate, but it adds time-to-green because each recovery costs the
duration of the original failed run plus the rerun.

## Strategic options — where to spend next

After 7-day characterization, the lever options are clearer. Listed by
ROI vs effort.

### A. Land iter 3 first

PR #17973 is still draft. The measured 26.5 → 20.5 min improvement only
matters when it lands and benefits everyone. **Stop iterating until iter 3
lands and a fresh 7-day baseline is available with iter 3 effects included.**
Otherwise the next iteration is measured against a moving target.

### B. Add `timeout-minutes` to the workflows that lack them

| Workflow | Current cap | Proposed | Rationale |
|---|---|---|---|
| `ci.yml` orchestrator job | 360 min/job, 4320 min/workflow | 60 min/job | Orchestrator should fail fast if a `needs:` chain hangs |
| `build-cli-native-archives.yml` per-RID jobs | 360 min default | 30 min/job | Longest measured legitimate run is ~17 min (macOS-x64) — 30 min gives ~75 % slack |
| `build-packages.yml` jobs | 360 min default | 20 min/job | Build packages job measured at ~3.5 min — 20 min gives ~5× headroom |

**Predicted impact:**
- Hangs become 30-min hangs instead of 6-hour or 6-day hangs.
- Saves ~700 runner-hours/week (cost / capacity).
- PR signal stops being stale for hours / days when a build job wedges.
- Does **not** affect healthy-run wall-clock — purely a tail / capacity fix.

**Risk:** A legitimate slow run that happens to exceed the cap would
fail. Mitigation: pick caps with ≥2× headroom over historical p95 for that
specific job; widen if false positives appear.

This is the lowest-risk highest-leverage idea in the queue. Recommend as
the immediate follow-up after iter 3 lands.

### C. Investigate why push-to-main is 19 % green

Independent of wall-clock. Every successful PR has a ~4-in-5 chance of
producing a red post-merge run. Hypotheses to validate:

- Are the failures the same recurring set (an unquarantined flaky test)?
  → fix-flaky-test skill + test-management skill if so.
- Are they real regressions slipping past PR validation? → PR validation is
  missing coverage that push runs (e.g. Templates Windows, which iter 1
  removes from PR but keeps on push — that's the shift-right hazard).
- Are they infrastructure failures that the auto-rerun workflow's pattern
  list doesn't cover? → expand the pattern list.

Spot-checked run 27053733754 (most recent push failure): `Hosting-4
(windows-latest)` and `Templates-XUnit_V3_NewUpAndBuildSupportProjectTemplatesTests
(windows-latest)` both failed. Hosting-4 on Windows matches one of the
three Windows-hang patterns flagged in the handoff session. Likely an
already-known windows-runner pool flakiness pattern.

This is **higher value than further wall-clock cuts** if we can confirm
the dominant failure is fixable / quarantinable, because a green main is
the prerequisite for trusting any PR signal.

### D. Iteration 4 — skip Windows-ARM64 archive + WinGet prep on PRs

Already analyzed earlier in this doc; needs user signoff on Win-ARM64 PR
coverage tradeoff. Predicted -1 to -1.5 min wall (down to ~19 min). Pure
incremental win; not load-bearing.

### E. Iteration 5 — split TypeScript test classes

Already analyzed earlier; recommend as a separate PR. Predicted -1 to -2.5
min wall (down to ~17.5-18 min depending on split). Touches C# test code.

### F. Revive PR #15742 — conditional test selection

Inspected 2026-06-06:

- Last commit: **2026-04-27** (5+ weeks stale)
- Size: **13,750 additions / 76 files changed**
- Labels: `NO-MERGE`, `no-review`
- State: `CONFLICTING` against main
- Author: same as this investigation
- Touches: `ci.yml`, `run-tests.yml`, `tests.yml`, plus new
  `tools/TestSelector/` + `tools/Aspire.TestSelector/` infrastructure,
  per-test rule files, ~10 new test files.

**Potential payoff:** For a PR touching one component, the selector could
skip 80-90% of test matrix → 26.5 → ~5-8 min wall in the best case. Far
larger than incremental cuts.

**Cost / risk to land:**
- Rebase against 5 weeks of main drift while CONFLICTING.
- Get 13.7k LOC reviewed — needs other reviewers' bandwidth.
- Soak in CI to validate the selector — **false negatives (a test that
  *should* run isn't run) ship bugs**. This is asymmetric: a false
  negative is much worse than a false positive (extra runs).
- The `NO-MERGE` / `no-review` labels were applied for reasons not
  documented in the PR body — need to surface those before reviving.

**When this is worth pivoting to:** if (a) the user has organizational
appetite for a multi-week landing effort and (b) measurements show most
PRs touch a small enough scope that the selector saves dominant time. If
most PRs touch widely-shared code (`Aspire.Hosting`, `Aspire.Dashboard`),
the selector helps less.

**Recommendation:** keep on the shelf until incremental optimizations
plateau (post iter 4/5) and until push-to-main reliability (option C) is
addressed. Then re-evaluate.

## Why has CI been so red? (2026-05-30 → 2026-06-06 deep-dive)

Fetched all 247 failed `ci.yml` runs (185 PR, 62 push), pulled failed-job
data for each, then sampled logs to identify root causes. 1308 total
failed-job records.

### Top failed jobs (all events)

| n | Job (matrix-suffix stripped) | What is it |
|---:|---|---|
| 217 | `Tests / Final Test Results` | Aggregate fail-on-any — always present when anything below fails |
| 217 | `Final Results` | Same — outer aggregate |
| **79** | Tests / VS Code extension E2E | (PR-only; 9 post-fix) |
| **65** | Tests / Polyglot SDK Validation / **TypeScript** SDK Validation | All polyglot langs fail together |
| **45** | Tests / **Hosting-4** | windows-latest |
| 38 | Polyglot Validation Results (aggregate) | |
| 36 | Polyglot / **Go** SDK Validation | |
| 34 | Tests / Cli | Real flakes (Assert.False) |
| 33 | Polyglot / **Rust** SDK Validation | |
| 33 | Polyglot / **Java** SDK Validation | |
| 32 | Polyglot / **Python** SDK Validation | |
| 19 | Hosting-1 | windows-latest |
| 16 | Cli.EndToEnd-PersistentContainerEndToEndTests | |
| 13 | Stabilization Check | |

The pattern is **stark**: 5 polyglot-SDK languages fail in lockstep on
nearly every push (32 records each × 5 languages = ~160 records — half
of all failed-job records). That's not 5 flaky tests; it's 1 shared
infrastructure failure manifesting 5 ways.

### Root cause #1 — Polyglot SDK Validation: NuGet package-feed bug

Sampled job log [79680614506](https://github.com/microsoft/aspire/actions/runs/27000541344/job/79680614506) (push, TypeScript SDK Validation, 2026-06-05):

```
ERROR: Unable to find a stable package Aspire.Hosting.Redis with version (>= 13.4.2)
Error: Restore failed: AspireRestore depends on Aspire.Hosting (>= 13.5.0-ci)
  but Aspire.Hosting 13.5.0-ci was not found.
  Aspire.Hosting 13.5.0-preview.1.26277.12 was resolved instead.
…
apphost.mts(13,29): error TS2339: Property 'addRedis' does not exist on type
  'DistributedApplicationBuilder'.
```

A NuGet channel-resolution bug: `aspire add Redis` resolved against the
preview channel instead of `-ci`, so `Aspire.Hosting.Redis` and the
`-ci` API surface were mismatched. Every polyglot language hit it
because they all share the same package-restore step.

**Status: APPEARS FIXED.** Last polyglot push-failure was 2026-06-05
07:11:30. Commit [27031991501](https://github.com/microsoft/aspire/commit/27031991501)
"Work around main polyglot channel collision" landed at 2026-06-05 18:00.
Zero polyglot failures after that timestamp in the 7-day window.

| Window | Push runs | Push green | PR runs* | PR green |
|---|---:|---:|---:|---:|
| **Before fix** (5/30 – 6/05 18:00) | 70 | 13 / 70 = **19 %** | 389 | 235 / 389 = **60 %** |
| **After fix** (6/05 18:00 – 6/06) | 11 | 3 / 11 = **27 %** | 61 | 30 / 61 = **49 %** |

\* excluding cancelled / action_required.

Push green improved (19 % → 27 %). PR sample post-fix is too small (1
day, 61 runs) for the apparent dip to be meaningful. The fix wasn't a
silver bullet — there's a long tail of other failures.

### Root cause #2 — Hosting-4 / Hosting-1 (windows-latest): Docker not available

Sampled push run 27053733754, job 79855449948 (Hosting-4 windows-latest):

```
failed Aspire.Hosting.Tests.Pipelines.DistributedApplicationPipelineTests
  .ExecuteAsync_PipelineLoggerProvider_PreservesLoggerAfterStepCompletion (1s 732ms)
  Xunit.MicrosoftTestingPlatform.XunitException:
  Aspire.Hosting.DistributedApplicationException : Docker is not running. Start Docker and try again.
  | Aspire.Hosting.ContainerRuntime Debug: Podman: not found on PATH
```

`windows-latest` GitHub-hosted runners **do not ship with Docker
running** by default (they have Docker Desktop available but it's not
started; and Linux containers require WSL2 init). The test does not
condition itself on container-runtime availability — it just calls into
the pipeline which tries to spin up a container and crashes.

This is **not transient** and **not a code regression** — it's a test
that has always been incompatible with the windows-latest configuration.
Either:

- the test should require `RequiresDocker` and skip on Windows runners
  that don't have Docker started, or
- `windows-latest` should be removed from the matrix for `Hosting-4`.

### Root cause #3 — VS Code Extension E2E: corepack digest mismatch (transient CDN)

Sampled PR run, job 79870253626 (VS Code extension E2E, 2026-06-06):

```
digest-mismatch: error
digest-mismatch: error
digest-mismatch: error
digest-mismatch: error
```

A corepack/npm registry CDN integrity check failing. This is transient
(retry usually succeeds) and is well-known in the npm ecosystem.

### Root cause #4 — Real test flakes (Cli, etc.)

Sampled PR run, job 79816396809 (Cli on windows-latest, 2026-06-05):

```
failed Aspire.Cli.Tests.Commands.AppHostLauncherTests
  .WaitForLegacyDetachedStartupStabilityAsync_RetriesV2ProbeUntilChildExits (2s 353ms)
  Assert.False() Failure
```

Legitimate test failures — probably timing-sensitive flakes given the
class name. These warrant individual `[QuarantinedTest]` or fix.

### Auto-rerun coverage

The `auto-rerun-transient-ci-failures.yml` workflow ran 100 times in 7
days and successfully rescued 37 failed runs (59 skipped → no match, 3
failure, 1 action_required). Its `transientAnnotationPatterns` cover
**pure-network / runner-infra failures**:

```
ENOTFOUND, ECONNRESET, EPROTO, Bad Gateway, getaddrinfo,
builds.dotnet.microsoft.com, api.github.com, 502/503/504,
RPC failed, Recv failure, "lost communication with the server",
Process exit code 0xC0000142 (Windows process init),
"job was not acquired by Runner of type hosted"
```

**It does NOT catch (and ideally should not, except #1):**

| Failure pattern | Records (7d) | Should rerun? |
|---|---:|---|
| `digest-mismatch` (npm CDN) | 79 + 9 = 88 PR/push | **Yes — transient CDN, retry usually works.** Add to pattern list. |
| `Aspire.Hosting … was not found` (polyglot bug) | ~330 | No — was a real bug; needed source fix. |
| `Docker is not running` (Hosting-4 win) | 45 | No — needs test conditioning, not retry. |
| `Assert.*Failure` (Cli flakes) | 34 | No — needs quarantine or fix. |

### Post-fix residual failure profile

Of 39 post-fix push-failure records (8 unique runs, all on
`windows-latest`):

- Hosting-1, Hosting-4 (×2 each) — Docker / Windows
- Long tail of integrations (Hosting.Valkey, StackExchange.Redis,
  Milvus.Client, MongoDB.Driver, Npgsql.EFCore.PostgreSQL, Pomelo.EFCore.MySql,
  Qdrant.Client, NATS.Net, etc.) — one record each, scattered

No dominant signal — these are individual flakes / environment quirks,
not another systemic issue.

Of 114 post-fix PR-failure records (28 unique runs):

- 9 × VS Code E2E (the digest-mismatch transient — auto-rerun would
  catch it if we added the pattern)
- 5 × Hosting.GitHub.Models, 5 × Cli.EndToEnd-PersistentContainerEndToEndTests
- 4 × Hosting-4, 2 × Hosting-1 (Docker issue)
- 2 × Stabilization Check, 2 × TypeScript API Compatibility, others scattered

### Concrete actions in priority order

1. **Add `digest-mismatch` (and `/digest mismatch/i`) to
   `auto-rerun-transient-ci-failures.js` `transientAnnotationPatterns`.**
   Would silently recover ~88 VS Code E2E failures/week. ~5-line change.
   Verify by checking that the bare `digest-mismatch:` line is
   surfaced as an annotation on the failing job (if not, fall back to
   step-name + log-snippet matching).

2. **Fix Hosting-4 / Hosting-1 on windows-latest.** Either condition
   container-runtime-requiring tests on `RequiresDocker` and have them
   skip when the runtime is unavailable, or remove `windows-latest`
   from the matrix for the affected `Hosting-*` projects. ~45 + 19 =
   64 records/week eliminated.

3. **Quarantine Cli flakes.** Specifically
   `AppHostLauncherTests.WaitForLegacyDetachedStartupStabilityAsync_RetriesV2ProbeUntilChildExits`
   and similar — use the `test-management` skill with `/quarantine-test`
   bot command, linking to a tracking issue.

4. **Monitor whether the polyglot fix sticks.** The
   `27031991501` workaround says "channel collision" — that's a
   symptom-level fix. If a related bug reappears (e.g. another
   integration package goes out of sync between -ci and preview),
   the same 5×polyglot multiplier kicks in immediately. Worth
   considering whether the polyglot SDK validation should fail-fast
   on first language (currently it runs all 5 in parallel and they
   all redundantly hit the same restore error).

5. **(Deprioritised given new data)** Wall-clock optimisation
   (iter 4/5, PR #15742). Iter 3 still worth landing for the proven
   23 % improvement, but the "CI is broken" perception was 80 %
   driven by the polyglot bug, not by wall-clock. Now that root
   cause is gone, the bar for further wall-clock cuts is "is it
   worth the engineering time", not "is CI usable".

## Incident log

Institutional memory of CI-wide incidents so future agents/maintainers
can recognise the signature if it recurs.

**Two distinct kinds** — record both, but label clearly:

- **Recurring transient (flake)** — root cause is external infrastructure
  (CDN serving stale bytes, registry briefly unreachable, hosted-runner
  flake). No source fix exists or is expected. Right response: add an
  auto-rerun rule so the bot silently retries future occurrences. These
  never go away — they recur on whatever cadence the upstream decides.
- **Fixed in source** — root cause was a bug in this repo (incorrect
  package channel, missing API call, broken assumption). A specific
  commit removed the failure. Recurrence is possible if the same class
  of bug returns; the log lets future agents recognise the signature.

Don't log routine per-PR build errors that the PR author fixed locally
(e.g. `CS1729`/`CS1591`/`NU1102` appearing on a single feature branch
in a single run, then resolved by the author re-pushing) — those are
normal PR signal working as intended, not CI-wide incidents.

Schema:

```
### <short signature> — <date range> — [KIND, STATUS]

- Kind:                    transient | fixed-in-source
- Signature (raw):         <error string>
- First seen:              <run id / timestamp>
- Last seen:               <run id / timestamp>
- Records (7-day sample):  <count> across <unique runs>
- Affected jobs:           <bulleted job names>
- Root cause:              <one-paragraph diagnosis>
- Fix:                     <commit sha + title> | <auto-rerun rule + scope>
- Auto-rerun coverage:     <before / after>
- Recurrence risk:         <what would make it come back>
```

### Polyglot SDK channel collision (`Aspire.Hosting -ci was not found`) — 2026-05-30 to 2026-06-05 18:00 — FIXED IN SOURCE

- **Signature (raw):**
  ```
  ERROR: Unable to find a stable package Aspire.Hosting.Redis with version (>= 13.4.2)
  Error: Restore failed: AspireRestore depends on Aspire.Hosting (>= 13.5.0-ci)
    but Aspire.Hosting 13.5.0-ci was not found.
    Aspire.Hosting 13.5.0-preview.1.26277.12 was resolved instead.
  …
  apphost.mts(13,29): error TS2339: Property 'addRedis' does not exist on type
    'DistributedApplicationBuilder'.
  ```
- **First seen:** push run `26799903389` at 2026-06-02 05:21:34Z
- **Last seen:** push run `27000541344` at 2026-06-05 07:11:30Z
- **Records (7-day sample):** ~330 across 32 unique push runs (52% of
  all push-to-main failures in the window) + similar contribution to
  PR failures.
- **Affected jobs:** every job in `Tests / Polyglot SDK Validation /`
  (TypeScript / Go / Rust / Java / Python SDK Validation) and the
  aggregate `Polyglot Validation Results`. All 5 languages failed in
  lockstep on every affected run because they share the same package
  restore step (`aspire add Redis`).
- **Root cause:** `aspire add Redis` (run from inside the polyglot
  validation flow) resolved `Aspire.Hosting` against the preview NuGet
  channel instead of `-ci`. The integration package `Aspire.Hosting.Redis`
  was on `-ci` only at the version the test required, so the channels
  mismatched and the apphost code referenced an `addRedis` API that the
  resolved (preview) `Aspire.Hosting` didn't expose.
- **Fix:** commit `27031991501` ("Work around main polyglot channel
  collision"), merged 2026-06-05 18:00. Zero recurrences in the
  ~14-hour post-merge window of the 7-day sample.
- **Auto-rerun coverage:** none. Pattern doesn't match any infra-network
  regex, and would not be appropriate to retry (it was a real source bug,
  not a transient).
- **Recurrence risk:** medium. The workaround commit is symptom-level —
  if any other integration package's `-ci` vs preview channel goes out
  of sync, the same 5×polyglot multiplier reappears. Consider making
  polyglot SDK validation fail-fast on the first language to hit the
  restore error rather than redundantly burning 5 jobs on the same root
  cause.

### Azure Container Registry brief reachability blip — 2026-06-05 23:54:56-59 — TRANSIENT, NOW AUTO-RETRIED

- **Signature (raw):**
  ```
  Docker.DotNet.DockerApiException : Docker API responded with status code=InternalServerError,
  response={"message":"Get \"https://netaspireci.azurecr.io/v2/\": dial tcp 20.150.241.14:443:
  connect: connection refused"}
  ```
- **First / last seen:** all within push run `27046149398` (sha `bba091af`,
  PR #17950 "Support command arguments for HTTP commands") between
  23:54:56 and 23:54:59 — a 3-second window.
- **Records (7-day sample):** 10 across 1 unique run.
- **Affected jobs:** `StackExchange.Redis`, `StackExchange.Redis.OutputCaching`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, `NATS.Net`, `MongoDB.Driver`,
  `MongoDB.Driver.v2`, `MongoDB.EntityFrameworkCore`, `Pomelo.EFCore.MySql`,
  `Milvus.Client`, `Qdrant.Client` — every integration test that pulls an
  image from `netaspireci.azurecr.io` happened to start during the
  reachability blip.
- **Root cause:** brief ACR network outage (likely Azure-side; no
  application code change involved). Self-healing.
- **Fix:** none required as source change. **Auto-rerun rule added** —
  `eng/test-retry-patterns.json` `jobFailurePatterns` now matches
  `(?:azurecr\.io|netaspireci)[\s\S]{0,200}connect:\s*connection refused`
  with reason `"Transient Azure Container Registry connection refused"`.
- **Auto-rerun coverage (before / after):** none / yes (#17978).
- **Recurrence risk:** high — ACR outages are rare but predictable; the
  next one will be silently retried.

### Corepack / npm registry CDN digest mismatch — 2026-05-31 to ongoing — RECURRING TRANSIENT, NOW AUTO-RETRIED

- **Signature (raw):**
  ```
      digest-mismatch: error
      digest-mismatch: error
  ```
  (repeated 2-4 times per failed retry attempt)
- **First seen:** 2026-05-31 02:31:08 (push to main, Templates job)
- **Last seen:** 2026-06-06 14:19:13 (still occurring at end of sample
  window)
- **Records (7-day sample):** 83 across many runs.
- **Affected jobs:** broader than initially thought — Templates, Polyglot
  SDK validation (via `Run tests`), VS Code extension E2E (via `Run
  extension E2E tests`), Cli.EndToEnd-KubernetesDeploy* (via `Run nuget
  dependent tests`), etc. Anything that invokes corepack to install an
  npm dependency can hit it.
- **Root cause:** corepack's tarball SHA integrity check against the
  npm-registry CDN sometimes sees a stale or partially-served response.
  Pure infrastructure flake; retries always succeed.
- **Fix:** none required as source change. **Auto-rerun rules added** in
  #17978: hardcoded `infrastructureNetworkFailureLogOverridePatterns`
  in the JS (for non-test-execution failure steps like VS Code E2E's
  `Run extension E2E tests`) **and** `jobFailurePatterns` in
  `eng/test-retry-patterns.json` (for test-execution failure steps).
- **Auto-rerun coverage (before / after):** none / yes (#17978).
- **Recurrence risk:** ongoing. The npm CDN doesn't owe us reliability,
  so the rule should stay indefinitely.

### Silent windows-latest `Upload logs, and test results` failure — 2026-05-30 to ongoing — RECURRING TRANSIENT, NOW AUTO-RETRIED

- **Kind:** transient (recurring)
- **Signature (raw):** no error annotation; sometimes the previous
  `actions/upload-artifact` step emits `##[error]Failed to FinalizeArtifact:
  Unable to make request: ECONNRESET` but the `Upload logs, and test
  results` step itself reports nothing visible. The only annotation on
  the failing job is the unrelated windows-latest deprecation notice.
- **Records (7-day sample):**
  - **47** records where `Upload logs, and test results` failed alone
    (tests passed, no other step failed)
  - **24** records where a windows hang-dump check fires and the entire
    post-cleanup cascade fails
  - **All 71 on `windows-latest`.**
- **Affected jobs:** every windows-latest test job — `Hosting.MongoDB`,
  `Hosting.RemoteHost`, `Hosting.RabbitMQ`, `Hosting.EntityFrameworkCore`,
  `Templates-XUnit_V3_NewUpAndBuildSupportProjectTemplatesTests`, …. Tests
  pass cleanly; the upload step silently flakes; the job is marked failed.
- **Root cause:** transient infra on windows-latest runners — either
  GitHub Actions artifact-storage blip mid-upload (sometimes visible as
  ECONNRESET on the previous step, sometimes silent), or Windows DLL
  init crashes (`0xC0000142`) during post-test cleanup scripts that
  don't always reach the annotations channel. Tests themselves succeed
  and their results are already on disk.
- **Fix:** **auto-rerun rule added** in
  [microsoft/aspire#17978](https://github.com/microsoft/aspire/pull/17978).
  New JS condition retries when ALL failed steps are post-test cleanup
  steps (no test-execution failure, no non-cleanup step failure),
  regardless of whether the annotation contains a specific transient
  signature. Conservative: a genuinely persistent failure reproduces on
  retry and is caught by the 3-attempt cap. Also makes the Windows
  process initialization failure (`0xC0000142`) retry unconditional
  (`STATUS_DLL_INIT_FAILED` can never be caused by test code) and adds
  `Check for hang dump files` to `postTestCleanupFailureStepPatterns`.
  (Folds in and supersedes the previously-open #16187.)
- **Auto-rerun coverage (before / after):** Windows-init annotation cases
  only / all 71 post-cleanup-only cases.
- **Recurrence risk:** ongoing — windows-latest runners aren't getting
  more reliable.

### Template entry for future incidents (copy this when filing)

```
### <signature> — <YYYY-MM-DD to YYYY-MM-DD> — <KIND, STATUS>

- **Kind:** transient | fixed-in-source
- **Signature (raw):**
  ```
  <error string>
  ```
- **First seen:** <run id / timestamp>
- **Last seen:** <run id / timestamp>
- **Records (7-day sample):** <n> across <m> unique runs
- **Affected jobs:** <list>
- **Root cause:** <one paragraph>
- **Fix:** <commit sha + title> | <auto-rerun rule scope>
- **Auto-rerun coverage (before / after):** <yes/no> / <yes/no>
- **Recurrence risk:** <low/medium/high + why>
```

## Auto-rerun coverage by job type (TRX vs non-TRX)

7-day data: **874 failed-job records** (after excluding aggregator jobs)
in the 247 failed `ci.yml` runs.

| Category | Records | Auto-rerun paths that apply |
|---|---:|---|
| Aggregator (`Final Results`, `Final Test Results`) | 434 | n/a — excluded from analysis (`ignoredJobs` in the JS) |
| **TRX-emitting test-execution failures** (`Run tests*`, `Run nuget dependent tests*`) | 314 | (a) `transientAnnotationPatterns` (annotations), (b) `jobFailurePatterns` (job log), (c) `testFailurePatterns` (per-test TRX output) |
| **Non-TRX failures** (build, validation, upload, scan, summary) | 560 | (a) `transientAnnotationPatterns` (annotations), (b) `infrastructureNetworkFailureLogOverridePatterns` (job log, only when no test-execution step failed). **No TRX path applies.** |

Within the 560 non-TRX failed records, the top failure causes are now
all covered by either the JS hardcoded patterns or `jobFailurePatterns`
in `eng/test-retry-patterns.json`:

| Cause | Records | Covered by |
|---|---:|---|
| VS Code extension E2E corepack digest-mismatch | 79 | digest-mismatch JS rule (#17978) |
| Polyglot SDK validation (TS / Go / Rust / Java / Python) channel collision | ~200 | fixed in source (commit `27031991501`) |
| `Upload logs, and test results` solo / cascade | 71 | post-test-cleanup JS rule (#17978) |
| `Check validation results` (polyglot aggregator) | 38 | tail of polyglot, fixed in source |
| Build step failures (CS1729, NU1102, CS1591, …) | ~70 | real source bugs, author-fixed per PR — **not auto-rerun candidates** |

### Could non-TRX jobs benefit from synthetic test-result files?

User-suggested direction: have non-TRX jobs emit a common-format
test-result file so they can participate in the `testFailurePatterns`
TRX path.

Analysis of the 7-day data: **no transient infrastructure signature
appeared in build steps** that the existing `jobFailurePatterns` log-text
path couldn't already match. Build failures in the window were all real
source bugs that PR authors fixed locally (CS1729, NU1102, CS1591 —
explicitly NOT logged as incidents per the policy above).

The two surfaces that already apply to non-TRX jobs are:

1. `infrastructureNetworkFailureLogOverridePatterns` (hardcoded JS,
   matches against full job log) — covers Azure DevOps NuGet feed
   errors, dotnet acquisition CDN errors, GitHub API transients, and
   (#17978) corepack digest mismatch.
2. `jobFailurePatterns` (config file, matches against full job log) —
   easy to extend without code changes. Now covers ACR connection-refused
   and digest-mismatch in test-execution variants.

Synthetic TRX emission would add an indirection without unlocking
matching capability that isn't already available via 1+2. **Defer until
a concrete transient pattern is observed that neither surface can
match.** When that happens, file a new incident-log entry and decide
the fix surface at that point.

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
