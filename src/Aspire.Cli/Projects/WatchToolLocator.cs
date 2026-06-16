// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Shared;

namespace Aspire.Cli.Projects;

/// <summary>
/// Locates the bundled watch tool (<c>Microsoft.DotNet.HotReload.Watch.Aspire</c>) 
/// for inner-loop development scenarios where no CLI bundle layout exists 
/// — e.g. running <c>dotnet run --project src/Aspire.Cli</c> from an Aspire repo checkout. 
/// In a normal installed CLI the tool ships inside the bundle's <c>watch/</c> directory 
/// and is resolved from the layout instead.
/// </summary>
internal static class WatchToolLocator
{
    private const string WatchVersionPropertyName = "MicrosoftDotNetHotReloadWatchAspireVersion";

    /// <summary>
    /// Resolves the watch tool entry-point DLL from the NuGet global packages cache, 
    /// pinned to the version declared in the repo's <c>eng/Versions.props</c>. Returns <c>null</c> when
    /// <paramref name="repoRoot"/> is empty (not running from a repo), or when the pinned package
    /// has not been restored into the cache yet.
    /// </summary>
    /// <param name="repoRoot">The Aspire repository root, or <c>null</c>/empty when not in repo mode.</param>
    public static string? TryGetRepoLocalWatchToolPath(string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return null;
        }

        var pinnedVersion = TryReadPinnedWatchVersion(repoRoot);
        return BundleDiscovery.TryGetWatchToolPathFromNuGetCache(pinnedVersion);
    }

    private static string? TryReadPinnedWatchVersion(string repoRoot)
    {
        try
        {
            var versionsPropsPath = Path.Combine(repoRoot, "eng", "Versions.props");
            if (!File.Exists(versionsPropsPath))
            {
                return null;
            }

            var doc = XDocument.Load(versionsPropsPath);
            return doc.Descendants(WatchVersionPropertyName).FirstOrDefault()?.Value;
        }
        catch
        {
            return null;
        }
    }
}
