// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BuildWarningPolicyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [RequiresTools(["pwsh"])]
    public async Task RepositoryPolicyPreservesInheritedExemptionsAndAuditSettings(bool strict, bool official)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var script = Path.Combine(workspace.Path, "evaluate.ps1");
        await File.WriteAllTextAsync(script, """
            & $env:TEST_DOTNET msbuild (Join-Path $env:TEST_REPO 'eng/WarningPolicy.proj') -nologo `
              -getProperty:WarningsNotAsErrors,NuGetAuditMode,NuGetAudit,TreatWarningsAsErrors `
              "/p:TreatNuGetAuditWarningsAsErrors=$env:TEST_STRICT" "/p:OfficialBuild=$env:TEST_OFFICIAL"
            exit $LASTEXITCODE
            """);

        using var command = CreateCommand(script, RepoRoot.Path)
            .WithEnvironmentVariable("WarningsNotAsErrors", "CS1591")
            .WithEnvironmentVariable("TEST_STRICT", strict.ToString())
            .WithEnvironmentVariable("TEST_OFFICIAL", official.ToString());
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();

        using var document = JsonDocument.Parse(result.Output);
        var properties = document.RootElement.GetProperty("Properties");
        Assert.Equal(
            strict ? ["CS1591"] : new[] { "CS1591", "NU1901", "NU1902", "NU1903", "NU1904" },
            SplitWarnings(properties.GetProperty("WarningsNotAsErrors").GetString()!));
        Assert.Equal("all", properties.GetProperty("NuGetAuditMode").GetString());
        Assert.Equal("true", properties.GetProperty("TreatWarningsAsErrors").GetString());
        if (official)
        {
            Assert.Equal("false", properties.GetProperty("NuGetAudit").GetString());
        }
    }

    [Theory]
    [InlineData("pwsh", "eng/build.ps1", "default")]
    [InlineData("bash", "build.sh", "default")]
    [InlineData("bash", "restore.sh", "default")]
    [InlineData("pwsh", "eng/build.ps1", "default-actions")]
    [InlineData("bash", "build.sh", "default-actions")]
    [InlineData("bash", "restore.sh", "default-actions")]
    [InlineData("pwsh", "eng/build.ps1", "strict")]
    [InlineData("bash", "build.sh", "strict")]
    [InlineData("bash", "restore.sh", "strict")]
    [InlineData("pwsh", "eng/build.ps1", "additional")]
    [InlineData("bash", "build.sh", "additional")]
    [InlineData("bash", "restore.sh", "additional")]
    [InlineData("pwsh", "eng/build.ps1", "context")]
    [InlineData("bash", "build.sh", "context")]
    [InlineData("pwsh", "eng/build.ps1", "mapped-property")]
    [InlineData("bash", "build.sh", "mapped-property")]
    [InlineData("pwsh", "eng/build.ps1", "property-alias")]
    [InlineData("bash", "build.sh", "property-alias")]
    [InlineData("pwsh", "eng/build.ps1", "disabled")]
    [InlineData("bash", "build.sh", "disabled")]
    [InlineData("pwsh", "eng/build.ps1", "environment-disabled")]
    [InlineData("bash", "build.sh", "environment-disabled")]
    [InlineData("pwsh", "eng/build.ps1", "unrelated")]
    [InlineData("bash", "build.sh", "unrelated")]
    [InlineData("pwsh", "eng/build.ps1", "empty")]
    [InlineData("bash", "build.sh", "empty")]
    [InlineData("pwsh", "eng/build.ps1", "invalid-policy")]
    [InlineData("bash", "build.sh", "invalid-policy")]
    [InlineData("pwsh", "eng/build.ps1", "clean")]
    [InlineData("bash", "build.sh", "clean")]
    [InlineData("bash", "restore.sh", "clean")]
    [InlineData("pwsh", "eng/build.ps1", "clean-with-build")]
    [InlineData("bash", "build.sh", "clean-with-build")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BuildEntryPointForwardsEvaluatedPolicyToStandaloneMSBuild(string shell, string entryPoint, string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("repository with spaces").FullName;
        var common = Directory.CreateDirectory(Path.Combine(root, "eng", "common")).FullName;
        foreach (var file in new[] { "build.ps1", "build.sh", "WarningPolicy.proj" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", file), Path.Combine(root, "eng", file));
        }

        foreach (var file in new[] { "build.sh", "restore.sh" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, file), Path.Combine(root, file));
        }

        var clean = scenario is "clean" or "clean-with-build";
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), scenario == "invalid-policy" || clean ? "<Project>" : """
            <Project>
              <PropertyGroup>
                <WarningsNotAsErrors>$(WarningsNotAsErrors);$(AdditionalWarningsNotAsErrors)</WarningsNotAsErrors>
                <WarningsNotAsErrors Condition="'$(TreatNuGetAuditWarningsAsErrors)' != 'true'">$(WarningsNotAsErrors);TST1001</WarningsNotAsErrors>
                <WarningsNotAsErrors Condition="'$(Configuration)' == 'Release' and '$(TargetArchitecture)' == 'arm64' and '$(TargetOS)' == 'linux' and '$(ContinuousIntegrationBuild)' == 'true'">$(WarningsNotAsErrors);CFG1001</WarningsNotAsErrors>
                <WarningsNotAsErrors Condition="'$(VSTestNoBuild)' == 'true'">$(WarningsNotAsErrors);MAP1001</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """);
        // Like Arcade's NuGet.targets restore, this project deliberately does not
        // import Directory.Build.props. Only the command-line exemption can help.
        await File.WriteAllTextAsync(Path.Combine(root, "eng", "Logger.proj"), """
            <Project>
              <Target Name="Build">
                <Warning Code="$(TEST_WARNING_CODE)" Text="Synthetic build warning" />
              </Target>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(common, "tools.ps1"), """
            function InitializeDotNetCli([bool]$install) {
              if (!$install) { throw 'Expected SDK initialization before evaluation.' }
              Set-Content $env:TEST_BOOTSTRAP 'initialized'
              if ($env:TEST_CLEAN -eq 'true') { throw 'Clean must not initialize the SDK.' }
              Write-Host 'SDK initialized'
              Split-Path $env:TEST_DOTNET
            }
            function GetExecutableFileName($name) {
              if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { "$name.exe" } else { $name }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(common, "tools.sh"), """
            eng_root="$scriptroot"
            function InitializeDotNetCli {
              [[ "$1" == "true" ]] || exit 1
              printf 'initialized\n' > "$TEST_BOOTSTRAP"
              if [[ "$TEST_CLEAN" == "true" ]]; then
                echo 'Clean must not initialize the SDK.' >&2
                exit 1
              fi
              echo 'SDK initialized'
              _InitializeDotNetCli="$(dirname "$TEST_DOTNET")"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(common, "build.ps1"), """
            [CmdletBinding(PositionalBinding=$false)]
            param(
              [switch]$restore, [switch]$build, [switch]$ci, [switch]$clean,
              [string]$configuration = 'Debug', [string]$projects, [string]$verbosity,
              [bool]$warnAsError = $true, [string]$warnNotAsError = '',
              [Parameter(ValueFromRemainingArguments=$true)][string[]]$properties = @()
            )
            [IO.File]::WriteAllLines($env:TEST_REPORT, @(
              $warnNotAsError, "$warnAsError".ToLowerInvariant(), "$($restore.IsPresent)".ToLowerInvariant(),
              "$($build.IsPresent)".ToLowerInvariant(), "$($clean.IsPresent)".ToLowerInvariant(),
              $projects, $configuration
            ) + @($properties))
            if ($clean) { exit 0 }
            $msbuildArgs = @('msbuild', 'eng/Logger.proj', '-nologo', '-m', '-v:quiet')
            if ($warnAsError) { $msbuildArgs += '-warnaserror' }
            if ($warnNotAsError) { $msbuildArgs += "-warnnotaserror:$warnNotAsError" }
            & $env:TEST_DOTNET @msbuildArgs
            exit $LASTEXITCODE
            """);
        var commonBash = Path.Combine(common, "build.sh");
        await File.WriteAllTextAsync(commonBash, """
            #!/usr/bin/env bash
            set -euo pipefail
            warnings=''
            warn_as_error=true
            restore=false
            build=false
            clean=false
            projects=''
            config=Debug
            properties=()
            while [[ $# -gt 0 ]]; do
              case "$1" in
                -restore|--restore) restore=true; shift ;;
                -build) build=true; shift ;;
                -clean) clean=true; shift ;;
                -ci) shift ;;
                -warnAsError) warn_as_error="$2"; shift 2 ;;
                -warnNotAsError) warnings="$2"; shift 2 ;;
                -projects) projects="$2"; shift 2 ;;
                -configuration) config="$2"; shift 2 ;;
                *) properties+=("$1"); shift ;;
              esac
            done
            printf '%s\n' "$warnings" "$warn_as_error" "$restore" "$build" "$clean" "$projects" "$config" > "$TEST_REPORT"
            if [[ ${#properties[@]} -gt 0 ]]; then
              printf '%s\n' "${properties[@]}" >> "$TEST_REPORT"
            fi
            if [[ "$clean" == true ]]; then exit 0; fi
            msbuild_args=(msbuild eng/Logger.proj -nologo -m -v:quiet)
            if [[ "$warn_as_error" == true ]]; then msbuild_args+=(-warnaserror); fi
            if [[ -n "$warnings" ]]; then msbuild_args+=("-warnnotaserror:$warnings"); fi
            "$TEST_DOTNET" "${msbuild_args[@]}"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(commonBash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(root, "eng", "build.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var arguments = new List<string> { "-projects", "a project with spaces.csproj" };
        if (entryPoint != "restore.sh")
        {
            arguments.Insert(0, "-restore");
        }
        var expectedWarnings = new List<string> { "CS1591", "TST1001" };
        var expectedProperties = new List<string>();
        var expectedExitCode = 0;
        switch (scenario)
        {
            case "default-actions":
                arguments.Clear();
                break;
            case "clean":
            case "clean-with-build":
                arguments = [shell == "bash" ? "--clean" : "-clean"];
                if (scenario == "clean-with-build")
                {
                    arguments.AddRange(["-restore", "-build"]);
                }
                expectedWarnings.Clear();
                break;
            case "strict":
            case "empty":
                arguments.Add("/p:TreatNuGetAuditWarningsAsErrors=true");
                expectedProperties.Add(arguments[^1]);
                expectedWarnings = scenario == "empty" ? [] : ["CS1591"];
                expectedExitCode = 1;
                break;
            case "additional":
                arguments.AddRange(["-warnNotAsError", "; USR1001 ;;USR1002;TST1001;", "-property:AdditionalWarningsNotAsErrors=PROP1001"]);
                expectedProperties.Add(shell == "pwsh" ? "/p:AdditionalWarningsNotAsErrors=PROP1001" : arguments[^1]);
                expectedWarnings = ["CS1591", "PROP1001", "TST1001", "USR1001", "USR1002"];
                break;
            case "property-alias":
                arguments.Add(shell == "pwsh" ? "/property:AdditionalWarningsNotAsErrors=PROP1001" : "-p:AdditionalWarningsNotAsErrors=PROP1001");
                expectedProperties.Add(arguments[^1]);
                expectedWarnings = ["CS1591", "PROP1001", "TST1001"];
                break;
            case "context":
                arguments.AddRange(["-configuration", "Release", "-arch", "arm64", "-os", "linux", "-ci"]);
                expectedProperties.AddRange(["/p:TargetArchitecture=arm64", "/p:TargetOS=linux"]);
                expectedWarnings.Add("CFG1001");
                break;
            case "mapped-property":
                arguments.Add("-testnobuild");
                expectedProperties.Add("/p:VSTestNoBuild=true");
                expectedWarnings.Add("MAP1001");
                break;
            case "disabled":
                arguments.AddRange(["-warnAsError", "false"]);
                expectedWarnings.Clear();
                break;
            case "environment-disabled":
                expectedWarnings.Clear();
                break;
            case "unrelated":
            case "invalid-policy":
                expectedExitCode = 1;
                break;
        }

        var harness = Path.Combine(root, "run.ps1");
        await File.WriteAllTextAsync(harness, """
            $scriptArgs = @($env:TEST_ARGUMENTS | ConvertFrom-Json)
            if ($env:TEST_SHELL -eq 'pwsh') {
              & pwsh -NoProfile -File $env:TEST_ENTRY_POINT @scriptArgs
            } else {
              & bash $env:TEST_ENTRY_POINT @scriptArgs
            }
            exit $LASTEXITCODE
            """);
        var report = Path.Combine(root, "report.txt");
        var bootstrap = Path.Combine(root, "bootstrap.txt");
        using var command = CreateCommand(harness, root)
            .WithEnvironmentVariable("TEST_SHELL", shell)
            .WithEnvironmentVariable("TEST_ENTRY_POINT", entryPoint)
            .WithEnvironmentVariable("TEST_CLEAN", clean ? "true" : "false")
            .WithEnvironmentVariable("TEST_ARGUMENTS", JsonSerializer.Serialize(arguments))
            .WithEnvironmentVariable("TEST_REPORT", report.Replace('\\', '/'))
            .WithEnvironmentVariable("TEST_BOOTSTRAP", bootstrap.Replace('\\', '/'))
            .WithEnvironmentVariable("TEST_WARNING_CODE", scenario == "unrelated" ? "TST9999" : "TST1001")
            .WithEnvironmentVariable("TreatWarningsAsErrors", scenario == "environment-disabled" ? "false" : "true")
            .WithEnvironmentVariable("WarningsNotAsErrors", scenario == "empty" ? "" : "CS1591");
        var result = await command.ExecuteAsync();
        result.EnsureExitCode(expectedExitCode);

        var warningsAsErrors = scenario is not ("disabled" or "environment-disabled");
        Assert.Equal(warningsAsErrors && !clean, File.Exists(bootstrap));
        if (scenario == "invalid-policy")
        {
            Assert.False(File.Exists(report), "A failed policy evaluation must not start the build.");
            Assert.Contains("Could not evaluate the repository warning policy", result.Output);
            return;
        }

        var values = await File.ReadAllLinesAsync(report);
        Assert.Equal(expectedWarnings, SplitWarnings(values[0]));
        Assert.Equal(
            new[]
            {
                warningsAsErrors ? "true" : "false",
                scenario != "clean" || entryPoint == "restore.sh" ? "true" : "false",
                scenario == "clean-with-build" || (scenario == "default-actions" && entryPoint != "restore.sh") ? "true" : "false",
                clean ? "true" : "false",
                clean || scenario == "default-actions" ? "" : "a project with spaces.csproj",
                scenario == "context" ? "Release" : "Debug",
            }
                .Concat(expectedProperties),
            values.Skip(1));
    }

    private PowerShellCommand CreateCommand(string script, string workingDirectory)
    {
        var dotnet = Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(dotnet), "The repository SDK must be initialized before running infrastructure tests.");

        return new PowerShellCommand(script, output)
            .WithWorkingDirectory(workingDirectory)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("TEST_DOTNET", dotnet.Replace('\\', '/'))
            .WithEnvironmentVariable("TEST_REPO", RepoRoot.Path)
            .WithEnvironmentVariable("MSBUILDTERMINALLOGGER", "false")
            .WithEnvironmentVariable("MSYS2_ARG_CONV_EXCL", "/p:;/property:")
            .WithEnvironmentVariable("TreatWarningsAsErrors", "true")
            .WithEnvironmentVariable("TreatNuGetAuditWarningsAsErrors", "")
            .WithEnvironmentVariable("AdditionalWarningsNotAsErrors", "")
            .WithEnvironmentVariable("MSBuildWarningsNotAsErrors", "")
            .WithEnvironmentVariable("MSBuildTreatWarningsAsErrors", "");
    }

    private static string[] SplitWarnings(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
