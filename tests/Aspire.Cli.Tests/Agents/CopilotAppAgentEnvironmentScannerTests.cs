// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.CopilotApp;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class CopilotAppAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ScanAsync_WhenWindowsAppExecutableExists_DetectsCopilotApp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localAppData = workspace.CreateDirectory("local-app-data");
        var appDirectory = Directory.CreateDirectory(
            Path.Combine(localAppData.FullName, "Programs", "GitHub Copilot"));
        await File.WriteAllTextAsync(
            Path.Combine(appDirectory.FullName, "github.exe"),
            string.Empty,
            TestContext.Current.CancellationToken);
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = localAppData.FullName,
        });
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await CreateScanner(environment, workspace).ScanAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal([AgentClientKind.CopilotApp], context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WhenUserMacOSAppBundleExists_DetectsCopilotApp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Directory.CreateDirectory(
            Path.Combine(workspace.WorkspaceRoot.FullName, "Applications", "GitHub Copilot.app"));
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await CreateScanner(TestEnvironment.CreateMacOS(), workspace).ScanAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal([AgentClientKind.CopilotApp], context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WhenRuntimeMarkerIsPresent_DetectsPortableCopilotApp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [CopilotAppInstallationDetector.AgentEnvironmentVariable] =
                CopilotAppInstallationDetector.AgentEnvironmentValue,
        });
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await CreateScanner(environment, workspace).ScanAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal([AgentClientKind.CopilotApp], context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WhenLinuxDesktopEntryExists_DetectsCopilotApp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applicationsDirectory = workspace.CreateDirectory(
            Path.Combine(".local", "share", "applications"));
        await File.WriteAllTextAsync(
            Path.Combine(applicationsDirectory.FullName, "github-copilot.desktop"),
            """
            [Desktop Entry]
            Name=GitHub Copilot
            Exec=/opt/github-copilot/github
            """,
            TestContext.Current.CancellationToken);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await CreateScanner(TestEnvironment.CreateLinux(), workspace).ScanAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal([AgentClientKind.CopilotApp], context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WithoutInstallationOrRuntimeMarker_DoesNotDetectCopilotApp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await CreateScanner(TestEnvironment.CreateLinux(), workspace).ScanAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Empty(context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    private static CopilotAppAgentEnvironmentScanner CreateScanner(
        IEnvironment environment,
        TemporaryWorkspace workspace)
    {
        return new(
            new CopilotAppInstallationDetector(
                environment,
                TestExecutionContextHelper.CreateExecutionContext(
                    workspace.WorkspaceRoot,
                    homeDirectory: workspace.WorkspaceRoot)),
            NullLogger<CopilotAppAgentEnvironmentScanner>.Instance);
    }

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo workingDirectory)
    {
        return new AgentEnvironmentScanContext
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = workingDirectory,
        };
    }
}
