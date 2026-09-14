// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// A validated bundle of agent skills or extensions.
/// </summary>
internal sealed class AspireSkillsBundle
{
    internal AspireSkillsBundle(
        string version,
        AgentAssetKind assetKind,
        IReadOnlyList<AgentAssetDefinition> assets)
    {
        Version = version;
        AssetKind = assetKind;
        Assets = [.. assets];
    }

    public string Version { get; }

    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the validated assets and their resolved installable files.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> Assets { get; }
}
