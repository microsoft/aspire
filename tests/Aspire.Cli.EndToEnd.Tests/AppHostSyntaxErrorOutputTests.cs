// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
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
            outputExpectation: s_dotNetRunOutputExpectation,
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
            outputExpectation: s_dotNetStartOutputExpectation,
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
            outputExpectation: s_typeScriptRunOutputExpectation,
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
            outputExpectation: s_typeScriptStartOutputExpectation,
            timeout: TimeSpan.FromMinutes(3));
    }

    private async Task RunSyntaxErrorScenarioAsync(
        string projectName,
        AspireTemplate template,
        Action<string> configureProject,
        string command,
        int expectedExitCode,
        CommandOutputExpectation outputExpectation,
        TimeSpan timeout,
        [CallerMemberName] string testName = "")
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var recordingPath = CliE2ETestHelpers.GetTestResultsRecordingPath(testName);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace,
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
                outputExpectation,
                recordingPath,
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
        CommandOutputExpectation outputExpectation,
        string recordingPath,
        TimeSpan timeout)
    {
        var quotedWorkingDirectory = AspireCliShellCommandHelpers.QuoteBashArg(workingDirectory);
        await auto.RunCommandAsync($"cd \"$ASPIRE_E2E_WORKSPACE\"/{quotedWorkingDirectory}", counter, TimeSpan.FromSeconds(10));
        await auto.ClearScreenAsync(counter);

        var recordingOffset = File.Exists(recordingPath) ? new FileInfo(recordingPath).Length : 0;
        await auto.TypeAsync(command);
        await auto.EnterAsync();

        var expectedCounter = counter.Value;
        var errorPromptSearcher = new CellPatternSearcher()
            .FindPattern(expectedCounter.ToString(CultureInfo.InvariantCulture))
            .RightText($" ERR:{expectedExitCode}] $ ");

        await auto.WaitUntilAsync(snapshot =>
        {
            if (errorPromptSearcher.Search(snapshot).Count == 0)
            {
                return false;
            }

            return true;
        }, timeout, description: $"waiting for '{command}' to fail with exit code {expectedExitCode}");
        counter.Increment();

        AssertTerminalRecording(ReadRecordingText(recordingPath, recordingOffset), outputExpectation);
    }

    private static string ReadRecordingText(string recordingPath, long recordingOffset)
    {
        using var stream = new FileStream(recordingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = recordingOffset;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertTerminalRecording(string terminalRecording, CommandOutputExpectation outputExpectation)
    {
        foreach (var text in outputExpectation.RequiredText)
        {
            Assert.Contains(text, terminalRecording);
        }

        foreach (var text in outputExpectation.ForbiddenText)
        {
            Assert.DoesNotContain(text, terminalRecording);
        }
    }

    private static readonly CommandOutputExpectation s_dotNetRunOutputExpectation = new(
        RequiredText:
        [
            "error CS1002: ; expected",
            "Build FAILED.",
            "The project could not be built."
        ],
        ForbiddenText:
        [
            RunCommandStrings.RecentAppHostStartupOutput
        ]);

    private static readonly CommandOutputExpectation s_dotNetStartOutputExpectation = new(
        RequiredText:
        [
            RunCommandStrings.FailedToStartAppHost,
            RunCommandStrings.RecentAppHostStartupOutput,
            "error CS1002: ; expected",
            "Build FAILED.",
            RunCommandStrings.AppHostFailedToBuild
        ]);

    private static readonly CommandOutputExpectation s_typeScriptRunOutputExpectation = new(
        RequiredText:
        [
            "apphost.mts(1,15): error TS1109: Expression expected.",
            "The TypeScript (Node.js) apphost failed."
        ],
        ForbiddenText:
        [
            RunCommandStrings.RecentAppHostStartupOutput,
            "Executing:"
        ]);

    private static readonly CommandOutputExpectation s_typeScriptStartOutputExpectation = new(
        RequiredText:
        [
            RunCommandStrings.FailedToStartAppHost,
            RunCommandStrings.RecentAppHostStartupOutput,
            "apphost.mts(1,15): error TS1109: Expression expected.",
            "AppHost process exited with code 2."
        ],
        ForbiddenText:
        [
            "Executing:",
            "audited",
            "funding"
        ]);

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
        File.WriteAllText(Path.Combine(projectDirectory, "apphost.mts"), "const value = ;");
    }

    private sealed record CommandOutputExpectation(string[] RequiredText, string[] ForbiddenText)
    {
        public CommandOutputExpectation(string[] RequiredText)
            : this(RequiredText, [])
        {
        }
    }
}
