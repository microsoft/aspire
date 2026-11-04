// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Scans for project use or an installation of VS Code.
/// </summary>
internal sealed class VsCodeAgentEnvironmentScanner(
    IVsCodeCliRunner vsCodeCliRunner,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<VsCodeAgentEnvironmentScanner> logger) : IAgentEnvironmentScanner
{
    private const string VsCodeFolderName = ".vscode";
    /// <inheritdoc />
    public async Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting VS Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);

        logger.LogDebug("Searching for .vscode folder...");
        var vsCodeFolder = context.FindProjectDirectory(VsCodeFolderName, executionContext.HomeDirectory);

        if (vsCodeFolder is not null)
        {
            logger.LogDebug("Found .vscode folder at: {VsCodeFolder}", vsCodeFolder.FullName);

            return AgentEnvironment.VsCode;
        }
        else if (await IsVsCodeAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("No .vscode folder found, but VS Code is available on the system");

            return AgentEnvironment.VsCode;
        }
        else
        {
            logger.LogDebug("No .vscode folder found and VS Code is not available");
        }

        return AgentEnvironment.None;
    }

    /// <summary>
    /// Checks if VS Code is available on the machine.
    /// First checks for VS Code environment variables (low cost),
    /// then falls back to checking for the CLI executables.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if VS Code is available, false otherwise.</returns>
    private async Task<bool> IsVsCodeAvailableAsync(CancellationToken cancellationToken)
    {
        // First check environment variables (low cost)
        logger.LogDebug("Checking for VS Code environment variables...");
        if (environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            logger.LogDebug("Found VS Code environment variables");
            return true;
        }

        // Try VS Code stable
        logger.LogDebug("Checking for VS Code stable CLI...");
        var vsCodeVersion = await vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = false }, cancellationToken).ConfigureAwait(false);
        if (vsCodeVersion is not null)
        {
            logger.LogDebug("Found VS Code stable version: {Version}", vsCodeVersion);
            return true;
        }

        // Try VS Code Insiders
        logger.LogDebug("Checking for VS Code Insiders CLI...");
        var vsCodeInsidersVersion = await vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = true }, cancellationToken).ConfigureAwait(false);
        if (vsCodeInsidersVersion is not null)
        {
            logger.LogDebug("Found VS Code Insiders version: {Version}", vsCodeInsidersVersion);
            return true;
        }

        logger.LogDebug("VS Code not found on the system");
        return false;
    }

}
