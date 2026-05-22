// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes the outcome of resolving the Aspire skills bundle.
/// </summary>
internal enum AspireSkillsInstallStatus
{
    /// <summary>
    /// The bundle is available locally and can be installed into skill locations.
    /// </summary>
    Installed,

    /// <summary>
    /// An agent-native plugin manager already provides the Aspire skills.
    /// </summary>
    PluginDetected,

    /// <summary>
    /// The bundle could not be resolved, verified, or cached.
    /// </summary>
    Failed
}

/// <summary>
/// Represents the result of resolving the Aspire skills bundle.
/// </summary>
internal sealed record AspireSkillsInstallResult(AspireSkillsInstallStatus Status, AspireSkillsBundle? Bundle, string? Message)
{
    public static AspireSkillsInstallResult Installed(AspireSkillsBundle bundle) => new(AspireSkillsInstallStatus.Installed, bundle, Message: null);

    public static AspireSkillsInstallResult PluginDetected(string message) => new(AspireSkillsInstallStatus.PluginDetected, Bundle: null, message);

    public static AspireSkillsInstallResult Failed(string message) => new(AspireSkillsInstallStatus.Failed, Bundle: null, message);
}
