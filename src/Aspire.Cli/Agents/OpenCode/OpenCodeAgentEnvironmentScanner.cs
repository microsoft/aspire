// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.OpenCode;

/// <summary>
/// Discovers OpenCode from project configuration or its installed CLI.
/// </summary>
internal sealed class OpenCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly IOpenCodeCliRunner _openCodeCliRunner;
    private readonly ILogger<OpenCodeAgentEnvironmentScanner> _logger;

    public OpenCodeAgentEnvironmentScanner(IOpenCodeCliRunner openCodeCliRunner, ILogger<OpenCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(openCodeCliRunner);
        ArgumentNullException.ThrowIfNull(logger);
        _openCodeCliRunner = openCodeCliRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting OpenCode environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);

        var hasProjectConfiguration = HasProjectConfiguration(context.WorkingDirectory, context.RepositoryRoot);
        // Probe even when configuration exists so application can compare the installed version
        // with the file's schema. A missing executable does not invalidate project evidence.
        var version = await _openCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (hasProjectConfiguration || version is not null)
        {
            _logger.LogDebug("Detected OpenCode with version: {Version}", version);
            return Array.AsReadOnly<AgentClientDetection>(
            [
                new(AgentClientKind.OpenCode, version?.ToString(), IsInsiders: false)
            ]);
        }

        return Array.AsReadOnly<AgentClientDetection>([]);
    }

    private static bool HasProjectConfiguration(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
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
            // V2 also loads config files inside .opencode. A skills-only directory is not enough
            // evidence of project configuration: https://opencode.ai/v2/docs/config#locations.
            if (File.Exists(Path.Combine(currentDirectory.FullName, "opencode.json")) ||
                File.Exists(Path.Combine(currentDirectory.FullName, "opencode.jsonc")) ||
                File.Exists(Path.Combine(currentDirectory.FullName, ".opencode", "opencode.json")) ||
                File.Exists(Path.Combine(currentDirectory.FullName, ".opencode", "opencode.jsonc")))
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
