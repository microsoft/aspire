// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that runs a command from an npm package using npx.
/// </summary>
public class NpxResource : ExecutableResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NpxResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="packageName">The name of the npm package to execute.</param>
    public NpxResource(string name, string packageName)
        : base(name, "npx", ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        var (parsedPackageName, version) = ParsePackageSpec(packageName);
        Annotations.Add(new NpxResourceConfiguration { PackageName = parsedPackageName, Version = version });
    }

    internal NpxResourceConfiguration Configuration =>
        Annotations.OfType<NpxResourceConfiguration>().LastOrDefault()
        ?? throw new InvalidOperationException("Unable to find NpxResourceConfiguration on resource.");

    internal static (string PackageName, string? Version) ParsePackageSpec(string packageSpec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSpec);

        var versionSeparator = packageSpec[0] == '@'
            ? packageSpec.IndexOf('@', packageSpec.IndexOf('/') + 1)
            : packageSpec.IndexOf('@');

        if (versionSeparator < 0)
        {
            return (packageSpec, null);
        }

        var packageName = packageSpec[..versionSeparator];
        var version = packageSpec[(versionSeparator + 1)..];
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("The package spec must include a package name and a non-empty version.", nameof(packageSpec));
        }

        return (packageName, version);
    }
}

internal sealed class NpxResourceConfiguration : IResourceAnnotation
{
    public required string PackageName { get; set; }

    public string? Version { get; set; }

    public string? Executable { get; set; }

    public string? Registry { get; set; }

    public NpxCachePreference CachePreference { get; set; }

    public List<string> NpxArgs { get; } = [];
}

internal enum NpxCachePreference
{
    Default,
    Offline,
    PreferOffline,
    PreferOnline
}
