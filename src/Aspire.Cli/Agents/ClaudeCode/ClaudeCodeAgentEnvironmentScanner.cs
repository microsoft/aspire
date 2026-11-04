// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Scans for project use or an installation of Claude Code.
/// </summary>
internal sealed class ClaudeCodeAgentEnvironmentScanner(
    IClaudeCodeCliRunner claudeCodeCliRunner,
    CliExecutionContext executionContext,
    ILogger<ClaudeCodeAgentEnvironmentScanner> logger) : IAgentEnvironmentScanner
{
    /// <inheritdoc />
    public async Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting Claude Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);

        // Find the .claude folder to determine if Claude Code is being used in this project
        logger.LogDebug("Searching for .claude folder...");
        var claudeCodeFolder = context.FindProjectDirectory(ClaudeCodePaths.ConfigDirectoryName, executionContext.HomeDirectory);

        if (claudeCodeFolder is not null)
        {
            return AgentEnvironment.ClaudeCode;
        }

        logger.LogDebug("No .claude folder found, checking for Claude Code CLI installation...");
        var claudeCodeVersion = await claudeCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        if (claudeCodeVersion is not null)
        {
            logger.LogDebug("Found Claude Code CLI version: {Version}", claudeCodeVersion);
            return AgentEnvironment.ClaudeCode;
        }

        logger.LogDebug("Claude Code CLI not found - skipping");

        return AgentEnvironment.None;
    }
}
