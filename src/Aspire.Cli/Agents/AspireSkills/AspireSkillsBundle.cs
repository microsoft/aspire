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
        if (assets.Any(asset => asset.AssetKind != assetKind))
        {
            throw new ArgumentException("All bundle assets must have the bundle's asset kind.", nameof(assets));
        }

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
