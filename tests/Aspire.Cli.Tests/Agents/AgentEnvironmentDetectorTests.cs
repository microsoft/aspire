// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class AgentEnvironmentDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DetectAsync_WithNoScanners_ReturnsReadOnlyEmptyList()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = new AgentEnvironmentDetector([]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        var collection = Assert.IsAssignableFrom<ICollection<AgentClientDetection>>(detections);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(new(AgentClientKind.CopilotCli, null, false)));
    }

    [Fact]
    public async Task DetectAsync_PassesContextAndCancellationToEveryScanner()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        var scanner1 = new TestAgentEnvironmentScanner(new AgentClientDetection(AgentClientKind.CopilotCli, "1.0.0", false));
        var scanner2 = new TestAgentEnvironmentScanner(new AgentClientDetection(AgentClientKind.ClaudeCode, "2.0.0", false));
        var detector = new AgentEnvironmentDetector([scanner1, scanner2]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await detector.DetectAsync(context, cancellationSource.Token).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
        [
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.ClaudeCode, "2.0.0", false)
        ], detections);
        foreach (var scanner in new[] { scanner1, scanner2 })
        {
            var call = Assert.Single(scanner.Calls);
            Assert.Same(context, call.Context);
            Assert.Equal(cancellationSource.Token, call.CancellationToken);
        }
    }

    [Fact]
    public async Task DetectAsync_DeduplicatesIdenticalEvidenceButPreservesClientVariants()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner1 = new TestAgentEnvironmentScanner(
            new(AgentClientKind.CopilotApp, null, false),
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.VsCode, "1.100.0", false));
        var scanner2 = new TestAgentEnvironmentScanner(
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.VsCode, "1.101.0-insider", true));
        var detector = new AgentEnvironmentDetector([scanner1, scanner2]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
        [
            new(AgentClientKind.CopilotApp, null, false),
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.VsCode, "1.100.0", false),
            new(AgentClientKind.VsCode, "1.101.0-insider", true)
        ], detections);
    }

    [Fact]
    public async Task DetectAsync_WithAllClientScanners_ReturnsAllEvidenceWithoutWritingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            CopilotVersion = new SemVersion(1, 0, 0),
            ClaudeCodeVersion = new SemVersion(2, 0, 0),
            OpenCodeVersion = new SemVersion(2, 1, 0),
            VsCodeVersion = new SemVersion(1, 100, 0),
            VsCodeInsidersVersion = SemVersion.Parse("1.101.0-insider", SemVersionStyles.Strict)
        };
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["AI_AGENT"] = "github_copilot_app_agent"
        });
        var executionContext = workspace.CreateExecutionContext();
        var detector = new AgentEnvironmentDetector(
        [
            new CopilotAgentEnvironmentScanner(runner, new CopilotAppInstallationDetector(environment, executionContext), environment, NullLogger<CopilotAgentEnvironmentScanner>.Instance),
            new VsCodeAgentEnvironmentScanner(runner, executionContext, environment, NullLogger<VsCodeAgentEnvironmentScanner>.Instance),
            new ClaudeCodeAgentEnvironmentScanner(runner, executionContext, NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance),
            new OpenCodeAgentEnvironmentScanner(runner, NullLogger<OpenCodeAgentEnvironmentScanner>.Instance)
        ]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
        [
            new(AgentClientKind.CopilotApp, null, false),
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.VsCode, "1.100.0", false),
            new(AgentClientKind.VsCode, "1.101.0-insider", true),
            new(AgentClientKind.ClaudeCode, "2.0.0", false),
            new(AgentClientKind.OpenCode, "2.1.0", false)
        ], detections);
        Assert.Equal(["copilot", "code", "code-insiders", "claude", "opencode"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public async Task DetectAsync_ReturnsIndependentReadOnlySnapshots()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var originalDetection = new AgentClientDetection(AgentClientKind.CopilotCli, "1.0.0", false);
        var updatedDetection = new AgentClientDetection(AgentClientKind.ClaudeCode, "2.0.0", false);
        var scannerResults = new List<AgentClientDetection> { originalDetection };
        var scanner = new TestAgentEnvironmentScanner
        {
            ScanAsyncCallback = (_, _) => Task.FromResult<IReadOnlyList<AgentClientDetection>>(scannerResults)
        };
        var detector = new AgentEnvironmentDetector([scanner]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var first = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();
        scannerResults.Clear();
        scannerResults.Add(updatedDetection);
        var second = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([originalDetection], first);
        Assert.Equal<AgentClientDetection>([updatedDetection], second);
        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(first);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = updatedDetection);
        Assert.Throws<NotSupportedException>(() => list.Add(updatedDetection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectAsync_WhenCancelled_DoesNotRunScanners(bool includeScanner)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner = new TestAgentEnvironmentScanner();
        var detector = new AgentEnvironmentDetector(includeScanner ? [scanner] : []);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => detector.DetectAsync(context, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(scanner.Calls);
    }

    [Fact]
    public async Task DetectAsync_WhenCancelledByScanner_DoesNotRunRemainingScanners()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        var scanner1 = new TestAgentEnvironmentScanner
        {
            ScanAsyncCallback = (_, _) =>
            {
                cancellationSource.Cancel();
                return Task.FromResult<IReadOnlyList<AgentClientDetection>>([]);
            }
        };
        var scanner2 = new TestAgentEnvironmentScanner();
        var detector = new AgentEnvironmentDetector([scanner1, scanner2]);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => detector.DetectAsync(context, cancellationSource.Token)).DefaultTimeout();

        Assert.Single(scanner1.Calls);
        Assert.Empty(scanner2.Calls);
    }

    [Fact]
    public async Task TestDetector_CopiesInputAndReturnsReadOnlyEvidence()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var originalDetection = new AgentClientDetection(AgentClientKind.OpenCode, "2.0.0", false);
        var input = new[] { originalDetection };
        var detector = new TestAgentEnvironmentDetector(input);
        input[0] = new(AgentClientKind.CopilotCli, null, false);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await detector.DetectAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([originalDetection], detections);
        Assert.Same(context, Assert.Single(detector.Requests));
        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(list.Clear);
    }

    [Fact]
    public async Task TestDetector_WhenCancelled_DoesNotRecordRequest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        var detector = new TestAgentEnvironmentDetector();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => detector.DetectAsync(context, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(detector.Requests);
    }

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo directory)
        => new()
        {
            WorkingDirectory = directory,
            RepositoryRoot = directory
        };
}
