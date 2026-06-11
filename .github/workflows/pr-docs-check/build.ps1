<#
.SYNOPSIS
    Recompiles the pr-docs-check agentic workflow and runs its unit tests.

.DESCRIPTION
    Repeatable maintenance helper for .github/workflows/pr-docs-check.md.

    After editing the workflow source (pr-docs-check.md), run this script to:
      1. Compile it to pr-docs-check.lock.yml via `gh aw compile`. The lock
         file is GENERATED output -- never hand-edit it.
      2. Run the compute_signals unit tests.

    Always commit pr-docs-check.md and the regenerated pr-docs-check.lock.yml
    together, and review the lock diff before committing.

.PARAMETER SkipCompile
    Skip the `gh aw compile` step (run tests only).

.PARAMETER SkipTests
    Skip the unit tests (compile only).

.EXAMPLE
    pwsh .github/workflows/pr-docs-check/build.ps1

.NOTES
    Cross-platform: run with PowerShell 7+ (`pwsh`) on Windows, Linux, or macOS.

    Requires the `gh aw` extension (github/gh-aw) pinned to the version in
    .github/aw/actions-lock.json (currently v0.77.5):

        gh extension install github/gh-aw --pin v0.77.5

    Requires Python 3 on PATH for the compute_signals tests.
#>
[CmdletBinding()]
param(
    [switch]$SkipCompile,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$workflowName  = 'pr-docs-check'
$workflowDir   = $PSScriptRoot                              # .github/workflows/pr-docs-check
$workflowsRoot = Split-Path -Parent $workflowDir            # .github/workflows
# repoRoot = two levels up from .github/workflows. Join-Path is nested for
# Windows PowerShell 5.1 compatibility (it only accepts two path segments).
$repoRoot      = (Resolve-Path (Join-Path (Join-Path $workflowsRoot '..') '..')).Path

# `gh aw compile` resolves workflows relative to the repo's .github/workflows
# directory, so it must run from the repository root.
Push-Location $repoRoot
try {
    if (-not $SkipCompile) {
        Write-Host "==> Compiling $workflowName (gh aw compile) ..." -ForegroundColor Cyan
        gh aw compile $workflowName
        if ($LASTEXITCODE -ne 0) { throw "gh aw compile failed (exit $LASTEXITCODE)" }
    }

    if (-not $SkipTests) {
        Write-Host "==> Running compute_signals tests ..." -ForegroundColor Cyan
        python -m unittest discover -s $workflowDir -v
        if ($LASTEXITCODE -ne 0) { throw "compute_signals tests failed (exit $LASTEXITCODE)" }
    }

    Write-Host "==> Done." -ForegroundColor Green
}
finally {
    Pop-Location
}
