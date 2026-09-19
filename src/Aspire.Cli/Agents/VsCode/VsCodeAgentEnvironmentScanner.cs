// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Discovers VS Code from its terminal, project configuration, or installed CLIs.
/// </summary>
internal sealed class VsCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly IVsCodeCliRunner _vsCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<VsCodeAgentEnvironmentScanner> _logger;

    public VsCodeAgentEnvironmentScanner(
        IVsCodeCliRunner vsCodeCliRunner,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<VsCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(vsCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _vsCodeCliRunner = vsCodeCliRunner;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting VS Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);

        if (_environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            var version = _environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.Trim();
            if (string.IsNullOrEmpty(version))
            {
                version = null;
            }

            // VS Code exposes e.g. "1.110.0" or "1.111.0-insider" in TERM_PROGRAM_VERSION.
            // Retain that evidence without invoking another editor process from its terminal.
            return Array.AsReadOnly<AgentClientDetection>(
            [
                new(AgentClientKind.VsCode, version, IsInsiders: version?.Contains("-insider", StringComparison.OrdinalIgnoreCase) == true)
            ]);
        }

        var detections = new List<AgentClientDetection>();
        foreach (var useInsiders in new[] { false, true })
        {
            var version = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = useInsiders }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (version is not null)
            {
                _logger.LogDebug("Detected VS Code version: {Version}, Insiders: {IsInsiders}", version, useInsiders);
                detections.Add(new AgentClientDetection(AgentClientKind.VsCode, version.ToString(), IsInsiders: useInsiders));
            }
        }

        if (detections.Count == 0 && HasProjectConfiguration(context.WorkingDirectory, context.RepositoryRoot))
        {
            detections.Add(new AgentClientDetection(AgentClientKind.VsCode, Version: null, IsInsiders: false));
        }

        return detections.AsReadOnly();
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
            // The home .vscode directory holds user extensions rather than workspace settings.
            if (Path.GetRelativePath(_executionContext.HomeDirectory.FullName, currentDirectory.FullName) != "." &&
                Directory.Exists(Path.Combine(currentDirectory.FullName, ".vscode")))
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
