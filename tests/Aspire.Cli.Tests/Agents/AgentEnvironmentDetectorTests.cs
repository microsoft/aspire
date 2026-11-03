// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class AgentEnvironmentDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DetectAsync_WithNoScanners_ReturnsNone()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = new AgentEnvironmentDetector([]);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot,
        };

        var environments = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.None, environments);
    }

    [Fact]
    public async Task DetectAsync_WithScanner_RunsScannerWithCorrectContext()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner = new TestAgentEnvironmentScanner();
        var detector = new AgentEnvironmentDetector([scanner]);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot,
        };

        await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.True(scanner.WasScanned);
        Assert.Equal(workspace.WorkspaceRoot.FullName, scanner.ScanContext?.WorkingDirectory.FullName);
        Assert.Equal(workspace.WorkspaceRoot.FullName, scanner.ScanContext?.RepositoryRoot.FullName);
    }

    [Fact]
    public async Task DetectAsync_WithScannerThatDetectsEnvironment_ReturnsEnvironment()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner = new TestAgentEnvironmentScanner
        {
            DetectedEnvironments = AgentEnvironment.ClaudeCode
        };
        var detector = new AgentEnvironmentDetector([scanner]);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot,
        };

        var environments = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.ClaudeCode, environments);
    }

    [Fact]
    public async Task DetectAsync_WithMultipleScanners_RunsAllScanners()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner1 = new TestAgentEnvironmentScanner
        {
            DetectedEnvironments = AgentEnvironment.CopilotCli | AgentEnvironment.CopilotApp
        };
        var scanner2 = new TestAgentEnvironmentScanner
        {
            DetectedEnvironments = AgentEnvironment.OpenCode | AgentEnvironment.CopilotCli
        };
        var detector = new AgentEnvironmentDetector([scanner1, scanner2]);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot,
        };

        var environments = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.True(scanner1.WasScanned);
        Assert.True(scanner2.WasScanned);
        Assert.Equal(AgentEnvironment.CopilotCli | AgentEnvironment.CopilotApp | AgentEnvironment.OpenCode, environments);
    }
}
