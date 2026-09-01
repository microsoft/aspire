// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes the outcome of resolving an Aspire Skills bundle.
/// </summary>
internal enum AspireSkillsInstallStatus
{
    /// <summary>
    /// The bundle is available locally and can be installed into agent asset locations.
    /// </summary>
    Installed,

    /// <summary>
    /// No bundle provider is registered for the requested asset kind.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The bundle could not be resolved, verified, or cached.
    /// </summary>
    Failed
}

/// <summary>
/// Represents the result of resolving an Aspire Skills bundle.
/// </summary>
internal sealed record AspireSkillsInstallResult(AspireSkillsInstallStatus Status, AspireSkillsBundle? Bundle, string? Message)
{
    public static AspireSkillsInstallResult Installed(AspireSkillsBundle bundle) => new(AspireSkillsInstallStatus.Installed, bundle, Message: null);

    public static AspireSkillsInstallResult Unavailable { get; } = new(AspireSkillsInstallStatus.Unavailable, Bundle: null, Message: null);

    public static AspireSkillsInstallResult Failed(string message) => new(AspireSkillsInstallStatus.Failed, Bundle: null, message);
}
