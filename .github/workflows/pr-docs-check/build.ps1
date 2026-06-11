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

    The script first verifies that the installed `gh aw` extension matches the
    version pinned in .github/aw/actions-lock.json, because a different compiler
    version emits a different lock file.

.PARAMETER SkipCompile
    Skip the `gh aw compile` step (run tests only).

.PARAMETER SkipTests
    Skip the unit tests (compile only).

.PARAMETER VerifyParity
    After compiling, assert that pr-docs-check.lock.yml has no pending git diff.
    Use this as a guard: with the source committed, recompiling with the correct
    tooling must reproduce the committed lock byte-for-byte. A non-empty diff
    means either the lock was hand-edited, the `.md` was changed without
    recompiling, or the `gh aw` version is wrong.

.EXAMPLE
    pwsh .github/workflows/pr-docs-check/build.ps1

.EXAMPLE
    # CI-style guard: confirm the committed lock is in sync with the source.
    pwsh .github/workflows/pr-docs-check/build.ps1 -VerifyParity -SkipTests

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
    [switch]$SkipTests,
    [switch]$VerifyParity
)

$ErrorActionPreference = 'Stop'

$workflowName  = 'pr-docs-check'
$workflowDir   = $PSScriptRoot                              # .github/workflows/pr-docs-check
$workflowsRoot = Split-Path -Parent $workflowDir            # .github/workflows
# repoRoot = two levels up from .github/workflows. Join-Path is nested for
# Windows PowerShell 5.1 compatibility (it only accepts two path segments).
$repoRoot      = (Resolve-Path (Join-Path (Join-Path $workflowsRoot '..') '..')).Path
$lockRelPath   = '.github/workflows/pr-docs-check.lock.yml'

# Resolve a working Python 3 interpreter. The CI pre-agent step runs the signals
# script with `python3` (see pr-docs-check.lock.yml), and on most Linux/macOS
# systems bare `python` is absent or still points at Python 2 -- so prefer
# `python3`. On Windows, however, `python3` is frequently the Microsoft Store
# "App execution alias" stub that prints "Python was not found" and exits
# non-zero without running anything, so each candidate is probed with
# `--version` and accepted only when it actually reports Python 3.
function Resolve-Python {
    foreach ($candidate in 'python3', 'python', 'py') {
        if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
        $ver = (& $candidate --version 2>&1 | Out-String)
        if ($LASTEXITCODE -eq 0 -and $ver -match 'Python\s+3') { return $candidate }
    }
    throw "No working Python 3 interpreter found on PATH (probed python3, python, py). Install Python 3 to run the compute_signals tests."
}

# Compare the installed `gh aw` version against the pin in actions-lock.json so
# a stale or upgraded extension (which would emit a different lock file) is
# caught before it produces a confusing diff.
function Assert-GhAwVersion {
    $lockJsonPath = Join-Path $repoRoot '.github/aw/actions-lock.json'
    if (-not (Test-Path $lockJsonPath)) { return }

    # Pinned version, e.g. the "github/gh-aw-actions/setup@v0.77.5" key.
    $pinned = $null
    $match = Select-String -Path $lockJsonPath -Pattern 'gh-aw-actions/setup@(v[0-9][^"]*)' | Select-Object -First 1
    if ($match) { $pinned = $match.Matches[0].Groups[1].Value }

    # `gh aw version` prints e.g. "gh aw version v0.77.5" (plus an optional
    # "new release available" notice); it writes to stderr, so merge streams
    # with 2>&1 before pulling the first vN.N.N token.
    $installed = $null
    $verOut = (gh aw version 2>&1 | Out-String)
    $m = [regex]::Match($verOut, 'v[0-9][\w.\-]*')
    if ($m.Success) { $installed = $m.Value }

    if (-not $pinned -or -not $installed) { return }
    if ($pinned -ne $installed) {
        Write-Warning "Installed 'gh aw' is $installed but actions-lock.json pins $pinned. The lock file may differ. To match CI, run: gh extension install github/gh-aw --pin $pinned --force"
    }
    else {
        Write-Host "==> gh aw version $installed matches pin $pinned." -ForegroundColor DarkGray
    }
}

# `gh aw compile` resolves workflows relative to the repo's .github/workflows
# directory, so it must run from the repository root.
Push-Location $repoRoot
try {
    if (-not $SkipCompile) {
        Assert-GhAwVersion
        Write-Host "==> Compiling $workflowName (gh aw compile) ..." -ForegroundColor Cyan
        gh aw compile $workflowName
        if ($LASTEXITCODE -ne 0) { throw "gh aw compile failed (exit $LASTEXITCODE)" }
    }

    if ($VerifyParity) {
        Write-Host "==> Verifying lock parity (recompiled lock must match committed lock) ..." -ForegroundColor Cyan
        git diff --quiet -- $lockRelPath
        if ($LASTEXITCODE -ne 0) {
            git --no-pager diff -- $lockRelPath
            throw "$lockRelPath differs from the committed version after recompiling. Commit the regenerated lock, or confirm the 'gh aw' version matches the pin in actions-lock.json."
        }
        Write-Host "    Lock is in sync with the committed source." -ForegroundColor DarkGray
    }

    if (-not $SkipTests) {
        $python = Resolve-Python
        Write-Host "==> Running compute_signals tests ($python) ..." -ForegroundColor Cyan
        & $python -m unittest discover -s $workflowDir -v
        if ($LASTEXITCODE -ne 0) { throw "compute_signals tests failed (exit $LASTEXITCODE)" }
    }

    Write-Host "==> Done." -ForegroundColor Green
}
finally {
    Pop-Location
}
