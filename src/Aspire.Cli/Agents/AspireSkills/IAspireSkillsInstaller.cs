// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Resolves and verifies Aspire-skills bundles.
/// </summary>
internal interface IAspireSkillsInstaller
{
    /// <summary>
    /// Gets whether an Aspire-skills bundle is registered for the specified asset kind.
    /// </summary>
    bool HasBundle(AgentAssetKind assetKind);

    /// <summary>
    /// Ensures the Aspire-skills bundle for the specified asset kind is available in the local cache.
    /// </summary>
    Task<AspireSkillsInstallResult> InstallAsync(
        AgentAssetKind assetKind,
        CancellationToken cancellationToken);
}
