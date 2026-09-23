// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly IReadOnlyList<AgentClientDetection> _detections;
    private readonly ConcurrentQueue<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> _calls = new();

    public TestAgentEnvironmentScanner(string id, string displayName, params AgentClientDetection[] detections)
    {
        Id = id;
        DisplayName = displayName;
        _detections = Array.AsReadOnly(detections.ToArray());
    }

    public string Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<(DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot, CancellationToken CancellationToken)> Calls => _calls.ToArray();

    public Func<AgentEnvironmentScanContext, CancellationToken, Task>? ScanAsyncCallback { get; set; }

    public Func<AgentInitRequest, IEnumerable<AgentConfigurationTarget>>? GetTargetsCallback { get; set; }

    public static TestAgentEnvironmentScanner[] CreateEnvironments(params AgentClientDetection[] detections) =>
    [
        new("copilot", AgentCommandStrings.Environment_Copilot, detections.Where(d => d.Client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp).ToArray()),
        new("vscode", AgentCommandStrings.Environment_VsCode, detections.Where(d => d.Client is AgentClientKind.VsCode).ToArray()),
        new("claude", "Claude Code", detections.Where(d => d.Client is AgentClientKind.ClaudeCode).ToArray()),
        new("opencode", "OpenCode", detections.Where(d => d.Client is AgentClientKind.OpenCode).ToArray())
    ];

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

    public AgentConfigurationTarget CreateTarget(string path, AgentAssetKind asset, AgentConfigurationStatus status, string? message)
        => new(path, AgentConfigurationScope.Project, asset, [this], asset.ToString(), (root, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status is AgentConfigurationStatus.Configured)
            {
                root["configured"] = true;
            }
            return Task.FromResult(new AgentConfigurationEdit(status, message));
        });

    public override string ToString() => Id;
}
