// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Detects agent environments by running all registered scanners.
/// </summary>
internal sealed class AgentEnvironmentDetector(IEnumerable<IAgentEnvironmentScanner> scanners) : IAgentEnvironmentDetector
{
    /// <inheritdoc />
    public async Task<AgentEnvironmentApplicator[]> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        foreach (var scanner in scanners)
        {
            await scanner.ScanAsync(context, cancellationToken);
        }

        return [.. context.Applicators];
    }
}
