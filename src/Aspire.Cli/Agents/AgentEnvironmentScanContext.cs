// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Context passed to agent environment scanners to collect detected applicators.
/// </summary>
internal sealed class AgentEnvironmentScanContext
{
    private readonly List<AgentEnvironmentApplicator> _applicators = [];
    private readonly HashSet<string> _skillFileApplicatorPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _skillBaseDirectories = new(StringComparer.OrdinalIgnoreCase);

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
    /// Checks if a skill file applicator has already been added for the specified path.
    /// </summary>
    /// <param name="skillRelativePath">The relative path to the skill file.</param>
    /// <returns>True if an applicator has already been added for this path.</returns>
    public bool HasSkillFileApplicator(string skillRelativePath)
    {
        return _skillFileApplicatorPaths.Contains(skillRelativePath);
    }

    /// <summary>
    /// Marks a skill file path as having an applicator added.
    /// </summary>
    /// <param name="skillRelativePath">The relative path to the skill file.</param>
    public void MarkSkillFileApplicatorAdded(string skillRelativePath)
    {
        _skillFileApplicatorPaths.Add(skillRelativePath);
    }

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
    /// Registers a skill base directory for an agent environment (e.g., ".claude/skills", ".github/skills").
    /// These directories are used to mirror skill files across all detected agent environments.
    /// </summary>
    /// <param name="relativeSkillBaseDir">The relative path to the skill base directory from the repository root.</param>
    public void AddSkillBaseDirectory(string relativeSkillBaseDir)
    {
        _skillBaseDirectories.Add(relativeSkillBaseDir);
    }

    /// <summary>
    /// Gets the registered skill base directories for all detected agent environments.
    /// </summary>
    public IReadOnlyCollection<string> SkillBaseDirectories => _skillBaseDirectories;
}
