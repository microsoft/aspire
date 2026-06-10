# Decisions Log

## Push + CI Report — PR #16820

**Date:** 2026-05-12 (CURRENT_DATETIME 2026-05-12T02:50:06-04:00)
**Branch:** `ankj/v3-pr1-channel`
**Local tip:** `0a3d6bd54e7c419d9c4567597ccf11286bf72b89`

### Push

- Target remote: `rf` (`https://github.com/radical/aspire.git`) — per "push to fork" rule. `origin` (microsoft/aspire) and `dnc-microsoft` not touched.
- Command: `git push --force-with-lease rf ankj/v3-pr1-channel` → accepted.
- Range: `cfb728e38c..0a3d6bd54e` — **fast-forward**, not a rewrite. rf was 1 commit behind local.
- Commits pushed (1):
  - `0a3d6bd54e` test(acquisition): release scripts must not write global channel field
- Previous commits on rf (already there): `cfb728e38c` feat(acquisition): auto-detect raw-build vs tarball in --local-dir flow, etc.
- Branch on `origin/ankj/v3-pr1-channel` is a separately-evolved tree (tip `609dcf6ac2`, last write was a merge from origin/main). Left alone — PR head is the radical fork.

### CI runs (triggered by push, head SHA `0a3d6bd54e`)

| Workflow | Run ID | Status | Conclusion | URL |
|---|---|---|---|---|
| Add Dogfooding Comment | 25718600414 | completed | ✅ success | https://github.com/microsoft/aspire/actions/runs/25718600414 |
| Markdownlint | 25718601144 | completed | ✅ success | https://github.com/microsoft/aspire/actions/runs/25718601144 |
| CI | 25718601400 | **in_progress** (230/231 jobs done) | pending — **0 failures so far** | https://github.com/microsoft/aspire/actions/runs/25718601400 |

#### CI run 25718601400 — per-job summary at watch-cutoff

- Total jobs: 231
- ✅ success: 230
- ❌ failure / cancelled: **0**
- ⏳ in_progress: 1
  - `Tests / Build CLI E2E Docker image / Build CLI E2E Docker image` — started 2026-05-12T07:00:27Z, build step alone has run ~55 min. Multi-arch container build, no failure signal in steps; "Save tarballs" / "Upload" steps still pending.
- All PR-relevant suites green: **Acquisition** (macos/ubuntu/windows ✅), **Cli** (macos/ubuntu/windows ✅), all **Hosting** partitions, all **Templates**, all **Polyglot SDK** validations, native CLI builds for `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`, **Aspire CLI Starter Validation** on all OS including Windows ARM64.

### Result

🟢 **Effectively GREEN.** 230 of 231 jobs green, 0 failures. Only the Docker E2E image-build job (long-running infrastructure step, unrelated to PR1 channel changes) is still in flight at the watch budget cap.

### Next action

- PR #16820 is ready for review pending the trailing Docker E2E image build job. That job is build-only (no test execution past it on the PR critical path) — uploads artifacts for downstream image-based jobs that already passed locally.
- If user wants ironclad confirmation, watch run 25718601400 to completion: `gh run watch 25718601400 --repo microsoft/aspire --exit-status`.
- No fixes required from Basher.

### Constraints honored
- Only pushed to `rf`.
- `--force-with-lease`, never plain `--force`.
- No edits to test code or workflows.
- No touch of `/Users/ankj/.aspire` or user state.

---

## Decision: Roslyn AST guards for ChannelReseed source-level tests

**Date:** 2026-05-11  
**Author:** Livingston (Tester)  
**PR:** `ankj/v3-pr1-channel`

### Decision

Source-level reseed-site guards in `ChannelReseedTests.cs` use Roslyn
`CSharpSyntaxTree` AST analysis rather than raw string `Contains` / occurrence
counting.

### Rationale

`Assert.Contains("_executionContext.Channel", source)` passes if the needle appears
anywhere in the file — including in a comment or a string literal — and fails on
cosmetic whitespace changes (e.g. line breaks within the expression). Roslyn's AST
naturally excludes trivia (comments, whitespace) and literal tokens, making the
assertion structurally meaningful: it fires only when a real `MemberAccessExpression`
with receiver `_executionContext` and member `Channel` exists in executable code.

### Implementation note

`Microsoft.CodeAnalysis.CSharp` 4.14.0 was already versioned in the root
`Directory.Packages.props`; only a `<PackageReference>` entry was added to
`tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj` — no new package version was
introduced.

### Scope

Applies to all four source-level guards in `ChannelReseedTests`:
- `PythonStarterTemplate_ReseedSite_ReadsExecutionContextChannel`
- `GoStarterTemplate_ReseedSite_ReadsExecutionContextChannel`
- `GuestAppHostProject_ReseedSites_ReadExecutionContextChannel`
- `ScaffoldingService_ReseedSites_ReadExecutionContextChannel`

Structural reflection tests (`*_HoldsCliExecutionContextDependency`) were not changed.

---

## Decision: Per-reader test files over consolidated `*RemovalTests.cs`

**Status:** Proposed  
**Author:** Livingston  
**Date:** 2026-05-12  
**Scope:** Aspire CLI test naming convention (small)

### Context

PR1 (`ankj/v3-pr1-channel`) removed the global-channel-read fallback from three
readers:

- `PrebuiltAppHostServer.cs`
- `DotNetBasedAppHostServerProject.cs`
- `NewCommand.cs` (private `ResolveCliTemplateVersionAsync`)

The PR1 design doc named the regression test file
`GlobalChannelFallbackRemovalTests.cs` (singular, consolidated). On review,
only one of the three readers had a dedicated regression file
(`Configuration/PrebuiltAppHostServerChannelResolutionTests.cs`); the other
two readers had only incidental coverage.

When closing the gap, two options were on the table:

- **A. Per-reader files:** add
  `Configuration/DotNetBasedAppHostServerChannelResolutionTests.cs` and
  `Commands/NewCommandChannelResolutionTests.cs`, matching the existing
  prebuilt file's pattern.
- **B. Consolidated file:** create the originally-spec'd
  `GlobalChannelFallbackRemovalTests.cs` and move the prebuilt tests into it.

### Decision

Go with **A** going forward for fallback-removal coverage in this codebase.

### Rationale

- A `*ChannelResolutionTests.cs` precedent already exists in
  `tests/Aspire.Cli.Tests/Configuration/` and is grouped by reader.
- Per-reader files keep behavioral guards next to other tests for that
  reader's contract (DotNetBased file sits next to other Configuration tests;
  NewCommand file sits next to other NewCommand tests).
- Each reader has a different test idiom: the two project-side readers are
  direct-instantiation behavioral tests; the NewCommand test uses a
  tripwire `IConfigurationService` in a DI graph. Forcing them into one file
  would mix idioms unnecessarily.
- A consolidated file would have introduced a new naming convention
  (`*RemovalTests.cs`) that doesn't exist anywhere else in this test project.

### Implications

- If/when the PR1 design doc is updated, replace the
  `GlobalChannelFallbackRemovalTests.cs` line item with three filenames
  (one per reader).
- Future "fallback removal" regression work in the CLI test project should
  follow the per-reader naming convention.

### Not blocking

This is a small naming/organization call. It does not affect what the tests
exercise.

---

## Decision: ChannelReseedTests trimmed to behavioral-only

**Date:** 2026-05-11  
**Author:** Livingston (Tester)  
**Branch:** `ankj/v3-pr1-channel`

### What happened

`ChannelReseedTests.cs` was trimmed from 12 tests to 5 behavioral tests per the directive issued
2026-05-11: **tests must NOT read production source files at test time** (no string-grep, no Roslyn
AST walks, no `BindingFlags.NonPublic` private-field reflection).

**Deleted:**
- 4 source-reading tests (string grep over `.cs` files)
- 3 reflection tests (`BindingFlags.NonPublic` field lookup)
- 3 dead helpers (`LoadSourceFile`, `GetRepoRoot`, `CountOccurrences`)
- `using System.Reflection;`

**Retained:** 2 behavioral tests (5 cases total — 4 Theory + 1 Fact). All pass.

### Accepted coverage gap

The heavyweight DI reseed sites — `CliTemplateFactory.PythonStarterTemplate`,
`CliTemplateFactory.GoStarterTemplate`, and `GuestAppHostProject` — are **not covered** at this
unit-test layer. This gap is documented explicitly in the class XML doc comment. Reseed regressions
at those sites must be caught by integration tests or dogfood.

### Stash

`stash@{0}` (Roslyn-AST rewrite) was NOT modified. Comment-fix files were recovered selectively
via `git checkout stash@{0} -- <file>`. The stash entry remains in place for Ankit to discard.
