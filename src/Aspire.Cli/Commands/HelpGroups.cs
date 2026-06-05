// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands;

/// <summary>
/// Well-known help group categories for CLI commands.
/// </summary>
internal enum HelpGroup
{
    /// <summary>
    /// No help group. The command appears in the "Other Commands:" catch-all section.
    /// </summary>
    None,

    /// <summary>
    /// Commands for creating, running, and managing applications.
    /// </summary>
    AppCommands,

    /// <summary>
    /// Commands for managing individual resources.
    /// </summary>
    ResourceManagement,

    /// <summary>
    /// Commands for monitoring and observability.
    /// </summary>
    Monitoring,

    /// <summary>
    /// Commands for deploying applications.
    /// </summary>
    Deployment,

    /// <summary>
    /// Commands for CLI tools and configuration.
    /// </summary>
    ToolsAndConfiguration,
}
