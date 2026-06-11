// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Ats;

/// <summary>
/// Options for creating a distributed application builder from polyglot apphosts.
/// </summary>
[AspireDto]
internal sealed class CreateBuilderOptions
{
    /// <summary>
    /// The command line arguments.
    /// </summary>
    public string[]? Args { get; set; }

    /// <summary>
    /// The directory containing the AppHost project file.
    /// </summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>
    /// The full path to the AppHost file (e.g., apphost.ts, apphost.py).
    /// Used for consistent socket path computation across CLI and AppHost.
    /// </summary>
    public string? AppHostFilePath { get; set; }

    /// <summary>
    /// When containers are used, use this value to override the container registry.
    /// </summary>
    public string? ContainerRegistryOverride { get; set; }

    /// <summary>
    /// Determines whether the dashboard is disabled.
    /// </summary>
    public bool DisableDashboard { get; set; }

    /// <summary>
    /// The application name to display in the dashboard.
    /// </summary>
    public string? DashboardApplicationName { get; set; }

    /// <summary>
    /// Allows the use of HTTP urls for the AppHost resource endpoint.
    /// </summary>
    public bool AllowUnsecuredTransport { get; set; }

    /// <summary>
    /// Enables resource logging.
    /// </summary>
    public bool EnableResourceLogging { get; set; }
}
