// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies an agent asset kind.
/// </summary>
internal enum AgentAssetKind
{
    /// <summary>
    /// Agent skills.
    /// </summary>
    Skill,

    /// <summary>
    /// Model Context Protocol server configuration.
    /// </summary>
    Mcp,

    /// <summary>
    /// Agent extensions.
    /// </summary>
    Extension,
}

/// <summary>
/// Identifies how an agent asset is installed or configured.
/// </summary>
internal enum AgentAssetBackingKind
{
    /// <summary>
    /// The asset is installed as files.
    /// </summary>
    File,

    /// <summary>
    /// The asset is configured through detected environment actions.
    /// </summary>
    Action,
}

internal static class AgentAssetKindExtensions
{
    /// <summary>
    /// Gets the backing kind for an agent asset kind.
    /// </summary>
    public static AgentAssetBackingKind GetBackingKind(this AgentAssetKind assetKind)
        => assetKind switch
        {
            AgentAssetKind.Skill or AgentAssetKind.Extension => AgentAssetBackingKind.File,
            AgentAssetKind.Mcp => AgentAssetBackingKind.Action,
            _ => throw new ArgumentOutOfRangeException(nameof(assetKind), assetKind, "Unknown agent asset kind."),
        };
}
