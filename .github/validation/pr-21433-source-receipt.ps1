param(
    [Parameter(Mandatory)][ValidateSet("before-build", "after-build")][string]$Stage,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][int]$Index
)

$ErrorActionPreference = "Stop"
$head = (& git --no-pager rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $env:TEST_SOURCE_SHA) {
    throw "Checkout does not match TEST_SOURCE_SHA."
}
$tree = (& git --no-pager rev-parse "HEAD^{tree}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot resolve checkout tree." }
$dirty = & git --no-pager status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw "Tracked checkout is not clean." }
$project = "tests/Aspire.Managed.Tests/Aspire.Managed.Tests.csproj"
if (-not (Test-Path $project)) { throw "Managed project is absent." }

[ordered]@{
    schema = 1
    stage = $Stage
    source = $head
    tree = $tree
    tracked_clean = $true
    project = $project
    workflow_source = $env:GITHUB_SHA
    repository = $env:GITHUB_REPOSITORY
    run_id = [long]$env:GITHUB_RUN_ID
    attempt = [int]$env:GITHUB_RUN_ATTEMPT
    index = $Index
    runner_os = $env:RUNNER_OS
    runner_arch = $env:RUNNER_ARCH
    image_os = $env:ImageOS
    image_version = $env:ImageVersion
    observed_at = [DateTimeOffset]::UtcNow.ToString("o")
} | ConvertTo-Json | Set-Content -Encoding utf8 -Path $OutputPath
