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

        if (installationDetector.GetInstallationPath() is { } installationPath)
        {
            logger.LogDebug("Detected GitHub Copilot App installation at {Path}", installationPath);
            context.AddDetectedClient(AgentClientKind.CopilotApp);
        }

        return Task.CompletedTask;
    }
}
