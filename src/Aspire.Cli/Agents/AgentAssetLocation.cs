// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a file-system location where agent asset files can be installed.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Scopes = {Scopes}, Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetLocation
{
    private readonly Func<DirectoryInfo, IEnvironment, AgentAssetInstallTarget>? _userInstallTargetResolver;

    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation Standard = new(
        AgentAssetKind.Skill,
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User,
        defaultForClients:
        [
            AgentClientKind.CopilotCli,
            AgentClientKind.CopilotApp,
            AgentClientKind.VsCode,
            AgentClientKind.OpenCode,
        ]);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        AgentAssetKind.Skill,
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        defaultForClients: [AgentClientKind.ClaudeCode]);

    /// <summary>
    /// VS Code and GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        AgentAssetKind.Skill,
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        defaultForClients: []);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        AgentAssetKind.Skill,
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace,
        defaultForClients: []);

    private AgentAssetLocation(
        AgentAssetKind assetKind,
        string id,
        string displayName,
        string description,
        string relativeAssetDirectory,
        bool isDefault,
        AgentAssetLocationScope scopes,
        IEnumerable<AgentClientKind> defaultForClients,
        Func<DirectoryInfo, IEnvironment, AgentAssetInstallTarget>? userInstallTargetResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeAssetDirectory);
        if (scopes is AgentAssetLocationScope.None)
        {
            throw new ArgumentException("An agent asset location must support at least one scope.", nameof(scopes));
        }

        AssetKind = assetKind;
        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeAssetDirectory = relativeAssetDirectory;
        IsDefault = isDefault;
        Scopes = scopes;
        DefaultForClients = defaultForClients.ToHashSet();
        _userInstallTargetResolver = userInstallTargetResolver;
    }

    /// <summary>
    /// Gets the kind of agent asset installed at this location.
    /// </summary>
    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the non-localized identifier for this location.
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
    /// Gets the relative asset directory.
    /// </summary>
    public string RelativeAssetDirectory { get; }

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the scopes where this location installs agent assets.
    /// </summary>
    public AgentAssetLocationScope Scopes { get; }

    /// <summary>
    /// Gets the clients for which this location is the recommended default.
    /// </summary>
    public IReadOnlySet<AgentClientKind> DefaultForClients { get; }

    /// <summary>
    /// Gets all available file-system locations.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> All { get; } =
        [Standard, ClaudeCode, GitHubSkills, OpenCode];

    /// <summary>
    /// Gets the file-system locations available for the specified asset kind.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> GetLocations(AgentAssetKind assetKind)
        => All.Where(location => location.AssetKind == assetKind).ToList();

    /// <summary>
    /// Gets the recommended default locations for the detected clients.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> GetDefaultLocations(
        AgentAssetKind assetKind,
        IReadOnlyCollection<AgentClientKind> detectedClients)
    {
        var locations = GetLocations(assetKind);
        var clientLocations = locations
            .Where(location => location.DefaultForClients.Overlaps(detectedClients))
            .ToList();

        return clientLocations.Count > 0
            ? clientLocations
            : locations.Where(static location => location.IsDefault).ToList();
    }

    /// <summary>
    /// Resolves the user-scoped installation target.
    /// </summary>
    internal AgentAssetInstallTarget ResolveUserInstallTarget(DirectoryInfo homeDirectory, IEnvironment environment)
    {
        if (!Scopes.HasFlag(AgentAssetLocationScope.User))
        {
            throw new InvalidOperationException($"Agent asset location '{Id}' does not support user-level installation.");
        }

        return _userInstallTargetResolver?.Invoke(homeDirectory, environment)
            ?? new(homeDirectory, RelativeAssetDirectory, GetUserDisplayDirectory(RelativeAssetDirectory));
    }

    /// <inheritdoc />
    public override string ToString() => Id;

    private static string GetUserDisplayDirectory(string relativeAssetDirectory)
    {
        var displayRelativeAssetDirectory = relativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return $"~/{displayRelativeAssetDirectory}";
    }
}

/// <summary>
/// Represents a resolved root and relative directory for an agent asset installation.
/// </summary>
internal readonly record struct AgentAssetInstallTarget(
    DirectoryInfo RootDirectory,
    string RelativeAssetDirectory,
    string DisplayDirectory);

/// <summary>
/// Identifies where an agent asset location is rooted.
/// </summary>
[Flags]
internal enum AgentAssetLocationScope
{
    /// <summary>
    /// No location scope.
    /// </summary>
    None = 0,

    /// <summary>
    /// The current workspace.
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// The current user's home directory.
    /// </summary>
    User = 2,
}
