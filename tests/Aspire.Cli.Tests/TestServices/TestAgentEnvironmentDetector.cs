// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentDetector(params AgentEnvironmentApplicator[] applicators) : IAgentEnvironmentDetector
{
    public IReadOnlyCollection<AgentClientKind> DetectedClients { get; init; } = [];

    public Task<AgentEnvironmentApplicator[]> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var client in DetectedClients)
        {
            context.AddDetectedClient(client);
        }
        return Task.FromResult(applicators);
    }
}
