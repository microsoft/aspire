// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeAgentEnvironmentDetector(params AgentClientKind[] detectedClients) : IAgentEnvironmentDetector
{
    public IReadOnlyList<AgentEnvironmentApplicator> Applicators { get; init; } = [];

    public Task<AgentEnvironmentApplicator[]> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var client in detectedClients)
        {
            context.AddDetectedClient(client);
        }

        foreach (var applicator in Applicators)
        {
            context.AddApplicator(applicator);
        }

        return Task.FromResult(context.Applicators.ToArray());
    }
}
