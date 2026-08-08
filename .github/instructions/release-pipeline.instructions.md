---
applyTo: "eng/pipelines/release-publish-nuget.yml,eng/pipelines/templates/publish-*.yml,eng/pipelines/scripts/**,.github/workflows/release-github-tasks.yml"
---

# Release Pipeline Review Patterns

The release pipeline (`eng/pipelines/release-publish-nuget.yml`, definition `microsoft-aspire-Release-To-NuGet`) and the GitHub release tasks it dispatches (`.github/workflows/release-github-tasks.yml`) are **side-effecting and never run on a GitHub PR**. Review them with that in mind.

## DryRun must be fully read-only

Every step that has an external side effect MUST be a no-op when `DryRun=true` — including the GitHub Tasks dispatch. "Side effect" means any of: publishing a package (NuGet, npm), pushing a git ref or tag, creating/editing a GitHub release, opening or editing a PR or issue, minting a credential/token for a live call, promoting a build to a channel (darc), submitting a manifest (WinGet/Homebrew), or **dispatching/triggering an external workflow**.

When reviewing a new or changed step:

- Confirm it is gated on `DryRun` — either compile-time (`${{ if and(eq(parameters.DryRun, false), …) }}`, so the step is not emitted) or runtime (`if ($dryRun) { … exit 0 }`).
- A dry run must reach `DryRun`/`Dry Run`/`No … were actually published` markers in the log instead of performing the action.
- Gating a side effect *only* on a `Skip*` flag is **not** sufficient — `Skip*` is for idempotent re-runs, not for read-only validation. A reviewer should flag any side-effecting step reachable under `DryRun=true`. (Known gap: the `GitHubTasks` stage dispatch is gated only by `SkipGitHubTasks`, tracked by microsoft/aspire#18129.)

## Template-expression traps in inlined scripts

AzDO evaluates `${{ … }}` template expressions **anywhere** inside an inlined `powershell:`/`pwsh:`/`bash:` block scalar — including inside PowerShell/shell **comments and string literals**. It does not treat them as script text.

- Never write a literal `${{ … }}` inside an inlined script block unless you intend the template engine to expand it. A comment like `# interpolating ${{ parameters.* }} into this script` compiles `parameters.*` to the parameters **object** and fails with `Unable to convert from Object to String`.
- This class of error fails template compilation **before any stage runs**, and passes every GitHub PR check plus the offline `Infrastructure.Tests` (AzDO never compiles the pipeline on a PR). Validate compilation with AzDO `previewRun` (see the `azdo-internal` skill) for any change to these files.

## Testing

Changes here are validated via the `pr-testing` skill's `ci-infra-testing.md` (Track B) and the `azdo-internal` skill: compile-check with `previewRun` first, then a `DryRun=true` run pinned to a real def-1602 source build, with `SkipGitHubTasks=true` for a fully read-only run.
