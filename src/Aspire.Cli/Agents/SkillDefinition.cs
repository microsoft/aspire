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
        CommonAgentApplicators.SkillFileContent,
        isDefault: true);

    /// <summary>
    /// The Playwright CLI skill for browser automation.
    /// </summary>
    public static readonly SkillDefinition PlaywrightCli = new(
        "playwright-cli",
        AgentCommandStrings.SkillDescription_PlaywrightCli,
        skillContent: null, // Playwright is installed via PlaywrightCliInstaller, not a static file
        isDefault: true);

    /// <summary>
    /// The dotnet-inspect skill for querying .NET API surfaces.
    /// </summary>
    public static readonly SkillDefinition DotnetInspect = new(
        CommonAgentApplicators.DotnetInspectSkillName,
        AgentCommandStrings.SkillDescription_DotnetInspect,
        CommonAgentApplicators.DotnetInspectSkillFileContent,
        isDefault: true);

    private SkillDefinition(string name, string description, string? skillContent, bool isDefault)
    {
        Name = name;
        Description = description;
        SkillContent = skillContent;
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
    /// Gets the content for the SKILL.md file, or <c>null</c> if this skill is installed by other means.
    /// </summary>
    public string? SkillContent { get; }

    /// <summary>
    /// Gets whether this skill should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets all available skill definitions.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> All { get; } = [Aspire, PlaywrightCli, DotnetInspect];
}
