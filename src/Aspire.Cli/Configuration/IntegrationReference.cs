// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

/// <summary>
/// Represents a reference to an Aspire hosting integration, which can be either
/// a NuGet package (with a version) or a local project reference (with a path to a .csproj).
/// </summary>
internal sealed class IntegrationReference
{
    /// <summary>
    /// Gets the package or assembly name (e.g., "Aspire.Hosting.Redis").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the NuGet package version, or null for project references.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the absolute path to the .csproj file, or null for NuGet packages.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Returns true if this is a project reference (has a .csproj path).
    /// </summary>
    public bool IsProjectReference => ProjectPath is not null;

    /// <summary>
    /// Returns true if this is a NuGet package reference (has a version).
    /// </summary>
    public bool IsPackageReference => Version is not null;

    /// <summary>
    /// Creates a NuGet package reference.
    /// </summary>
    /// <param name="name">The package name.</param>
    /// <param name="version">The NuGet package version.</param>
    public static IntegrationReference FromPackage(string name, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(version);

        return new IntegrationReference { Name = name, Version = version };
    }

    /// <summary>
    /// Creates a local project reference.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="projectPath">The absolute path to the .csproj file.</param>
    public static IntegrationReference FromProject(string name, string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return new IntegrationReference { Name = name, ProjectPath = projectPath };
    }

}
