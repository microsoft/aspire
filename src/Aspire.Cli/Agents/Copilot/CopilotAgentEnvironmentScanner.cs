// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Discovers GitHub Copilot App and CLI independently without configuring either client.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly ICopilotCliRunner _copilotCliRunner;
    private readonly ICopilotAppInstallationDetector _copilotAppInstallationDetector;
    private readonly IEnvironment _environment;
    private readonly ILogger<CopilotAgentEnvironmentScanner> _logger;

    public CopilotAgentEnvironmentScanner(
        ICopilotCliRunner copilotCliRunner,
        ICopilotAppInstallationDetector copilotAppInstallationDetector,
        IEnvironment environment,
        ILogger<CopilotAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(copilotCliRunner);
        ArgumentNullException.ThrowIfNull(copilotAppInstallationDetector);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _copilotCliRunner = copilotCliRunner;
        _copilotAppInstallationDetector = copilotAppInstallationDetector;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting GitHub Copilot environment scan");

        var detections = new List<AgentClientDetection>();
        if (_copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            _logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            detections.Add(new AgentClientDetection(AgentClientKind.CopilotApp, Version: null, IsInsiders: false));
        }

        // VS Code can supply an interactive Copilot installation shim. Do not invoke it during
        // discovery, where an installation prompt could hang the enclosing command.
        if (_environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            _logger.LogDebug("Detected VS Code terminal environment. Skipping the Copilot CLI version probe.");
            detections.Add(new AgentClientDetection(AgentClientKind.CopilotCli, Version: null, IsInsiders: false));
        }
        else
        {
            var version = await _copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                _logger.LogDebug("Found GitHub Copilot CLI version: {Version}", version);
                detections.Add(new AgentClientDetection(AgentClientKind.CopilotCli, version.ToString(), IsInsiders: false));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return detections.AsReadOnly();
    }
}
