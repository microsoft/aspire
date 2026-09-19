// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Discovers Claude Code from project configuration or its installed CLI.
/// </summary>
internal sealed class ClaudeCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly IClaudeCodeCliRunner _claudeCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<ClaudeCodeAgentEnvironmentScanner> _logger;

    public ClaudeCodeAgentEnvironmentScanner(
        IClaudeCodeCliRunner claudeCodeCliRunner,
        CliExecutionContext executionContext,
        ILogger<ClaudeCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(claudeCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);
        _claudeCodeCliRunner = claudeCodeCliRunner;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting Claude Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);

        var hasProjectConfiguration = HasProjectConfiguration(context.WorkingDirectory, context.RepositoryRoot);
        var version = await _claudeCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (hasProjectConfiguration || version is not null)
        {
            _logger.LogDebug("Detected Claude Code with version: {Version}", version);
            return Array.AsReadOnly<AgentClientDetection>(
            [
                new(AgentClientKind.ClaudeCode, version?.ToString(), IsInsiders: false)
            ]);
        }

        return Array.AsReadOnly<AgentClientDetection>([]);
    }

    private bool HasProjectConfiguration(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot.FullName, startDirectory.FullName);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            // An explicit --workspace-root can be outside the current working directory.
            startDirectory = repositoryRoot;
        }

        for (var currentDirectory = startDirectory; currentDirectory is not null; currentDirectory = currentDirectory.Parent)
        {
            // The home .claude directory contains user settings, not evidence of project usage.
            if (Path.GetRelativePath(_executionContext.HomeDirectory.FullName, currentDirectory.FullName) != "." &&
                (Directory.Exists(Path.Combine(currentDirectory.FullName, ".claude")) ||
                 File.Exists(Path.Combine(currentDirectory.FullName, ".mcp.json"))))
            {
                return true;
            }

            if (Path.GetRelativePath(repositoryRoot.FullName, currentDirectory.FullName) == ".")
            {
                break;
            }
        }

        return false;
    }
}
