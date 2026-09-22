// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentClientEnvironment : IAgentEnvironmentScanner
{
    private readonly IReadOnlyList<AgentClientDetection> _detections;
    private readonly ConcurrentQueue<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> _calls;

    public TestAgentClientEnvironment(params AgentClientDetection[] detections)
    {
        _detections = Array.AsReadOnly(detections.ToArray());
        _calls = new();
    }

    private TestAgentClientEnvironment(TestAgentClientEnvironment owner, string id)
    {
        _detections = owner._detections.Where(detection => detection.Client.Id == id).ToArray();
        _calls = owner._calls;
        ScanAsyncCallback = owner.ScanAsyncCallback;
        GetTargetsCallback = owner.GetTargetsCallback;
    }

    public IReadOnlyList<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> Calls => _calls.ToArray();

    public Func<DirectoryInfo, DirectoryInfo, CancellationToken, Task<AgentEnvironmentDetection?>>? ScanAsyncCallback { get; init; }

    public Func<AgentInitRequest, IEnumerable<AgentConfigurationTarget>>? GetTargetsCallback { get; init; }

    public AgentClientCatalog CreateCatalog()
        => new(new TestAgentClients(this).All.Select(client => client with { Environment = new TestAgentClientEnvironment(this, client.Id) }));

    public Task<AgentEnvironmentDetection?> ScanAsync(DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue((workingDirectory, workspaceRoot, cancellationToken));

        return ScanAsyncCallback?.Invoke(workingDirectory, workspaceRoot, cancellationToken) ??
            Task.FromResult<AgentEnvironmentDetection?>(_detections.Count > 0
                ? new(_detections[0].Version, _detections[0].IsInsiders)
                : null);
    }

    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
        => GetTargetsCallback?.Invoke(request) ?? [];
}

/// <summary>
/// Isolated client metadata for tests that do not execute native configuration.
/// </summary>
internal sealed class TestAgentClients
{
    public static TestAgentClients Default { get; } = new(new TestAgentClientEnvironment());

    public TestAgentClients(IAgentEnvironmentScanner environment)
    {
        Copilot = new("copilot", AgentCommandStrings.Environment_Copilot, environment);
        VsCode = new("vscode", AgentCommandStrings.Environment_VsCode, environment);
        ClaudeCode = new("claude", "Claude Code", environment);
        OpenCode = new("opencode", "OpenCode", environment);
        All = Array.AsReadOnly<AgentClient>([Copilot, VsCode, ClaudeCode, OpenCode]);
    }

    public AgentClient Copilot { get; }
    public AgentClient VsCode { get; }
    public AgentClient ClaudeCode { get; }
    public AgentClient OpenCode { get; }
    public IReadOnlyList<AgentClient> All { get; }
}
