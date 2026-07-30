// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a location where agent asset files can be installed.
/// </summary>
[DebuggerDisplay("Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}, AgentAssetKind = {AgentAssetKind}")]
internal sealed class AgentAssetLocation
{
    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation Standard = new(
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User,
        agentAssetKind: AgentAssetKind.Skill);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        agentAssetKind: AgentAssetKind.Skill);

    /// <summary>
    /// VS Code / GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        agentAssetKind: AgentAssetKind.Skill);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        agentAssetKind: AgentAssetKind.Skill);

    /// <summary>
    /// GitHub Copilot workspace extension location at <c>.github/extensions/</c>.
    /// </summary>
    public static readonly AgentAssetLocation GitHubExtensions = new(
        "workspace",
        AgentCommandStrings.ExtensionLocation_GitHubName,
        AgentCommandStrings.ExtensionLocation_GitHubDescription,
        Path.Combine(".github", "extensions"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace,
        agentAssetKind: AgentAssetKind.Extension);

    /// <summary>
    /// GitHub Copilot user extension location at <c>~/.copilot/extensions/</c>.
    /// </summary>
    public static readonly AgentAssetLocation CopilotExtensions = new(
        "user",
        AgentCommandStrings.ExtensionLocation_CopilotName,
        AgentCommandStrings.ExtensionLocation_CopilotDescription,
        Path.Combine(".copilot", "extensions"),
        isDefault: false,
        scopes: AgentAssetLocationScope.User,
        agentAssetKind: AgentAssetKind.Extension);

    private AgentAssetLocation(string id, string displayName, string description, string relativeDirectory, bool isDefault, AgentAssetLocationScope scopes, AgentAssetKind agentAssetKind)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeDirectory = relativeDirectory;
        IsDefault = isDefault;
        Scopes = scopes;
        AgentAssetKind = agentAssetKind;
    }

    /// <summary>
    /// Gets the non-localized identifier for this location, used for CLI option matching.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name for this location.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description shown alongside the name in prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the relative directory path (e.g., ".agents/skills").
    /// </summary>
    public string RelativeDirectory { get; }

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the scopes in which this location installs files.
    /// </summary>
    public AgentAssetLocationScope Scopes { get; }

    /// <summary>
    /// Gets the kind of agent asset this location is for.
    /// </summary>
    public AgentAssetKind AgentAssetKind { get; }

    /// <summary>
    /// Gets all available agent asset locations.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> All { get; } = [Standard, ClaudeCode, GitHubSkills, OpenCode, GitHubExtensions, CopilotExtensions];

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>
/// Specifies the roots against which an agent asset location is installed.
/// </summary>
[Flags]
internal enum AgentAssetLocationScope
{
    /// <summary>
    /// The asset is installed relative to the workspace root.
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// The asset is installed relative to the user home directory.
    /// </summary>
    User = 2,
}
