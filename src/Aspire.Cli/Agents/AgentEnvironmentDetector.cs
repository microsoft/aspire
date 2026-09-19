// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Detects agent environments by running all registered scanners.
/// </summary>
internal sealed class AgentEnvironmentDetector(IEnumerable<IAgentEnvironmentScanner> scanners) : IAgentEnvironmentDetector
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detections = new List<AgentClientDetection>();

        foreach (var scanner in scanners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            detections.AddRange(await scanner.ScanAsync(context, cancellationToken).ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(detections.Distinct().ToArray());
    }
}
