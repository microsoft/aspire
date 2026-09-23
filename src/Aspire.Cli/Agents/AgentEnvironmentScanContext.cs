// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Collects client detections during a read-only environment scan.
/// </summary>
internal sealed class AgentEnvironmentScanContext
{
    private readonly List<AgentClientDetection> _detectedClients = [];

    public AgentEnvironmentScanContext(DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot)
    {
        WorkingDirectory = workingDirectory;
        WorkspaceRoot = workspaceRoot;
        DetectedClients = _detectedClients.AsReadOnly();
    }

    public DirectoryInfo WorkingDirectory { get; }
    public DirectoryInfo WorkspaceRoot { get; }
    public IReadOnlyList<AgentClientDetection> DetectedClients { get; }

    public void AddDetection(AgentClientDetection detection)
    {
        if (!_detectedClients.Contains(detection))
        {
            _detectedClients.Add(detection);
        }
    }
}
