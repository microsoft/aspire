// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a skill that can be installed into a skill location.
/// </summary>
internal sealed class SkillDefinition
{
    /// <summary>
    /// The Aspire skill for CLI commands and workflows.
    /// </summary>
    public static readonly SkillDefinition Aspire = new(
        CommonAgentApplicators.AspireSkillName,
        AgentCommandStrings.SkillDescription_Aspire,
        skillContent: null,
        embeddedResourceRoot: CommonAgentApplicators.AspireSkillResourceRoot,
        installExcludedRelativePaths: [Path.Combine("evals")],
        isDefault: true);

    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly SkillDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        skillContent: null,
        embeddedResourceRoot: null, // Playwright is installed via PlaywrightCliInstaller, not a static file
        installExcludedRelativePaths: [],
        isDefault: true);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// </summary>
    public static readonly SkillDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        CommonAgentApplicators.DotnetInspectSkillFileContent,
        embeddedResourceRoot: null,
        installExcludedRelativePaths: [],
        isDefault: true);

    private SkillDefinition(string name, string description, string? skillContent, string? embeddedResourceRoot, IReadOnlyList<string> installExcludedRelativePaths, bool isDefault)
    {
        Name = name;
        Description = description;
        SkillContent = skillContent;
        EmbeddedResourceRoot = embeddedResourceRoot;
        InstallExcludedRelativePaths = installExcludedRelativePaths;
        IsDefault = isDefault;
    }

    /// <summary>
    /// Gets the skill name (used as the folder name under skill locations).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown in the selection prompt.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the content for the top-level SKILL.md file when the skill is defined as a single-file bundle,
    /// or <c>null</c> when installable files come from <see cref="EmbeddedResourceRoot"/> or another installer.
    /// </summary>
    public string? SkillContent { get; }

    /// <summary>
    /// Gets the embedded resource root for bundled skill files, or <c>null</c> if the skill is not installed from an embedded file tree.
    /// </summary>
    public string? EmbeddedResourceRoot { get; }

    /// <summary>
    /// Gets relative paths that should be excluded when the skill is installed into a workspace.
    /// </summary>
    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    /// <summary>
    /// Gets whether a bundled file should be installed into a workspace.
    /// </summary>
    public bool ShouldInstallFile(string relativePath)
    {
        foreach (var excludedPath in InstallExcludedRelativePaths)
        {
            if (PathMatchesOrIsUnder(relativePath, excludedPath))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether this skill should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    private static bool PathMatchesOrIsUnder(string relativePath, string excludedPath)
    {
        if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!relativePath.StartsWith(excludedPath, StringComparison.Ordinal))
        {
            return false;
        }

        return relativePath.Length > excludedPath.Length && relativePath[excludedPath.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Gets all available skill definitions.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> All { get; } = [Aspire, PlaywrightCli, DotnetInspect];
}
