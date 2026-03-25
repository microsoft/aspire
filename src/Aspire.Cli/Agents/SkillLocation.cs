// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a location where skill files can be installed.
/// </summary>
internal sealed class SkillLocation
{
    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly SkillLocation Standard = new(
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        includeUserLevel: true);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly SkillLocation ClaudeCode = new(
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        includeUserLevel: false);

    /// <summary>
    /// VS Code / GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly SkillLocation GitHubSkills = new(
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        includeUserLevel: false);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly SkillLocation OpenCode = new(
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        includeUserLevel: false);

    private SkillLocation(string name, string description, string relativeSkillDirectory, bool isDefault, bool includeUserLevel)
    {
        Name = name;
        Description = description;
        RelativeSkillDirectory = relativeSkillDirectory;
        IsDefault = isDefault;
        IncludeUserLevel = includeUserLevel;
    }

    /// <summary>
    /// Gets the display name for this location.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown alongside the name in prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the relative skill directory path (e.g., ".agents/skills").
    /// </summary>
    public string RelativeSkillDirectory { get; }

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets whether this location also installs skill files at the user level (<c>~/</c>).
    /// </summary>
    public bool IncludeUserLevel { get; }

    /// <summary>
    /// Gets all available skill locations.
    /// </summary>
    public static IReadOnlyList<SkillLocation> All { get; } = [Standard, ClaudeCode, GitHubSkills, OpenCode];
}
