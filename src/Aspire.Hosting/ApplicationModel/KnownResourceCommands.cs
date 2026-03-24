// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides constants for well-known resource command names.
/// </summary>
public static class KnownResourceCommands
{
    // Keep in sync with CommandViewModel in Aspire.Dashboard.

    /// <summary>
    /// The command name for starting a resource.
    /// </summary>
    public static readonly string StartCommand = "start";

    /// <summary>
    /// The command name for stopping a resource.
    /// </summary>
    public static readonly string StopCommand = "stop";

    /// <summary>
    /// The command name for restarting a resource.
    /// </summary>
    public static readonly string RestartCommand = "restart";

    /// <summary>
    /// The command name for rebuilding a project resource.
    /// </summary>
    public static readonly string RebuildCommand = "rebuild";

    /// <summary>
    /// The command name for setting a parameter value.
    /// </summary>
    public static readonly string SetParameterCommand = "parameter-set";

    /// <summary>
    /// The command name for deleting a parameter value.
    /// </summary>
    public static readonly string DeleteParameterCommand = "parameter-delete";

    internal const string LegacyStartCommand = "resource-start";
    internal const string LegacyStopCommand = "resource-stop";
    internal const string LegacyRestartCommand = "resource-restart";
}
