// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a skill that can be installed into a skill location.
/// </summary>
[DebuggerDisplay("Name = {Name}, Description = {Description}, IsDefault = {IsDefault}")]
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
        isDefault: false);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// Only offered when the workspace contains a .NET AppHost.
    /// </summary>
    public static readonly SkillDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        CommonAgentApplicators.DotnetInspectSkillFileContent,
        embeddedResourceRoot: null,
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    /// <summary>
    /// One-time skill for completing Aspire initialization.
    /// Installed by <c>aspire init</c> to scan the repo, wire up the AppHost, and configure dependencies.
    /// </summary>
    public static readonly SkillDefinition Aspireify = new(
        CommonAgentApplicators.AspireifySkillName,
        AgentCommandStrings.SkillDescription_Aspireify,
        skillContent: null,
        embeddedResourceRoot: CommonAgentApplicators.AspireifySkillResourceRoot,
        installExcludedRelativePaths: [],
        isDefault: true);

    private SkillDefinition(string name, string description, string? skillContent, string? embeddedResourceRoot, IReadOnlyList<string> installExcludedRelativePaths, bool isDefault, IReadOnlyList<string>? applicableLanguages = null)
    {
        Name = name;
        Description = description;
        SkillContent = skillContent;
        EmbeddedResourceRoot = embeddedResourceRoot;
        InstallExcludedRelativePaths = installExcludedRelativePaths;
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages ?? [];
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

    /// <summary>
    /// Gets the set of language identifiers (from <see cref="KnownLanguageId"/>) this skill applies to.
    /// An empty list means the skill is language-agnostic and always offered.
    /// When non-empty, the skill is only offered when the detected language matches one of the entries.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Returns whether this skill is applicable for the given detected language.
    /// A skill with no <see cref="ApplicableLanguages"/> restrictions is always applicable.
    /// A skill with restrictions is only applicable when the detected language matches one of the entries.
    /// When no language is detected (<paramref name="detectedLanguage"/> is <c>null</c>), language-restricted skills are excluded.
    /// </summary>
    public bool IsApplicableToLanguage(LanguageId? detectedLanguage)
    {
        if (ApplicableLanguages.Count == 0)
        {
            return true;
        }

        if (detectedLanguage is null)
        {
            return false;
        }

        return ApplicableLanguages.Any(l => string.Equals(l, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase));
    }

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
    public static IReadOnlyList<SkillDefinition> All { get; } = [Aspire, Aspireify, PlaywrightCli, DotnetInspect];

    /// <inheritdoc />
    public override string ToString() => Name;
}
