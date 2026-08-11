// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;

namespace Aspire.Shared;

/// <summary>
/// Resolves the Aspire Dashboard container image reference used by the Docker Compose and
/// Kubernetes publishers when they inject a dashboard into generated deployment artifacts.
/// </summary>
internal static class DashboardImage
{
    /// <summary>
    /// The Aspire Dashboard container image (without a tag), published to the .NET nightly registry.
    /// See <see href="https://mcr.microsoft.com/artifact/mar/dotnet/nightly/aspire-dashboard/about"/>.
    /// </summary>
    public const string Name = "mcr.microsoft.com/dotnet/nightly/aspire-dashboard";

    /// <summary>
    /// Resolves the tag to pin the dashboard image to, derived from the running Aspire product
    /// version's <c>major.minor</c> (for example <c>13.5</c>).
    /// </summary>
    /// <remarks>
    /// The publishers previously emitted the image without a tag, which Docker and Kubernetes both
    /// resolve to the mutable <c>:latest</c> tag. That made generated manifests non-reproducible and
    /// let the dashboard drift away from the app's Aspire version. Pinning to <c>major.minor</c> keeps
    /// the dashboard on the same Aspire line that generated the manifest and always resolves to a tag
    /// that exists on the registry — including for prerelease/CI builds, where a full
    /// <c>major.minor.patch-prerelease</c> tag is not published.
    /// </remarks>
    public static string ResolveTag()
        => ResolveTag(typeof(DashboardImage).Assembly);

    internal static string ResolveTag(Assembly assembly)
    {
        // The product version is stamped into AssemblyInformationalVersion at build time, e.g.:
        //   "13.5.0-preview.1.25111.1+ad18db0213e9db8209bca0feb83fc801f34634f5"
        // The assembly version (e.g. "13.5.0.0") is used as a fallback when it is unavailable.
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return ResolveTag(informationalVersion, assembly.GetName().Version?.ToString());
    }

    internal static string ResolveTag(string? informationalVersion, string? assemblyVersion)
    {
        if (TryGetMajorMinor(informationalVersion, out var majorMinor) ||
            TryGetMajorMinor(assemblyVersion, out majorMinor))
        {
            return majorMinor;
        }

        // Defensive only: shipped assemblies always carry a version, so this guards against a stripped
        // version attribute. Preserve the historical ":latest" behavior rather than emitting an
        // invalid tag.
        return "latest";
    }

    private static bool TryGetMajorMinor(string? version, out string majorMinor)
    {
        majorMinor = string.Empty;

        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        // Strip any prerelease label ("-preview...") and build metadata ("+sha") before splitting so
        // that both SemVer informational versions and 4-part assembly versions ("13.5.0.0") parse the
        // same way.
        var core = version.Split('-', '+')[0];
        var segments = core.Split('.');

        if (segments.Length < 2 ||
            !int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        majorMinor = string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}");
        return true;
    }
}
