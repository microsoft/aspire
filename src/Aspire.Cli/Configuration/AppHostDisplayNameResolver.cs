// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Configuration;

/// <summary>
/// Resolves the friendly display name configured for a running AppHost in <c>aspire.config.json</c>.
/// </summary>
/// <remarks>
/// <para>The name comes from the nearest <c>aspire.config.json</c> at or above the AppHost's directory,
/// using the same nearest-wins rule as AppHost discovery:</para>
/// <code>
/// {
///   "appHost": { "path": "src/apphost.cs", "name": "Contoso Shop" }
/// }
/// </code>
/// <para>The name applies only when that file identifies this AppHost: its <c>appHost.path</c> resolves to
/// the AppHost file, or it has no path and sits in the AppHost's own directory. A repository-level file that
/// names a different AppHost must not rename every AppHost below it.</para>
/// </remarks>
internal static class AppHostDisplayNameResolver
{
    /// <summary>
    /// Gets the configured display name for the AppHost at <paramref name="appHostPath"/>.
    /// </summary>
    /// <param name="appHostPath">The fully qualified AppHost file path reported by the AppHost, such as <c>/repo/src/apphost.cs</c>.</param>
    /// <returns>The trimmed configured name, or <see langword="null"/> when none applies.</returns>
    public static string? GetConfiguredName(string appHostPath)
    {
        if (!Path.IsPathFullyQualified(appHostPath) || Path.GetDirectoryName(appHostPath) is not { Length: > 0 } appHostDirectory)
        {
            return null;
        }

        try
        {
            for (var directory = new DirectoryInfo(appHostDirectory); directory is not null; directory = directory.Parent)
            {
                var config = AspireConfigFile.Load(directory.FullName);
                if (config is null)
                {
                    continue;
                }

                return AppliesTo(config, directory.FullName, appHostDirectory, appHostPath) && !string.IsNullOrWhiteSpace(config.AppHost?.Name)
                    ? config.AppHost.Name.Trim()
                    : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The name is cosmetic. A malformed or unreadable config must not break discovery;
            // callers fall back to a name derived from the path.
        }

        return null;
    }

    private static bool AppliesTo(AspireConfigFile config, string configDirectory, string appHostDirectory, string appHostPath)
    {
        if (string.IsNullOrEmpty(config.AppHost?.Path))
        {
            return string.Equals(
                PathNormalizer.ResolveToFilesystemPath(configDirectory),
                PathNormalizer.ResolveToFilesystemPath(appHostDirectory),
                StringComparisons.FileSystemPath);
        }

        // appHost.path is stored with '/' separators relative to the config file's directory; Path
        // accepts '/' on every platform. Don't pre-normalize it with GetFullPath, which would resolve
        // it against the process's current directory instead of the config directory.
        var configuredAppHostPath = Path.GetFullPath(Path.Combine(configDirectory, config.AppHost.Path));
        return string.Equals(
            PathNormalizer.ResolveToFilesystemPath(configuredAppHostPath),
            PathNormalizer.ResolveToFilesystemPath(appHostPath),
            StringComparisons.FileSystemPath);
    }
}
