// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Resolves and verifies the external Aspire skills bundle.
/// </summary>
internal interface IAspireSkillsInstaller
{
    /// <summary>
    /// Ensures the Aspire skills bundle is available in the local cache.
    /// </summary>
    Task<AspireSkillsInstallResult> InstallAsync(CancellationToken cancellationToken);
}
