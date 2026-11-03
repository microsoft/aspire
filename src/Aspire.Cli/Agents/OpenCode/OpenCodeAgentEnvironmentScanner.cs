// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.OpenCode;

/// <summary>
/// Scans for project use or an installation of OpenCode.
/// </summary>
internal sealed class OpenCodeAgentEnvironmentScanner(
    IOpenCodeCliRunner openCodeCliRunner,
    ILogger<OpenCodeAgentEnvironmentScanner> logger) : IAgentEnvironmentScanner
{
    /// <inheritdoc />
    public async Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting OpenCode environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);

        var configFilePath = OpenCodePaths.GetMcpConfigPath(context.RepositoryRoot);
        var configFileExists = File.Exists(configFilePath);

        if (configFileExists)
        {
            logger.LogDebug("Found existing OpenCode configuration at: {ConfigFilePath}", configFilePath);

            return AgentEnvironment.OpenCode;
        }

        logger.LogDebug("No OpenCode configuration found, checking for OpenCode CLI installation...");
        var openCodeVersion = await openCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        if (openCodeVersion is not null)
        {
            logger.LogDebug("Found OpenCode CLI version: {Version}", openCodeVersion);
            return AgentEnvironment.OpenCode;
        }

        logger.LogDebug("OpenCode CLI not found - skipping");

        return AgentEnvironment.None;
    }
}
