// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentDetector(params AgentClientDetection[] detections) : IAgentEnvironmentDetector
{
    private readonly IReadOnlyList<AgentClientDetection> _detections = Array.AsReadOnly(detections.ToArray());
    private readonly ConcurrentQueue<AgentEnvironmentScanContext> _requests = new();

    public IReadOnlyList<AgentEnvironmentScanContext> Requests => _requests.ToArray();

    public Task<IReadOnlyList<AgentClientDetection>> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(context);

        return Task.FromResult(_detections);
    }
}
