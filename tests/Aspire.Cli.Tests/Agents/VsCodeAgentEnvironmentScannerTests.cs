// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class VsCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_UsesStableBeforeFallingBackToInsiders(bool stableInstalled, bool insidersInstalled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            VsCodeVersion = stableInstalled ? new SemVersion(1, 100, 0) : null,
            VsCodeInsidersVersion = insidersInstalled ? SemVersion.Parse("1.101.0-insider", SemVersionStyles.Strict) : null
        };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        AgentEnvironmentDetection? expected = stableInstalled ? new("1.100.0", false)
            : insidersInstalled ? new("1.101.0-insider", true) : null;
        Assert.Equal(expected, detections);
        Assert.Equal(stableInstalled ? ["code"] : ["code", "code-insiders"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));

    }

    [Fact]
    public async Task ScanAsync_WithProjectConfiguration_DoesNotProbeInstalledEditions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".vscode");
        var runner = new TestAgentCliRunner
        {
            VsCodeInsidersVersion = SemVersion.Parse("1.101.0-insider", SemVersionStyles.Strict)
        };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentEnvironmentDetection(null, false), detections);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("  ", null, false)]
    [InlineData("1.100.0", "1.100.0", false)]
    [InlineData(" 1.100.0 ", "1.100.0", false)]
    [InlineData("1.101.0-insider", "1.101.0-insider", true)]
    public async Task ScanAsync_WhenInVsCode_RecordsTerminalEvidenceWithoutProbing(string? terminalVersion, string? expectedVersion, bool isInsiders)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) =>
            {
                throw new InvalidOperationException("Editor probes must not run in a VS Code terminal.");
            }
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["TERM_PROGRAM_VERSION"] = terminalVersion
        });
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), environment);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentEnvironmentDetection(expectedVersion, isInsiders), detections);
        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public async Task ScanAsync_WithProjectConfigurationInInsidersTerminal_PreservesNativeUserEditionWithoutProbing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".vscode");
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) => throw new InvalidOperationException("Project evidence must avoid CLI probes.")
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["TERM_PROGRAM_VERSION"] = "1.101.0-insider"
        });
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), environment);

        var detections = await agent.ScanAsync(workspace.WorkspaceRoot, workspace.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentEnvironmentDetection("1.101.0-insider", true), detections);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ScanAsync_WhenCancelledByStableProbe_DoesNotProbeInsiders()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) =>
            {
                cancellationSource.Cancel();
                return Task.FromResult<SemVersion?>(new SemVersion(1, 100, 0));
            }
        };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.ScanAsync(directories.WorkingDirectory, directories.WorkspaceRoot, cancellationSource.Token)).DefaultTimeout();

        Assert.Equal(["code"], runner.Commands);
    }

    private static VsCodeAgentEnvironmentScanner CreateAgent(
        TestAgentCliRunner runner,
        CliExecutionContext executionContext,
        TestEnvironment environment)
        => new(runner, executionContext, environment, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);

    private static (DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot) CreateScanDirectories(DirectoryInfo workingDirectory, DirectoryInfo repositoryRoot)
        => (workingDirectory, repositoryRoot);
}
