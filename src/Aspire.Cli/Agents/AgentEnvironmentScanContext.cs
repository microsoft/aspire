// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Context passed to agent environment scanners to collect detected applicators.
/// </summary>
internal sealed class AgentEnvironmentScanContext
{
    private readonly List<AgentEnvironmentApplicator> _applicators = [];
    private readonly Dictionary<AgentAssetKind, HashSet<string>> _assetBaseDirectories = new();
    private readonly HashSet<AgentClient> _detectedClients = [];

    /// <summary>
    /// Gets the working directory being scanned.
    /// </summary>
    public required DirectoryInfo WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the root directory of the repository/workspace.
    /// This is typically the git repository root if available, otherwise the working directory.
    /// Scanners should use this as the boundary for searches instead of searching up the directory tree.
    /// </summary>
    public required DirectoryInfo RepositoryRoot { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a Playwright CLI applicator has been added.
    /// This is used to ensure only one applicator for Playwright is added across all scanners.
    /// </summary>
    public bool PlaywrightApplicatorAdded { get; set; }

    /// <summary>
    /// Adds an applicator to the collection of detected agent environments.
    /// </summary>
    /// <param name="applicator">The applicator to add.</param>
    public void AddApplicator(AgentEnvironmentApplicator applicator)
    {
        ArgumentNullException.ThrowIfNull(applicator);
        _applicators.Add(applicator);
    }

    /// <summary>
    /// Gets the collection of detected applicators.
    /// </summary>
    public IReadOnlyList<AgentEnvironmentApplicator> Applicators => _applicators;

    /// <summary>
    /// Registers an agent asset base directory for an agent environment (e.g., ".claude/skills", ".github/skills").
    /// These directories are used to mirror agent asset files across all detected agent environments.
    /// </summary>
    /// <param name="assetType">The type of agent asset (e.g., skill, extension).</param>
    /// <param name="relativeAssetBaseDir">The relative path to the asset base directory from the repository root.</param>
    public void AddAssetBaseDirectory(AgentAssetKind assetType, string relativeAssetBaseDir)
    {
        if (!_assetBaseDirectories.TryGetValue(assetType, out var dirs))
        {
            dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _assetBaseDirectories[assetType] = dirs;
        }
        dirs.Add(relativeAssetBaseDir);
    }

    /// <summary>
    /// Gets the registered asset base directories for all detected agent environments.
    /// </summary>
    public IReadOnlySet<string> AgentAssetBaseDirectories(AgentAssetKind assetType) => _assetBaseDirectories.TryGetValue(assetType, out var dirs) ? dirs : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that an agent client was detected as present in the environment. Used to scope
    /// telemetry hook registration to the clients the user actually has, independent of whether the
    /// Aspire MCP server still needs configuring.
    /// </summary>
    /// <param name="client">The detected agent client.</param>
    public void AddDetectedClient(AgentClient client)
    {
        _detectedClients.Add(client);
    }

    /// <summary>
    /// Gets the set of agent clients detected as present in the environment.
    /// </summary>
    public IReadOnlyCollection<AgentClient> DetectedClients => _detectedClients;

    /// <summary>
    /// Gets the set of agent asset types supported by the detected agent clients.
    /// </summary>
    public IReadOnlyCollection<AgentAssetKind> SupportedAssetTypes => _detectedClients.SelectMany(c => c.SupportedAssetKinds).Distinct().ToList();
}
