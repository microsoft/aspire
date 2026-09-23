// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class MicrosoftTestingPlatformTests(ITestOutputHelper output)
{
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
