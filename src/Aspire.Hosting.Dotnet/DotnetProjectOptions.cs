// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Launch options for a .NET project in a polyglot AppHost.
/// </summary>
// Keep the shared ProjectResourceOptions handle unchanged for legacy project APIs.
[AspireDto]
internal sealed class DotnetProjectOptions
{
    /// <summary>
    /// The launch profile to use. If omitted or null, the default launch profile is used.
    /// </summary>
    public string? LaunchProfileName { get; init; }

    /// <summary>
    /// Whether to disable launch profiles. When true, the launch profile name is ignored.
    /// </summary>
    public bool ExcludeLaunchProfile { get; init; }

    /// <summary>
    /// Whether to ignore endpoints from Kestrel configuration.
    /// </summary>
    public bool ExcludeKestrelEndpoints { get; init; }
}
