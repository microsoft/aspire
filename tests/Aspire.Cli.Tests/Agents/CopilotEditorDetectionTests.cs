// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class CopilotEditorDetectionTests(ITestOutputHelper outputHelper)
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
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await agent.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        AgentClientDetection[] expected = stableInstalled ? [new(AgentClientKind.VsCode, "1.100.0", false)]
            : insidersInstalled ? [new(AgentClientKind.VsCode, "1.101.0-insider", true)] : [];
        Assert.Equal(expected, context.DetectedClients);
        Assert.Equal(stableInstalled ? ["copilot", "code"] : ["copilot", "code", "code-insiders"], runner.Commands);
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
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await agent.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentClientDetection(AgentClientKind.VsCode, null, false), Assert.Single(context.DetectedClients));
        Assert.Equal(["copilot"], runner.Commands);
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
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await agent.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentClientDetection(AgentClientKind.VsCode, expectedVersion, isInsiders), Assert.Single(context.DetectedClients));
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
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await agent.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentClientDetection(AgentClientKind.VsCode, "1.101.0-insider", true), Assert.Single(context.DetectedClients));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ScanAsync_WhenCancelledByStableProbe_DoesNotProbeInsiders()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (command, _) =>
            {
                if (command == "copilot")
                {
                    return Task.FromResult<SemVersion?>(null);
                }

                cancellationSource.Cancel();
                return Task.FromResult<SemVersion?>(new SemVersion(1, 100, 0));
            }
        };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.ScanAsync(context, cancellationSource.Token)).DefaultTimeout();

        Assert.Equal(["copilot", "code"], runner.Commands);
    }

    private static CopilotAgentEnvironmentScanner CreateAgent(
        TestAgentCliRunner runner,
        CliExecutionContext executionContext,
        TestEnvironment environment)
        => new(runner, new CopilotAppInstallationDetector(environment, executionContext), runner,
            executionContext, environment, NullLogger<CopilotAgentEnvironmentScanner>.Instance);
}
