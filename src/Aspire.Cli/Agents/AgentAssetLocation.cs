// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a file-system location where agent asset files can be installed.
/// </summary>
[DebuggerDisplay("Scopes = {Scopes}, Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetLocation
{
    public AgentAssetLocation(
        string id,
        string displayName,
        string description,
        string relativeAssetDirectory,
        bool isDefault,
        AgentAssetLocationScope scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeAssetDirectory);
        if (scopes is AgentAssetLocationScope.None)
        {
            throw new ArgumentException("An agent asset location must support at least one scope.", nameof(scopes));
        }

        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeAssetDirectory = relativeAssetDirectory;
        IsDefault = isDefault;
        Scopes = scopes;
    }

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

    /// <inheritdoc />
    public override string ToString() => Id;
}

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
