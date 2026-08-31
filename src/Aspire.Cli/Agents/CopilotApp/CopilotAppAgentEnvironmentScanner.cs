// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.CopilotApp;

/// <summary>
/// Detects whether the GitHub Copilot App is installed.
/// </summary>
internal sealed class CopilotAppAgentEnvironmentScanner(
    ICopilotAppInstallationDetector installationDetector,
    ILogger<CopilotAppAgentEnvironmentScanner> logger) : IAgentEnvironmentScanner
{
    /// <inheritdoc />
    public Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (installationDetector.GetInstallationMarker() is { } installationMarker)
        {
            logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            context.AddDetectedClient(AgentClientKind.CopilotApp);
        }

        return Task.CompletedTask;
    }
}
