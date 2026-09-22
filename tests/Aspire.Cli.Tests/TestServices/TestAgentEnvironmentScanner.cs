// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly IReadOnlyList<AgentClientDetection> _detections;
    private readonly ConcurrentQueue<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> _calls;

    public TestAgentEnvironmentScanner(params AgentClientDetection[] detections)
    {
        _detections = Array.AsReadOnly(detections.ToArray());
        _calls = new();
    }

    private TestAgentEnvironmentScanner(TestAgentEnvironmentScanner owner, string id, string displayName, params AgentClientKind[] clients)
    {
        Id = id;
        DisplayName = displayName;
        _detections = owner._detections.Where(detection => clients.Contains(detection.Client)).ToArray();
        _calls = owner._calls;
        ScanAsyncCallback = owner.ScanAsyncCallback;
        GetTargetsCallback = owner.GetTargetsCallback;
    }

    public string Id { get; init; } = "test";
    public string DisplayName { get; init; } = "Test";
    public IReadOnlyList<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> Calls => _calls.ToArray();

    public Func<AgentEnvironmentScanContext, CancellationToken, Task>? ScanAsyncCallback { get; init; }

    public Func<AgentInitRequest, IEnumerable<AgentConfigurationTarget>>? GetTargetsCallback { get; init; }

    public IReadOnlyList<IAgentEnvironmentScanner> CreateScanners()
        => Array.AsReadOnly<IAgentEnvironmentScanner>(
        [
            new TestAgentEnvironmentScanner(this, "copilot", AgentCommandStrings.Environment_Copilot, AgentClientKind.CopilotCli, AgentClientKind.CopilotApp),
            new TestAgentEnvironmentScanner(this, "vscode", AgentCommandStrings.Environment_VsCode, AgentClientKind.VsCode),
            new TestAgentEnvironmentScanner(this, "claude", "Claude Code", AgentClientKind.ClaudeCode),
            new TestAgentEnvironmentScanner(this, "opencode", "OpenCode", AgentClientKind.OpenCode)
        ]);

    public Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue((context.WorkingDirectory, context.WorkspaceRoot, cancellationToken));

        if (ScanAsyncCallback is not null)
        {
            return ScanAsyncCallback(context, cancellationToken);
        }
        foreach (var detection in _detections)
        {
            context.AddDetection(detection);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
        => GetTargetsCallback?.Invoke(request) ?? [];

    public override string ToString() => Id;
}

/// <summary>
/// Isolated scanners for tests that do not execute native configuration.
/// </summary>
internal sealed class TestAgentEnvironments
{
    public TestAgentEnvironments(TestAgentEnvironmentScanner scanner)
    {
        All = scanner.CreateScanners();
        Copilot = All[0];
        VsCode = All[1];
        ClaudeCode = All[2];
        OpenCode = All[3];
    }

    public static TestAgentEnvironments Default { get; } = new(new TestAgentEnvironmentScanner());

    public IAgentEnvironmentScanner Copilot { get; }
    public IAgentEnvironmentScanner VsCode { get; }
    public IAgentEnvironmentScanner ClaudeCode { get; }
    public IAgentEnvironmentScanner OpenCode { get; }
    public IReadOnlyList<IAgentEnvironmentScanner> All { get; }
}
