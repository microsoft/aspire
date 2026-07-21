// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Selects the command used to delegate an AppHost launch to the Aspire CLI.
/// </summary>
public sealed class ResolveAspireCliInvocation : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Gets or sets an explicitly configured Aspire CLI path.
    /// </summary>
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// Gets or sets the requested Aspire CLI invocation mode.
    /// </summary>
    public string? AspireCliInvocationMode { get; set; }

    /// <summary>
    /// Gets or sets the <c>PATH</c> value used to locate commands.
    /// </summary>
    public string? PathEnvironmentVariable { get; set; }

    /// <summary>
    /// Gets the selected invocation mode.
    /// </summary>
    [Output]
    public string? ResolvedInvocationMode { get; set; }

    /// <summary>
    /// Gets the Aspire CLI command found on <c>PATH</c>.
    /// </summary>
    [Output]
    public string? ResolvedAspireCliPath { get; set; }

    /// <summary>
    /// Gets the DNX command found on <c>PATH</c>.
    /// </summary>
    [Output]
    public string? ResolvedDnxPath { get; set; }

    public override bool Execute()
    {
        if (!string.IsNullOrWhiteSpace(AspireCliPath))
        {
            ResolvedInvocationMode = "Aspire";
            ResolvedAspireCliPath = AspireCliPath;
            return true;
        }

        var forceDnx = string.Equals(AspireCliInvocationMode, "Dnx", StringComparison.OrdinalIgnoreCase);
        if (!forceDnx)
        {
            ResolvedAspireCliPath = CommandPathResolver.ResolveFromPath("aspire", PathEnvironmentVariable);
            if (ResolvedAspireCliPath is not null)
            {
                ResolvedInvocationMode = "Aspire";
                return true;
            }
        }

        ResolvedDnxPath = CommandPathResolver.ResolveFromPath("dnx", PathEnvironmentVariable);
        if (forceDnx || ResolvedDnxPath is not null)
        {
            // Keep DNX selected when it was explicitly requested but unavailable. The run
            // preflight emits the actionable diagnostic, while ordinary builds remain valid.
            ResolvedInvocationMode = "Dnx";
        }

        return true;
    }
}
