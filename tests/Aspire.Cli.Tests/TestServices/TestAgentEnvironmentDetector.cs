// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentDetector : IAgentEnvironmentDetector
{
    private int _scanCount;

    public int ScanCount => _scanCount;

    public AgentEnvironment DetectedEnvironments { get; init; }

    public Task<AgentEnvironment> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _scanCount);
        return Task.FromResult(DetectedEnvironments);
    }
}
