// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests.ConditionalSelection;

public class TemplateSelectionPropertyTests
{
    [Fact]
    public async Task ConditionalSelectionTemplateFlag_IsEnabled_WhenTemplateProjectIsNotDirectlyAffected()
    {
        var result = await EvaluateTemplateProjectAsync("tests/Some.Other.Tests/Some.Other.Tests.csproj");

        Assert.Equal("true", result.ConditionalSelectionRunOnlyBasicBuildTemplateScenarios);
        Assert.Contains("category=basic-build", result.TestRunnerAdditionalArguments);
    }

    [Fact]
    public async Task ConditionalSelectionTemplateFlag_IsDisabled_WhenTemplateProjectIsDirectlyAffected()
    {
        var result = await EvaluateTemplateProjectAsync("tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj");

        Assert.Equal("false", result.ConditionalSelectionRunOnlyBasicBuildTemplateScenarios);
        Assert.DoesNotContain("category=basic-build", result.TestRunnerAdditionalArguments);
    }

    private static async Task<TemplateProjectEvaluationProperties> EvaluateTemplateProjectAsync(string directlyAffectedTestProjects)
    {
        var repoRoot = FindRepoRoot();
        var dotnetScriptPath = Path.Combine(repoRoot.FullName, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.cmd" : "dotnet.sh");
        var projectPath = Path.Combine(repoRoot.FullName, "tests", "Aspire.Templates.Tests", "Aspire.Templates.Tests.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetScriptPath,
            Arguments = $"msbuild \"{projectPath}\" -nologo -tl:false -getProperty:ConditionalSelectionRunOnlyBasicBuildTemplateScenarios,TestRunnerAdditionalArguments -p:DirectlyAffectedTestProjects=\"{directlyAffectedTestProjects}\"",
            WorkingDirectory = repoRoot.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet msbuild.");
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);

        Assert.True(process.ExitCode == 0, $"dotnet msbuild failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdOut}{Environment.NewLine}STDERR:{Environment.NewLine}{stdErr}");

        var evaluation = JsonSerializer.Deserialize<TemplateProjectEvaluation>(stdOut);
        Assert.NotNull(evaluation);
        Assert.NotNull(evaluation.Properties);

        return evaluation.Properties;
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aspire.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class TemplateProjectEvaluation
    {
        public required TemplateProjectEvaluationProperties Properties { get; init; }
    }

    private sealed class TemplateProjectEvaluationProperties
    {
        public required string ConditionalSelectionRunOnlyBasicBuildTemplateScenarios { get; init; }

        public required string TestRunnerAdditionalArguments { get; init; }
    }
}
