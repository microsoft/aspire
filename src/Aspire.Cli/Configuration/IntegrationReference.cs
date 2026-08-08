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
    /// Gets a value indicating whether <see cref="Version"/> must resolve to exactly that version.
    /// </summary>
    /// <remarks>
    /// A bare NuGet version is a <em>minimum</em>, not an equality: <c>13.5.0</c> means
    /// <c>[13.5.0, )</c> and resolves to the nearest version at or above it, so a version that is
    /// missing from the feed silently restores as a later one. Only <c>[13.5.0]</c> pins a single
    /// version. Callers that publish artifacts keyed on the requested version — <c>aspire sdk
    /// export</c> — set this so an unavailable version fails the restore instead of being described
    /// under the wrong number. Everything else keeps the minimum form, which is what lets a shared
    /// transitive dependency unify.
    /// See https://learn.microsoft.com/nuget/concepts/package-versioning#version-ranges.
    /// </remarks>
    public bool RequireExactVersion { get; init; }

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
    /// Creates a NuGet package reference that must restore at exactly <paramref name="version"/>.
    /// </summary>
    /// <param name="name">The package name.</param>
    /// <param name="version">The NuGet package version.</param>
    /// <seealso cref="RequireExactVersion"/>
    public static IntegrationReference FromExactPackage(string name, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(version);

        return new IntegrationReference { Name = name, Version = version, RequireExactVersion = true };
    }

    /// <summary>
    /// Gets the NuGet version range to restore this reference with.
    /// </summary>
    /// <param name="forceExact">
    /// Pins the version even when <see cref="RequireExactVersion"/> is not set, for callers that
    /// decide exactness from context rather than from the reference (for example, restoring Aspire
    /// packages from an explicit <c>--source</c>).
    /// </param>
    /// <returns>Either the version as written, or <c>[version]</c> when it has to be pinned.</returns>
    public string GetRestoreVersionRange(bool forceExact)
    {
        if (Version is null)
        {
            throw new InvalidOperationException($"Integration '{Name}' is a project reference and has no version to restore.");
        }

        // An explicit range the caller already wrote (`[1.2.3]`, `(1.0,2.0)`) is left alone: wrapping
        // it again would produce a syntactically invalid range.
        if (!(forceExact || RequireExactVersion) || Version.Length == 0 || Version[0] is '[' or '(')
        {
            return Version;
        }

        return $"[{Version}]";
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
