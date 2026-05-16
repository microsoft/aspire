// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
    public async Task RunAndStartReportSyntaxErrorsForDotNetAndTypeScriptAppHosts()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);

            await auto.AspireNewAsync("BrokenDotNetApp", counter, template: AspireTemplate.EmptyAppHost);
            await auto.AspireNewAsync("BrokenTypeScriptApp", counter, template: AspireTemplate.TypeScriptEmptyAppHost);

            WriteBrokenDotNetAppHost(Path.Combine(workspace.WorkspaceRoot.FullName, "BrokenDotNetApp"));
            WriteBrokenTypeScriptAppHost(Path.Combine(workspace.WorkspaceRoot.FullName, "BrokenTypeScriptApp"));

            var outputsDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "outputs");
            Directory.CreateDirectory(outputsDirectory);

            await RunAspireCommandToOutputFileAsync(auto, counter, "BrokenDotNetApp", "aspire run --apphost BrokenDotNetApp.csproj", "dotnet-run", timeout: TimeSpan.FromMinutes(2));
            await RunAspireCommandToOutputFileAsync(auto, counter, "BrokenDotNetApp", "aspire start --apphost BrokenDotNetApp.csproj", "dotnet-start", timeout: TimeSpan.FromMinutes(2));
            await RunAspireCommandToOutputFileAsync(auto, counter, "BrokenTypeScriptApp", "aspire run", "typescript-run", timeout: TimeSpan.FromMinutes(3));
            await RunAspireCommandToOutputFileAsync(auto, counter, "BrokenTypeScriptApp", "aspire start", "typescript-start", timeout: TimeSpan.FromMinutes(3));

            AssertDotNetRunOutput(ReadCommandOutput(outputsDirectory, "dotnet-run"));
            AssertDotNetStartOutput(ReadCommandOutput(outputsDirectory, "dotnet-start"));
            AssertTypeScriptRunOutput(ReadCommandOutput(outputsDirectory, "typescript-run"));
            AssertTypeScriptStartOutput(ReadCommandOutput(outputsDirectory, "typescript-start"));
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

    private static async Task RunAspireCommandToOutputFileAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string workingDirectory,
        string command,
        string outputName,
        TimeSpan timeout)
    {
        var quotedWorkingDirectory = AspireCliShellCommandHelpers.QuoteBashArg(workingDirectory);
        var quotedOutputPath = AspireCliShellCommandHelpers.QuoteBashArg($"../outputs/{outputName}.out");
        var quotedExitCodePath = AspireCliShellCommandHelpers.QuoteBashArg($"../outputs/{outputName}.exit");
        var timeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds);

        await auto.RunCommandAsync(
            $"(cd {quotedWorkingDirectory} && timeout --kill-after=5s {timeoutSeconds}s {command} > {quotedOutputPath} 2>&1; printf '%s\\n' \"$?\" > {quotedExitCodePath})",
            counter,
            timeout + TimeSpan.FromSeconds(10));
    }

    private static CommandOutput ReadCommandOutput(string outputsDirectory, string outputName)
    {
        return new CommandOutput(
            File.ReadAllText(Path.Combine(outputsDirectory, $"{outputName}.out")),
            int.Parse(File.ReadAllText(Path.Combine(outputsDirectory, $"{outputName}.exit")).Trim(), CultureInfo.InvariantCulture));
    }

    private static void AssertDotNetRunOutput(CommandOutput output)
    {
        Assert.Equal(6, output.ExitCode);
        Assert.Contains("error CS1002: ; expected", output.Text);
        Assert.Contains("Build FAILED.", output.Text);
        Assert.Contains("The project could not be built.", output.Text);
        Assert.DoesNotContain(RunCommandStrings.RecentAppHostStartupOutput, output.Text);
    }

    private static void AssertDotNetStartOutput(CommandOutput output)
    {
        Assert.Equal(2, output.ExitCode);
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, output.Text);
        Assert.Contains(RunCommandStrings.RecentAppHostStartupOutput, output.Text);
        Assert.Contains("error CS1002: ; expected", output.Text);
        Assert.Contains("Build FAILED.", output.Text);
        Assert.Contains(RunCommandStrings.AppHostFailedToBuild, output.Text);
    }

    private static void AssertTypeScriptRunOutput(CommandOutput output)
    {
        Assert.Equal(2, output.ExitCode);
        Assert.Contains("apphost.ts(1,15): error TS1109: Expression expected.", output.Text);
        Assert.Contains("The TypeScript (Node.js) apphost failed.", output.Text);
        Assert.DoesNotContain(RunCommandStrings.RecentAppHostStartupOutput, output.Text);
        Assert.DoesNotContain("Executing:", output.Text);
    }

    private static void AssertTypeScriptStartOutput(CommandOutput output)
    {
        Assert.Equal(2, output.ExitCode);
        Assert.Contains(RunCommandStrings.FailedToStartAppHost, output.Text);
        Assert.Contains(RunCommandStrings.RecentAppHostStartupOutput, output.Text);
        Assert.Contains("apphost.ts(1,15): error TS1109: Expression expected.", output.Text);
        Assert.Contains("AppHost process exited with code 2.", output.Text);
        Assert.DoesNotContain("Executing:", output.Text);
        Assert.DoesNotContain("audited", output.Text);
        Assert.DoesNotContain("funding", output.Text);
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

    private sealed record CommandOutput(string Text, int ExitCode);
}
