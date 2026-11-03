// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a skill that can be installed into a skill location.
/// </summary>
[DebuggerDisplay("Name = {Name}, Description = {Description}")]
internal sealed class SkillDefinition
{
    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly SkillDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        skillContent: null);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// Only offered when the workspace contains a .NET AppHost.
    /// </summary>
    public static readonly SkillDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        CommonAgentApplicators.DotnetInspectSkillFileContent,
        applicableLanguages: [KnownLanguageId.CSharp]);

    private SkillDefinition(
        string name,
        string description,
        string? skillContent,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        Name = name;
        Description = description;
        SkillContent = skillContent;
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
    /// Gets the content for the top-level SKILL.md file when the skill is defined as a single-file bundle.
    /// </summary>
    public string? SkillContent { get; }

    /// <summary>
    /// Gets whether this skill has files that <c>aspire agent init</c> installs directly.
    /// </summary>
    public bool HasInstallableFiles => SkillContent is not null;

    /// <summary>
    /// Gets the set of language identifiers this skill applies to.
    /// An empty list means the skill is language-agnostic.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Returns whether this skill applies to the detected AppHost language.
    /// </summary>
    public bool IsApplicableToLanguage(LanguageId? detectedLanguage)
    {
        if (ApplicableLanguages.Count == 0)
        {
            return true;
        }

        return detectedLanguage is not null &&
            ApplicableLanguages.Any(language => string.Equals(
                language,
                detectedLanguage.Value.Value,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns whether this skill has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <summary>
    /// Gets the skills whose files are managed directly by the CLI.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> CliDefined { get; } = [PlaywrightCli, DotnetInspect];

    /// <inheritdoc />
    public override string ToString() => Name;
}
