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
internal sealed class SkillDefinition : AgentAssetDefinition
{
    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly SkillDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        skillContent: null,
        sourceKind: SkillSourceKind.ExternalInstaller, // Playwright is installed via PlaywrightCliInstaller, not a static file
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
        sourceKind: SkillSourceKind.Static,
        installExcludedRelativePaths: [],
        isDefault: false,
        applicableLanguages: [KnownLanguageId.CSharp]);

    /// <summary>
    /// Creates a skill definition sourced from the Aspire skills bundle. All bundle-sourced
    /// skills are pre-selected by default in the install prompt; callers like <c>aspire new</c>
    /// and standalone <c>aspire agent init</c> can still narrow that set with a predicate
    /// (see <c>AgentInitCommand.ExcludeOneTimeSetupSkillsFromDefaults</c>).
    /// </summary>
    internal static SkillDefinition CreateAspireSkillsBundle(
        string name,
        string description,
        IReadOnlyList<string>? installExcludedRelativePaths = null,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return new(
            name,
            description,
            skillContent: null,
            sourceKind: SkillSourceKind.AspireSkillsBundle,
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            applicableLanguages);
    }

    private SkillDefinition(string name, string description, string? skillContent, SkillSourceKind sourceKind, IReadOnlyList<string> installExcludedRelativePaths, bool isDefault, IReadOnlyList<string>? applicableLanguages = null)
        : base(name, description, installExcludedRelativePaths, isDefault, applicableLanguages)
    {
        SkillContent = skillContent;
        SourceKind = sourceKind;
    }

    /// <summary>
    /// Gets the content for the top-level SKILL.md file when the skill is defined as a single-file bundle.
    /// </summary>
    public string? SkillContent { get; }

    /// <summary>
    /// Gets where the installable files for this skill come from.
    /// </summary>
    public SkillSourceKind SourceKind { get; }

    /// <summary>
    /// Gets whether this skill has files that <c>aspire agent init</c> installs directly.
    /// </summary>
    public bool HasInstallableFiles => SkillContent is not null || SourceKind is SkillSourceKind.AspireSkillsBundle;

    /// <summary>
    /// Gets CLI-defined skills that are not sourced from the Aspire skills bundle.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> CliDefined { get; } = [PlaywrightCli, DotnetInspect];

}

/// <summary>
/// Identifies where skill files are sourced from.
/// </summary>
internal enum SkillSourceKind
{
    /// <summary>
    /// The skill is represented by static content compiled into the CLI.
    /// </summary>
    Static,

    /// <summary>
    /// The skill is installed from the external Aspire skills bundle.
    /// </summary>
    AspireSkillsBundle,

    /// <summary>
    /// The skill is managed by a dedicated external installer.
    /// </summary>
    ExternalInstaller
}
