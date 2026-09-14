$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = $env:WINUI_PROBE_REPO_ROOT
$evidence = $env:WINUI_PROBE_EVIDENCE
$projectDirectory = Join-Path $env:WINUI_PROBE_WORKSPACE 'AspireE2E.WinUI'
$project = Join-Path $projectDirectory 'AspireE2E.WinUI.csproj'
$dotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'

function Invoke-ProbeDotNet([string] $stage, [string[]] $arguments) {
    $log = Join-Path $evidence "$stage.log"
    Write-Host "WINUI_PROBE stage=$stage command=$dotnet $($arguments -join ' ')"
    & $dotnet @arguments *> $log
    $code = $LASTEXITCODE
    $text = Get-Content -LiteralPath $log -Raw
    Write-Host $text
    Write-Host "WINUI_PROBE stage=$stage exit=$code"
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function Invoke-RequiredDotNet([string] $stage, [string[]] $arguments) {
    $result = Invoke-ProbeDotNet $stage $arguments
    if ($result.ExitCode -ne 0) {
        throw "CAPABILITY_OR_SETUP_FAILURE: $stage failed with exit $($result.ExitCode). This is not a reproduction."
    }
    return $result
}

function Resolve-ProjectPath([string] $value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw 'CAPABILITY_OR_SETUP_FAILURE: MSBuild returned an empty required path.'
    }
    $resolved = [IO.Path]::GetFullPath($value, $projectDirectory)
    if (-not $resolved.StartsWith("$projectDirectory\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "CAPABILITY_OR_SETUP_FAILURE: Unexpected output outside the generated project: $resolved"
    }
    return $resolved
}

function Read-BuildProperties([string] $stage, [string[]] $modeArguments) {
    $names = 'IntermediateOutputPath,XamlGeneratedOutputPath,XamlSavedStateFilePath,MSBuildProjectExtensionsPath,ProjectAssetsFile,RuntimeIdentifier,TargetFramework,TargetPath,NuGetPackageRoot'
    $result = Invoke-RequiredDotNet $stage (@('msbuild', $project, '-nologo', "-getProperty:$names") + $modeArguments)
    # Multiple -getProperty values produce {"Properties":{"IntermediateOutputPath":"obj\\...", ...}}.
    # Parse the actual evaluation; inferred obj paths would mask a broken isolation fix.
    $properties = ($result.Output | ConvertFrom-Json).Properties
    $properties | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence "$stage.json")
    return $properties
}

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "CAPABILITY_FAILURE: Repository-local SDK not found: $dotnet"
}
Copy-Item -LiteralPath $project -Destination (Join-Path $evidence 'generated.csproj')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'App.xaml') -Destination (Join-Path $evidence 'generated.App.xaml')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'App.xaml.cs') -Destination (Join-Path $evidence 'generated.App.xaml.cs')
$null = Invoke-RequiredDotNet 'sdk' @('--info')
$null = Invoke-RequiredDotNet 'restore' @(
    'restore', $project, '--configfile', (Join-Path $repoRoot 'NuGet.config'),
    '-p:UseSharedCompilation=false', '-nodeReuse:false', '-verbosity:minimal'
)

$designArguments = @(
    '-p:DesignTimeBuild=true', '-p:BuildingInsideVisualStudio=true',
    '-p:BuildProjectReferences=false', '-p:SkipCompilerExecution=true',
    '-p:ProvideCommandLineArgs=true', '-p:UseSharedCompilation=false', '-nodeReuse:false'
)
$normalArguments = @('-p:DesignTimeBuild=false', '-p:UseSharedCompilation=false', '-nodeReuse:false')
$design = Read-BuildProperties 'design-properties' $designArguments
$normal = Read-BuildProperties 'normal-properties' $normalArguments

$assetsPath = Resolve-ProjectPath $normal.ProjectAssetsFile
if ($assetsPath -ne (Resolve-ProjectPath $design.ProjectAssetsFile)) {
    throw 'CAPABILITY_OR_SETUP_FAILURE: Both build modes must use the restored NuGet assets.'
}
if ((Resolve-ProjectPath $normal.MSBuildProjectExtensionsPath) -ne (Resolve-ProjectPath $design.MSBuildProjectExtensionsPath)) {
    throw 'CAPABILITY_OR_SETUP_FAILURE: Build modes disagree on MSBuildProjectExtensionsPath.'
}
if ($env:GITHUB_ACTIONS -eq 'true' -and $normal.RuntimeIdentifier -ne 'win-x64') {
    throw "CAPABILITY_FAILURE: Expected win-x64, got $($normal.RuntimeIdentifier)."
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
foreach ($library in @(
    'Microsoft.WindowsAppSDK/1.8.260209005',
    'Microsoft.Windows.SDK.BuildTools/10.0.26100.7175',
    'Microsoft.WindowsAppSDK.WinUI/1.8.260204000'
)) {
    if ($null -eq $assets.libraries.PSObject.Properties[$library]) {
        throw "CAPABILITY_OR_SETUP_FAILURE: Missing pinned dependency $library."
    }
}

# Prove the unmodified fixture can complete a normal build before introducing contention.
$null = Invoke-RequiredDotNet 'unlocked-normal-build' (@(
    'build', $project, '--no-restore', '--disable-build-servers', '-verbosity:minimal',
    "-bl:$(Join-Path $evidence 'unlocked-normal-build.binlog')"
) + $normalArguments)

$designDirectory = Resolve-ProjectPath $design.XamlGeneratedOutputPath
$normalDirectory = Resolve-ProjectPath $normal.XamlGeneratedOutputPath
$designInput = Join-Path $designDirectory 'input.json'
$designGenerated = Join-Path $designDirectory 'App.g.i.cs'
$normalInput = Join-Path $normalDirectory 'input.json'
$normalGenerated = Join-Path $normalDirectory 'App.g.cs'
$designState = Resolve-ProjectPath $design.XamlSavedStateFilePath
$normalState = Resolve-ProjectPath $normal.XamlSavedStateFilePath

# Remove only these evaluated, project-local outputs to prove both compiler passes run.
# A warm build that merely reuses generated XAML would give a false positive.
foreach ($file in @($designGenerated, $designState)) {
    if (Test-Path -LiteralPath $file) {
        Remove-Item -LiteralPath $file
    }
}
$null = Invoke-RequiredDotNet 'design-time-compile' (@(
    'msbuild', $project, '-t:Compile', '-nologo', '-verbosity:minimal',
    "-bl:$(Join-Path $evidence 'design-time-compile.binlog')"
) + $designArguments)
foreach ($file in @($designInput, $designGenerated)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "CAPABILITY_OR_SETUP_FAILURE: Design-time compilation did not generate $file."
    }
}
Copy-Item -LiteralPath $designInput -Destination (Join-Path $evidence 'design-input.json')
Copy-Item -LiteralPath $designGenerated -Destination (Join-Path $evidence 'design-App.g.i.cs')

foreach ($file in @($normalGenerated, $normalState)) {
    if (Test-Path -LiteralPath $file) {
        Remove-Item -LiteralPath $file
    }
}
if ($normalInput -ne $designInput -and (Test-Path -LiteralPath $normalInput)) {
    Remove-Item -LiteralPath $normalInput
}
# Invalidate XAML's content-based cache as well as its saved state, identically in both phases.
[IO.File]::AppendAllText((Join-Path $projectDirectory 'App.xaml'), "`n<!-- WinUI contention probe: rerun the XAML pass. -->`n")

$handle = [IO.File]::Open($designInput, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Write-Host "WINUI_PROBE exclusive_lock=$designInput"
    $build = Invoke-ProbeDotNet 'locked-normal-build' (@(
        'build', $project, '--no-restore', '--disable-build-servers', '-verbosity:minimal',
        "-bl:$(Join-Path $evidence 'locked-normal-build.binlog')"
    ) + $normalArguments)
}
finally {
    $handle.Dispose()
}

# Match the original XAML task error, not a restore, SDK, cleanup, or unrelated file-lock failure:
# Microsoft.UI.Xaml.Markup.Compiler.interop.targets(560,9): error : The process cannot access
# the file '...\input.json' because it is being used by another process.
$sharingPattern = "(?im)Microsoft\.UI\.Xaml\.Markup\.Compiler\.interop\.targets\(\d+,\d+\): error[^\r\n]*The process cannot access the file '$([regex]::Escape($designInput))' because it is being used by another process\."
$normalXamlRegenerated = (Test-Path -LiteralPath $normalGenerated -PathType Leaf) -and (Test-Path -LiteralPath $normalInput -PathType Leaf)
$classification = if ($build.ExitCode -eq 0 -and $normalXamlRegenerated) {
    'passed'
} elseif ($build.ExitCode -ne 0 -and $build.Output -match $sharingPattern) {
    'sharing-violation'
} elseif ($build.ExitCode -eq 0) {
    'xaml-did-not-execute'
} else {
    'unexpected-build-failure'
}

$outcome = [ordered]@{
    classification = $classification
    normalBuildExitCode = $build.ExitCode
    normalXamlRegenerated = $normalXamlRegenerated
    designTimeInput = $designInput
    normalInput = $normalInput
    sharedNuGetAssets = $assetsPath
    runtimeIdentifier = $normal.RuntimeIdentifier
}
$outcome | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'outcome.json')
Write-Host "WINUI_PROBE outcome=$($outcome | ConvertTo-Json -Compress)"

# The xUnit test asserts the real build exit code. Nonzero exits here identify setup failures only.
exit 0
