// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for AppHost syntax-error output.
/// </summary>
public sealed class AppHostSyntaxErrorOutputTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RunReportsSyntaxErrorsForDotNetAppHost()
    {
        return RunSyntaxErrorScenarioAsync(
            projectName: "BrokenDotNetApp",
            template: AspireTemplate.EmptyAppHost,
            configureProject: WriteBrokenDotNetAppHost,
            command: "aspire run --apphost BrokenDotNetApp.csproj",
            expectedExitCode: 6,
            assertOutput: AssertDotNetRunOutput,
            timeout: TimeSpan.FromMinutes(2));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task StartReportsSyntaxErrorsForDotNetAppHost()
    {
        return RunSyntaxErrorScenarioAsync(
            projectName: "BrokenDotNetApp",
            template: AspireTemplate.EmptyAppHost,
            configureProject: WriteBrokenDotNetAppHost,
            command: "aspire start --apphost BrokenDotNetApp.csproj",
            expectedExitCode: 2,
            assertOutput: AssertDotNetStartOutput,
            timeout: TimeSpan.FromMinutes(2));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RunReportsSyntaxErrorsForTypeScriptAppHost()
    {
        return RunSyntaxErrorScenarioAsync(
            projectName: "BrokenTypeScriptApp",
            template: AspireTemplate.TypeScriptEmptyAppHost,
            configureProject: WriteBrokenTypeScriptAppHost,
            command: "aspire run",
            expectedExitCode: 2,
            assertOutput: AssertTypeScriptRunOutput,
            timeout: TimeSpan.FromMinutes(3));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task StartReportsSyntaxErrorsForTypeScriptAppHost()
    {
        return RunSyntaxErrorScenarioAsync(
            projectName: "BrokenTypeScriptApp",
            template: AspireTemplate.TypeScriptEmptyAppHost,
            configureProject: WriteBrokenTypeScriptAppHost,
            command: "aspire start",
            expectedExitCode: 2,
            assertOutput: AssertTypeScriptStartOutput,
            timeout: TimeSpan.FromMinutes(3));
    }

    private async Task RunSyntaxErrorScenarioAsync(
        string projectName,
        AspireTemplate template,
        Action<string> configureProject,
        string command,
        int expectedExitCode,
        Action<string> assertOutput,
        TimeSpan timeout,
        [CallerMemberName] string testName = "")
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace,
            height: 160,
            testName: testName);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);

            await auto.AspireNewAsync(projectName, counter, template: template);
            configureProject(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));

            await AssertAspireCommandOutputAsync(
                auto,
                counter,
                projectName,
                command,
                expectedExitCode,
                assertOutput,
                timeout);
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }

    private static async Task AssertAspireCommandOutputAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string workingDirectory,
        string command,
        int expectedExitCode,
        Action<string> assertOutput,
        TimeSpan timeout)
    {
        var quotedWorkingDirectory = AspireCliShellCommandHelpers.QuoteBashArg(workingDirectory);
        await auto.RunCommandAsync($"cd \"$ASPIRE_E2E_WORKSPACE\"/{quotedWorkingDirectory}", counter, TimeSpan.FromSeconds(10));
        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync(command);
        await auto.EnterAsync();

        var expectedCounter = counter.Value;
        var observedTerminalText = new StringBuilder();
        var previousScreenText = "";
        var errorPromptSearcher = new CellPatternSearcher()
            .FindPattern(expectedCounter.ToString(CultureInfo.InvariantCulture))
            .RightText($" ERR:{expectedExitCode}] $ ");

        await auto.WaitUntilAsync(snapshot =>
        {
            var screenText = snapshot.GetScreenText();
            if (!string.Equals(screenText, previousScreenText, StringComparison.Ordinal))
            {
                observedTerminalText.AppendLine(screenText);
                previousScreenText = screenText;
            }

            if (errorPromptSearcher.Search(snapshot).Count == 0)
            {
                return false;
            }

            return true;
        }, timeout, description: $"waiting for '{command}' to fail with exit code {expectedExitCode}");
        counter.Increment();

        assertOutput(observedTerminalText.ToString());
    }

    private static void AssertDotNetRunOutput(string output)
    {
        Assert.Contains("error CS1002: ; expected", output);
        Assert.Contains("Build FAILED.", output);
        Assert.Contains("The project could not be built.", output);
        Assert.DoesNotContain(RunCommandStrings.RecentAppHostStartupOutput, output);
    }

    private static void AssertDotNetStartOutput(string output)
    {
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, output);
        Assert.Contains(RunCommandStrings.RecentAppHostStartupOutput, output);
        Assert.Contains("error CS1002: ; expected", output);
        Assert.Contains("Build FAILED.", output);
        Assert.Contains(RunCommandStrings.AppHostFailedToBuild, output);
    }

    private static void AssertTypeScriptRunOutput(string output)
    {
        Assert.Contains("apphost.ts(1,15): error TS1109: Expression expected.", output);
        Assert.Contains("The TypeScript (Node.js) apphost failed.", output);
        Assert.DoesNotContain(RunCommandStrings.RecentAppHostStartupOutput, output);
        Assert.DoesNotContain("Executing:", output);
    }

    private static void AssertTypeScriptStartOutput(string output)
    {
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, output);
        Assert.Contains(RunCommandStrings.RecentAppHostStartupOutput, output);
        Assert.Contains("apphost.ts(1,15): error TS1109: Expression expected.", output);
        Assert.Contains("AppHost process exited with code 2.", output);
        Assert.DoesNotContain("Executing:", output);
        Assert.DoesNotContain("audited", output);
        Assert.DoesNotContain("funding", output);
    }

    private static void WriteBrokenDotNetAppHost(string projectDirectory)
    {
        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var aspireSdkVersion = GetAspireSdkVersion(appHostPath);

        File.WriteAllText(Path.Combine(projectDirectory, "BrokenDotNetApp.csproj"), $$"""
            <Project Sdk="Aspire.AppHost.Sdk/{{aspireSdkVersion}}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(appHostPath, """
            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddParameter("example", "value")

            var app = builder.Build();
            await app.RunAsync();
            """);
    }

    private static string GetAspireSdkVersion(string appHostPath)
    {
        var firstLine = File.ReadLines(appHostPath).First();
        const string versionMarker = "Aspire.AppHost.Sdk@";
        var markerIndex = firstLine.IndexOf(versionMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Expected {appHostPath} to start with an Aspire.AppHost.Sdk directive.");

        return firstLine[(markerIndex + versionMarker.Length)..].Trim();
    }

    private static void WriteBrokenTypeScriptAppHost(string projectDirectory)
    {
        File.WriteAllText(Path.Combine(projectDirectory, "apphost.ts"), "const value = ;");
    }
}
