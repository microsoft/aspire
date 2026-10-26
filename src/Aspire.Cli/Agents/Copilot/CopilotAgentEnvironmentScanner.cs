// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Scans for GitHub Copilot App and CLI installations.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner(
    ICopilotCliRunner copilotCliRunner,
    ICopilotAppInstallationDetector copilotAppInstallationDetector,
    IEnvironment environment,
    ILogger<CopilotAgentEnvironmentScanner> logger) : IAgentEnvironmentScanner
{
    /// <inheritdoc />
    public async Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting GitHub Copilot environment scan");

        var environments = AgentEnvironment.None;
        if (copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            environments |= AgentEnvironment.CopilotApp;
        }

        // Check if we're running in a VSCode terminal.
        var isVSCode = environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        if (isVSCode)
        {
            // Do not probe VS Code's Copilot installation shim; it can prompt or hang discovery.
            logger.LogDebug("Detected VSCode terminal environment. Skipping the Copilot CLI version probe.");
        }
        else
        {
            logger.LogDebug("Checking for GitHub Copilot CLI installation...");
            var copilotVersion = await copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (copilotVersion is not null)
            {
                logger.LogDebug("Found GitHub Copilot CLI version: {Version}", copilotVersion);
                environments |= AgentEnvironment.CopilotCli;
            }
            else
            {
                logger.LogDebug("GitHub Copilot CLI is not installed");
            }
        }

        return environments;
    }
}
