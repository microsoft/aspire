// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentScanner(params AgentClientDetection[] detections) : IAgentEnvironmentScanner
{
    private readonly IReadOnlyList<AgentClientDetection> _detections = Array.AsReadOnly(detections.ToArray());
    private readonly ConcurrentQueue<(AgentEnvironmentScanContext Context, CancellationToken CancellationToken)> _calls = new();

    public IReadOnlyList<(AgentEnvironmentScanContext Context, CancellationToken CancellationToken)> Calls => _calls.ToArray();

    public Func<AgentEnvironmentScanContext, CancellationToken, Task<IReadOnlyList<AgentClientDetection>>>? ScanAsyncCallback { get; init; }

    public Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue((context, cancellationToken));

        return ScanAsyncCallback?.Invoke(context, cancellationToken) ?? Task.FromResult(_detections);
    }
}
