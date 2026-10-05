[CmdletBinding(PositionalBinding=$false)]
Param(
  [switch][Alias('h')]$help,
  [switch][Alias('t')]$test,
  [ValidateSet("Debug","Release")][string[]][Alias('c')]$configuration = @("Debug"),
  [string][Alias('v')]$verbosity = "minimal",
  [switch]$vs,
  [ValidateSet("windows","linux","osx")][string]$os,
  [switch]$testnobuild,
  [ValidateSet("x86","x64","arm","arm64")][string[]][Alias('a')]$arch = @([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()),
  [switch]$mauirestore,
  [switch]$bundle,
  [string]$runtimeVersion,
  [switch]$ci,
  [switch]$clean,
  [ValidateSet('true','false')][string]$warnAsError = 'true',
  [string]$warnNotAsError = '',

  [Parameter(ValueFromRemainingArguments=$true)][String[]]$remainingArguments
)

$ErrorActionPreference = 'Stop'

function Get-Help() {
  Write-Host "Common settings:"
  Write-Host "  -arch (-a)                     Target platform: x86, x64, arm or arm64."
  Write-Host "                                 [Default: Your machine's architecture.]"
  Write-Host "  -binaryLog (-bl)               Output binary log."
  Write-Host "  -configuration (-c)            Build configuration: Debug or Release."
  Write-Host "                                 [Default: Debug]"
  Write-Host "  -help (-h)                     Print help and exit."
  Write-Host "  -os                            Target operating system: windows, linux or osx."
  Write-Host "                                 [Default: Your machine's OS.]"
  Write-Host "  -verbosity (-v)                MSBuild verbosity: q[uiet], m[inimal], n[ormal], d[etailed], and diag[nostic]."
  Write-Host "                                 [Default: Minimal]"
  Write-Host "  -warnNotAsError <codes>         Additional warning exemptions, merged with the evaluated repository policy."
  Write-Host "  -vs                            Open the solution with Visual Studio using the locally acquired SDK."
  Write-Host ""

  Write-Host "Actions (defaults to -restore -build):"
  Write-Host "  -build (-b)             Build all source projects."
  Write-Host "                          This assumes -restore has been run already."
  Write-Host "  -clean                  Clean the solution."
  Write-Host "  -pack                   Package build outputs into NuGet packages."
  Write-Host "  -publish                Publish artifacts (e.g. symbols)."
  Write-Host "                          This assumes -build has been run already."
  Write-Host "  -rebuild                Rebuild all source projects."
  Write-Host "  -restore                Restore dependencies."
  Write-Host "  -mauirestore            Restore dependencies and install MAUI workload (only on Windows/macOS)."
  Write-Host "  -sign                   Sign build outputs."
  Write-Host "  -test (-t)              Incrementally builds and runs tests."
  Write-Host "                          Use in conjunction with -testnobuild to only run tests."
  Write-Host ""

  Write-Host "Libraries settings:"
  Write-Host "  -testnobuild            Skip building tests when invoking -test."
  Write-Host "  -buildExtension         Build the VS Code extension."
  Write-Host "  -bundle                 Build the self-contained bundle (CLI + Runtime + Dashboard + DCP)."
  Write-Host "  -runtimeVersion <ver>   .NET runtime version for bundle (default: from eng/Versions.props RuntimeVersion)."
  Write-Host ""

  Write-Host "Command-line arguments not listed above are passed through to MSBuild."
  Write-Host "The above arguments can be shortened as much as to be unambiguous."
  Write-Host "(Example: -con for configuration, -t for test, etc.)."
  Write-Host ""
}

if ($help) {
  Get-Help
  exit 0
}

if ($vs) {
  $solution = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath "Aspire.slnx"

  [bool]$warnAsError = [bool]::Parse($warnAsError)
  . $PSScriptRoot\common\tools.ps1

  # This tells .NET Core to use the bootstrapped runtime
  $env:DOTNET_ROOT=InitializeDotNetCli -install:$true -createSdkLocationFile:$true

  # This tells MSBuild to load the SDK from the directory of the bootstrapped SDK
  $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR=$env:DOTNET_ROOT

  # Put our local dotnet.exe on PATH first so Visual Studio knows which one to use
  $env:PATH=($env:DOTNET_ROOT + ";" + $env:PATH);

  # Launch Visual Studio with the locally defined environment variables
  ."$solution"

  exit 0
}

# PowerShell can deliver "-property:Name=Value" as two arguments ("-property", "Name=Value")
# for a remaining-arguments parameter. Recombine it before evaluating the policy.
$normalizedArguments = @()
for ($i = 0; $i -lt $remainingArguments.Count; $i++) {
  if ($remainingArguments[$i] -eq '-property') {
    if ($i + 1 -ge $remainingArguments.Count) {
      throw "No property value supplied for $($remainingArguments[$i])."
    }
    $normalizedArguments += "/p:$($remainingArguments[++$i])"
  } else {
    $normalizedArguments += $remainingArguments[$i]
  }
}
$remainingArguments = $normalizedArguments

# Check if an action is passed in
$actions = "b","build","r","restore","rebuild","sign","testnobuild","publish","clean","t","test"
$actionPassedIn = @(Compare-Object -ReferenceObject @($PSBoundParameters.Keys) -DifferenceObject $actions -ExcludeDifferent -IncludeEqual).Length -ne 0
if ($null -ne $remainingArguments -and $actionPassedIn -ne $true) {
  $actionPassedIn = @(Compare-Object -ReferenceObject $remainingArguments -DifferenceObject $actions.ForEach({ "-" + $_ }) -ExcludeDifferent -IncludeEqual).Length -ne 0
}

$arguments = @()
if (!$actionPassedIn) {
  $arguments += '-restore', '-build'
}

foreach ($argument in $PSBoundParameters.Keys)
{
  switch($argument)
  {
    "os"                     { $arguments += "/p:TargetOS=$($PSBoundParameters[$argument])" }
    "remainingArguments"     { $arguments += $remainingArguments }
    "verbosity"              { $arguments += "-$argument", $PSBoundParameters[$argument] }
    "configuration"          { $configuration = (Get-Culture).TextInfo.ToTitleCase($($PSBoundParameters[$argument])); $arguments += '-configuration', $configuration }
    "arch"                   { $arguments += "/p:TargetArchitecture=$($PSBoundParameters[$argument])" }
    "testnobuild"            { $arguments += "/p:VSTestNoBuild=true" }
    "buildExtension"         { $arguments += "/p:BuildExtension=true" }
    "mauirestore"            { $arguments += '-restoreMaui' }
    "ci"                    { $arguments += '-ci' }
    "clean"                 { if ($clean) { $arguments += '-clean' } }
    "warnAsError"            { } # Passed as a boolean below.
    "warnNotAsError"         { } # Merged with the repository policy below.
    "bundle"                 { } # Handled after main build
    "runtimeVersion"         { } # Handled after main build
    default                  { $arguments += "/p:$argument=$($PSBoundParameters[$argument])" }
  }
}

if ($env:TreatWarningsAsErrors -eq 'false') {
  # Arcade forwards this value directly into /p:TreatWarningsAsErrors=...,
  # and the Csc task only accepts boolean literals (true/false). The
  # earlier '0' worked as a shell-truthy switch but produced
  # 'MSB4030: "0" is an invalid value for the "TreatWarningsAsErrors"
  # parameter' once it reached the inner MSBuild call.
  $warnAsError = 'false'
}

# Arcade handles clean before invoking MSBuild, even when other actions are supplied.
# Preserve SDK-free cleanup instead of bootstrapping just to evaluate unused policy.
if (!$clean -and [bool]::Parse($warnAsError)) {
  # Arcade restores through a standalone NuGet.targets project that never imports
  # Directory.Build.props. Forward its evaluated policy to the command-line logger too.
  # See https://github.com/dotnet/msbuild/issues/10801.
  $evaluationProperties = @("/p:Configuration=$configuration", "/p:ContinuousIntegrationBuild=$($ci.IsPresent)")
  $evaluationProperties += @($arguments | Where-Object { $_ -match '^[-/](p|property):' })
  $repositoryWarnings = & {
    $ErrorActionPreference = 'Stop'
    [bool]$warnAsError = [bool]::Parse($warnAsError)
    . $PSScriptRoot\common\tools.ps1
    $dotnetRoot = InitializeDotNetCli -install:$true
    $dotnet = Join-Path $dotnetRoot (GetExecutableFileName 'dotnet')
    $output = & $dotnet msbuild "$PSScriptRoot\WarningPolicy.proj" -nologo -getProperty:WarningsNotAsErrors @evaluationProperties
    if ($LASTEXITCODE -ne 0) {
      throw "Could not evaluate the repository warning policy (exit code $LASTEXITCODE).`n$($output -join [Environment]::NewLine)"
    }
    # A single-property query prints a semicolon-delimited value, e.g. ";CS1591;NU1901".
    # Reject unexpected diagnostic output rather than treating it as warning codes.
    if (@($output).Count -gt 1) {
      throw "Unexpected output while evaluating the repository warning policy:`n$($output -join [Environment]::NewLine)"
    }
    "$output".Trim()
  }
  $warnNotAsError = (@("$repositoryWarnings;$warnNotAsError" -split ';') |
    ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique) -join ';'
}

$arguments += '-warnAsError', [bool]::Parse($warnAsError)
if ($warnNotAsError) {
  $arguments += '-warnNotAsError', $warnNotAsError
}

# Array splatting treats dynamically supplied parameter names as positional values
# for PowerShell scripts. Quote all values when forming the invocation so paths with
# spaces and semicolon-delimited warning lists remain single arguments.
$quotedArguments = foreach ($argument in $arguments) {
  if ($argument -is [bool]) {
    if ($argument) { '$true' } else { '$false' }
  } elseif ($argument -match '^-[a-zA-Z][a-zA-Z0-9]*$') {
    $argument
  } else {
    "'$(([string]$argument).Replace("'", "''"))'"
  }
}
$invocation = "& '$($PSScriptRoot.Replace("'", "''"))/common/build.ps1' $($quotedArguments -join ' ')"
Write-Host $invocation
Invoke-Expression $invocation
$buildExitCode = $LASTEXITCODE

if ($buildExitCode -ne 0) {
  exit $buildExitCode
}

# Build bundle if requested
if ($bundle) {
  Write-Host ""
  Write-Host "Building bundle via MSBuild..."
  Write-Host ""
  
  $repoRoot = Split-Path $PSScriptRoot -Parent
  $config = if ($configuration) { $configuration } else { "Debug" }
  
  # Determine RID
  $targetOs = if ($os) { $os } else { 
    if ($IsWindows -or $env:OS -eq "Windows_NT") { "win" }
    elseif ($IsMacOS) { "osx" }
    else { "linux" }
  }
  $targetArch = if ($arch) { 
    # If arch is an array with multiple values, use only the first one for bundle build
    if ($arch -is [array]) { $arch[0] } else { $arch }
  } else { 
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
  }
  if ($targetArch -eq "x64" -or $targetArch -eq "amd64") { $targetArch = "x64" }
  $rid = "$targetOs-$targetArch"
  
  # Build MSBuild arguments (use MSBuild syntax, not dotnet build syntax)
  $bundleArgs = @(
    "$PSScriptRoot/Bundle.proj",
    "/p:Configuration=$config",
    "/p:TargetRid=$rid"
  )
  
  # Pass through SkipNativeBuild if set
  if ($remainingArguments -contains "/p:SkipNativeBuild=true") {
    $bundleArgs += "/p:SkipNativeBuild=true"
  }
  
  # CI flag is passed to Bundle.proj which handles version computation via Versions.props
  if ($ci) {
    $bundleArgs += "/p:ContinuousIntegrationBuild=true"
  }
  
  Write-Host "  RID: $rid"
  Write-Host "  Configuration: $config"
  Write-Host ""
  
  & dotnet msbuild @bundleArgs
  
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

exit 0
