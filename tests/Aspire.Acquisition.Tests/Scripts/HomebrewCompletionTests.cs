// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

[RequiresTools(["pwsh"])]
public class HomebrewCompletionTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PowerShellLoader_OnlyEvaluatesSuccessfulGeneration(int cliExitCode)
    {
        using var environment = new TestEnvironment();
        var repoRoot = TestUtils.FindRepoRoot()!.FullName;
        var template = await File.ReadAllTextAsync(Path.Combine(repoRoot, "eng", "homebrew", "aspire.rb.template"));
        // The cask embeds the loader as: generated_script "...", content: <<~'POWERSHELL'.
        // Exercise that body rather than maintaining a second copy of the installed script.
        var match = Regex.Match(template,
            """(?s)generated_script "aspire-completion.ps1", content: <<~'POWERSHELL'\r?\n(?<script>.*?)\r?\n  POWERSHELL""");
        Assert.True(match.Success, "PowerShell loader heredoc was not found in the cask template.");
        var loaderPath = Path.Combine(environment.TempDirectory, "loader.ps1");
        await File.WriteAllTextAsync(loaderPath, match.Groups["script"].Value);
        var testPath = Path.Combine(environment.TempDirectory, "test.ps1");
        await File.WriteAllTextAsync(testPath, $$"""
            $ErrorActionPreference = 'Stop'
            $global:CompletionLoaded = $false
            function aspire {
                if (($args -join ' ') -ne 'completions script pwsh') { throw 'Unexpected CLI arguments' }
                $global:LASTEXITCODE = {{cliExitCode}}
                '$global:CompletionLoaded = $true'
            }
            $failed = $false
            try { . $env:ASPIRE_COMPLETION_LOADER } catch [System.Management.Automation.RuntimeException] {
                if ($_.Exception.Message -ne 'Aspire could not generate PowerShell completions.') { throw }
                $failed = $true
            }
            if ($failed -ne ${{(cliExitCode == 0 ? "false" : "true")}}) { throw 'Unexpected loader failure state' }
            if ($global:CompletionLoaded -ne ${{(cliExitCode == 0 ? "true" : "false")}}) { throw 'Unexpected script evaluation' }
            'loader complete'
            """);

        using var command = new ToolCommand("pwsh", testOutput)
            .WithWorkingDirectory(environment.TempDirectory)
            .WithEnvironmentVariable("ASPIRE_COMPLETION_LOADER", loaderPath)
            .WithTimeout(TimeSpan.FromSeconds(30));
        var result = await command.ExecuteAsync("-NoLogo", "-NoProfile", "-NonInteractive", "-File", $"\"{testPath}\"");

        result.EnsureSuccessful();
        Assert.Equal("loader complete", result.Output.Trim());
    }
}
