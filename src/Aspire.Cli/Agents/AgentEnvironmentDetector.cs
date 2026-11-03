// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Detects agent environments by running all registered scanners.
/// </summary>
internal sealed class AgentEnvironmentDetector(IEnumerable<IAgentEnvironmentScanner> scanners) : IAgentEnvironmentDetector
{
    /// <inheritdoc />
    public async Task<AgentEnvironment> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        var environments = AgentEnvironment.None;
        foreach (var scanner in scanners)
        {
            environments |= await scanner.ScanAsync(context, cancellationToken);
        }

        return environments;
    }
}
