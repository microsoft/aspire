// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class MicrosoftTestingPlatformTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "20m")]
    [InlineData(true, "30m")]
    [RequiresTools(["pwsh"])]
    public async Task ProjectTimeoutOverridesFlowToMtpArguments(bool basicBuild, string expectedSessionTimeout)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var script = Path.Combine(workspace.Path, "evaluate.ps1");
        await File.WriteAllTextAsync(script, """
            & $env:TEST_DOTNET msbuild (Join-Path $env:TEST_REPO 'tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj') -nologo `
              -getProperty:MtpBaseArgs,TestRunnerAdditionalArguments `
              "/p:RunOnlyBasicBuildTemplateTests=$env:TEST_BASIC_BUILD"
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(script, output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithEnvironmentVariable("TEST_DOTNET", Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .WithEnvironmentVariable("TEST_REPO", RepoRoot.Path)
            .WithEnvironmentVariable("TEST_BASIC_BUILD", basicBuild.ToString());
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();

        using var document = JsonDocument.Parse(result.Output);
        var properties = document.RootElement.GetProperty("Properties");
        Assert.Equal(
            $"--ignore-exit-code 8 --crashdump --hangdump --hangdump-type none --hangdump-timeout 15m --timeout {expectedSessionTimeout}",
            properties.GetProperty("MtpBaseArgs").GetString());
        var testRunnerAdditionalArguments = properties.GetProperty("TestRunnerAdditionalArguments").GetString()!;
        Assert.Equal(basicBuild, testRunnerAdditionalArguments.Contains("--filter-trait category=basic-build", StringComparison.Ordinal));
        Assert.Contains($"--hangdump-timeout 15m --timeout {expectedSessionTimeout}", testRunnerAdditionalArguments, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ArcadeRunnerOptionsAreRegisteredByTheCurrentTestHost()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var script = Path.Combine(workspace.Path, "runner-help.ps1");
        await File.WriteAllTextAsync(script, """
            & $env:TEST_DOTNET $env:TEST_ASSEMBLY --help
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(script, output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithEnvironmentVariable("TEST_DOTNET", Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .WithEnvironmentVariable("TEST_ASSEMBLY", typeof(MicrosoftTestingPlatformTests).Assembly.Location);
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();

        var target = XDocument.Load(Path.Combine(RepoRoot.Path, "eng", "Xunit3", "Microsoft.Testing.Platform.targets"));
        var arguments = string.Join(" ", target.Descendants("_TestRunnerArgs").Select(element => element.Value));
        var options = Regex.Matches(arguments, @"--[\w-]+").Select(match => match.Value).Distinct().ToArray();
        Assert.NotEmpty(options);
        foreach (var option in options)
        {
            Assert.Matches($@"(?m)^\s+{Regex.Escape(option)}\s*$", result.Output);
        }
    }
}
