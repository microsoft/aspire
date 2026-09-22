// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentClientEnvironment(params AgentClientDetection[] detections) : IAgentClientEnvironment
{
    private readonly IReadOnlyList<AgentClientDetection> _detections = Array.AsReadOnly(detections.ToArray());
    private readonly ConcurrentQueue<(IReadOnlyList<AgentClient> Clients, DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> _calls = new();

    public IReadOnlyList<(IReadOnlyList<AgentClient> Clients, DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> Calls => _calls.ToArray();

    public Func<IReadOnlyList<AgentClient>, DirectoryInfo, DirectoryInfo, CancellationToken, Task<IReadOnlyList<AgentClientDetection>>>? ScanAsyncCallback { get; init; }

    public Func<AgentInitRequest, IEnumerable<AgentConfigurationTarget>>? GetTargetsCallback { get; init; }

    public Task<IReadOnlyList<AgentClientDetection>> ScanAsync(IReadOnlyList<AgentClient> clients, DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue((clients, workingDirectory, workspaceRoot, cancellationToken));

        return ScanAsyncCallback?.Invoke(clients, workingDirectory, workspaceRoot, cancellationToken) ??
            Task.FromResult<IReadOnlyList<AgentClientDetection>>(Array.AsReadOnly(_detections
                .Select(detection => detection with { Client = clients.Single(client => client.Id == detection.Client.Id) }).ToArray()));
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

    public TestAgentClients(IAgentClientEnvironment environment)
    {
        CopilotCli = new("copilot-cli", "GitHub Copilot CLI", environment);
        CopilotApp = new("copilot-app", "GitHub Copilot App", environment);
        VsCode = new("vscode", "VS Code", environment);
        ClaudeCode = new("claude-code", "Claude Code", environment);
        OpenCode = new("opencode", "OpenCode", environment);
        All = Array.AsReadOnly<AgentClient>([CopilotCli, CopilotApp, VsCode, ClaudeCode, OpenCode]);
    }

    public AgentClient CopilotCli { get; }
    public AgentClient CopilotApp { get; }
    public AgentClient VsCode { get; }
    public AgentClient ClaudeCode { get; }
    public AgentClient OpenCode { get; }
    public IReadOnlyList<AgentClient> All { get; }
}
