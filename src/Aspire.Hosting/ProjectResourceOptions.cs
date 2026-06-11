// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Various properties to modify the behavior of the project resource.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class ProjectResourceOptions
{
    /// <summary>
    /// The launch profile to use. If <c>null</c> then the default launch profile will be used.
    /// </summary>
    public string? LaunchProfileName { get; set; }
    /// <summary>
    /// If set, no launch profile will be used, and LaunchProfileName will be ignored.
    /// </summary>
    public bool ExcludeLaunchProfile { get; set; }
    /// <summary>
    /// If set, ignore endpoints coming from Kestrel configuration.
    /// </summary>
    public bool ExcludeKestrelEndpoints { get; set; }
}
