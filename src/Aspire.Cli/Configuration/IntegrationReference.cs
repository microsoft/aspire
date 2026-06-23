// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

/// <summary>
/// Identifies the source an <see cref="IntegrationReference"/> was loaded from.
/// Each source has its own required fields (see <see cref="IntegrationReference"/>).
/// Adding a new ecosystem means one enum value plus one parsing branch in
/// <see cref="AspireConfigFile.GetIntegrationReferences"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntegrationSource>))]
internal enum IntegrationSource
{
    /// <summary>NuGet package. Uses <see cref="IntegrationReference.Version"/>.</summary>
    Nuget,

    /// <summary>Local .NET project reference. Uses <see cref="IntegrationReference.Path"/> (absolute csproj path).</summary>
    Project,

    /// <summary>npm integration host package. Uses <see cref="IntegrationReference.Path"/> (absolute host entry-point path).</summary>
    Npm,
}

/// <summary>
/// Represents a reference to an Aspire hosting integration. The <see cref="Source"/>
/// discriminator identifies the ecosystem; per-source fields carry the rest.
/// </summary>
internal sealed class IntegrationReference
{
    /// <summary>
    /// Gets the package or assembly name (e.g., <c>Aspire.Hosting.Redis</c>, <c>@spike/aspire-kafka</c>).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the source ecosystem this reference came from. Drives which fields are populated
    /// and which downstream code path handles it.
    /// </summary>
    public required IntegrationSource Source { get; init; }

    /// <summary>
    /// Gets the NuGet package version. Non-null only when <see cref="Source"/> is <see cref="IntegrationSource.Nuget"/>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the absolute path to the reference's on-disk entry point:
    /// the <c>.csproj</c> file for <see cref="IntegrationSource.Project"/>,
    /// or the integration host entry-point file for <see cref="IntegrationSource.Npm"/>.
    /// Null for <see cref="IntegrationSource.Nuget"/>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Creates a NuGet package reference.
    /// </summary>
    public static IntegrationReference FromPackage(string name, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(version);

        return new IntegrationReference { Name = name, Source = IntegrationSource.Nuget, Version = version };
    }

    /// <summary>
    /// Creates a local .NET project reference.
    /// </summary>
    public static IntegrationReference FromProject(string name, string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return new IntegrationReference { Name = name, Source = IntegrationSource.Project, Path = projectPath };
    }

    /// <summary>
    /// Creates an npm integration host reference.
    /// </summary>
    public static IntegrationReference FromNpm(string name, string hostPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(hostPath);

        return new IntegrationReference { Name = name, Source = IntegrationSource.Npm, Path = hostPath };
    }
}
