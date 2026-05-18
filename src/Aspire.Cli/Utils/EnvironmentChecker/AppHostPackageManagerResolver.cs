// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Identifies a supported package manager for a TypeScript AppHost.
/// </summary>
internal enum AppHostPackageManager
{
    /// <summary>
    /// npm (the default package manager bundled with Node.js).
    /// </summary>
    Npm,

    /// <summary>
    /// pnpm.
    /// </summary>
    Pnpm,

    /// <summary>
    /// Bun.
    /// </summary>
    Bun,

    /// <summary>
    /// Yarn 2 or later (Berry). This is the only supported Yarn line.
    /// </summary>
    YarnBerry,

    /// <summary>
    /// Yarn Classic (v1). Detected for diagnostic purposes only; not supported by Aspire.
    /// </summary>
    YarnClassic,
}

/// <summary>
/// Describes how a package manager was resolved for a TypeScript AppHost directory.
/// </summary>
/// <param name="PackageManager">The resolved package manager.</param>
/// <param name="Source">The source that drove the resolution (e.g., the path of a lockfile or "default").</param>
/// <param name="DeclaredVersion">The version declared in the <c>packageManager</c> field of <c>package.json</c>, when available.</param>
internal sealed record AppHostPackageManagerResolution(
    AppHostPackageManager PackageManager,
    string Source,
    string? DeclaredVersion = null);

/// <summary>
/// Resolves the package manager configured for a TypeScript AppHost directory.
/// </summary>
/// <remarks>
/// The resolver mirrors the conventions documented for TypeScript AppHosts:
/// <list type="number">
///   <item>The <c>packageManager</c> field of <c>package.json</c> wins (e.g., <c>"pnpm@8.6.0"</c>).</item>
///   <item>Lockfile presence in the AppHost directory.</item>
///   <item>Lockfile presence in the immediate parent directory (for workspace-style repos).</item>
///   <item>If nothing is found, the default is npm.</item>
/// </list>
/// Markers above the immediate parent are intentionally ignored.
/// </remarks>
internal static class AppHostPackageManagerResolver
{
    /// <summary>
    /// Resolves the package manager configured for the AppHost rooted at <paramref name="appHostDirectory"/>.
    /// </summary>
    /// <param name="appHostDirectory">The directory containing <c>apphost.ts</c>.</param>
    /// <returns>The resolved package manager and the source that drove the decision.</returns>
    public static AppHostPackageManagerResolution Resolve(DirectoryInfo appHostDirectory)
    {
        ArgumentNullException.ThrowIfNull(appHostDirectory);

        // 1. packageManager field in package.json (apphost dir first, then immediate parent).
        if (TryReadPackageManagerField(appHostDirectory, out var fieldResolution))
        {
            return fieldResolution;
        }

        var parent = appHostDirectory.Parent;
        if (parent is not null && TryReadPackageManagerField(parent, out var parentFieldResolution))
        {
            return parentFieldResolution;
        }

        // 2. Lockfile detection in the AppHost directory.
        if (TryDetectFromLockfiles(appHostDirectory, out var lockfileResolution))
        {
            return lockfileResolution;
        }

        // 3. Lockfile detection in the immediate parent directory (workspace-style).
        if (parent is not null && TryDetectFromLockfiles(parent, out var parentLockfileResolution))
        {
            return parentLockfileResolution;
        }

        // 4. Default to npm.
        return new AppHostPackageManagerResolution(AppHostPackageManager.Npm, "default");
    }

    /// <summary>
    /// Gets the conventional executable name for a package manager.
    /// </summary>
    public static string GetExecutableName(AppHostPackageManager packageManager)
    {
        return packageManager switch
        {
            AppHostPackageManager.Npm => "npm",
            AppHostPackageManager.Pnpm => "pnpm",
            AppHostPackageManager.Bun => "bun",
            AppHostPackageManager.YarnBerry or AppHostPackageManager.YarnClassic => "yarn",
            _ => throw new ArgumentOutOfRangeException(nameof(packageManager), packageManager, "Unknown package manager."),
        };
    }

    private static bool TryReadPackageManagerField(DirectoryInfo directory, out AppHostPackageManagerResolution resolution)
    {
        var packageJsonPath = Path.Combine(directory.FullName, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            resolution = null!;
            return false;
        }

        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (node is not JsonObject obj)
            {
                resolution = null!;
                return false;
            }

            if (!obj.TryGetPropertyValue("packageManager", out var packageManagerNode) ||
                packageManagerNode?.GetValue<string>() is not { Length: > 0 } value)
            {
                resolution = null!;
                return false;
            }

            // The packageManager field follows the form "<name>@<version>[+<sha-256-hash>]".
            var atIndex = value.IndexOf('@');
            var name = atIndex < 0 ? value : value[..atIndex];
            var version = atIndex < 0 || atIndex == value.Length - 1
                ? null
                : value[(atIndex + 1)..];

            // Strip integrity suffix if present (e.g., "@8.6.0+sha256:abc...").
            if (version is not null)
            {
                var plusIndex = version.IndexOf('+');
                if (plusIndex >= 0)
                {
                    version = version[..plusIndex];
                }
            }

            var packageManager = name.Trim().ToLowerInvariant() switch
            {
                "npm" => AppHostPackageManager.Npm,
                "pnpm" => AppHostPackageManager.Pnpm,
                "bun" => AppHostPackageManager.Bun,
                "yarn" => ClassifyYarn(version, directory),
                _ => (AppHostPackageManager?)null,
            };

            if (packageManager is null)
            {
                resolution = null!;
                return false;
            }

            resolution = new AppHostPackageManagerResolution(
                packageManager.Value,
                $"packageManager field in {packageJsonPath}",
                version);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            resolution = null!;
            return false;
        }
    }

    private static bool TryDetectFromLockfiles(DirectoryInfo directory, out AppHostPackageManagerResolution resolution)
    {
        // Order matters: most-specific markers first.
        var pnpmLock = Path.Combine(directory.FullName, "pnpm-lock.yaml");
        if (File.Exists(pnpmLock))
        {
            resolution = new AppHostPackageManagerResolution(AppHostPackageManager.Pnpm, pnpmLock);
            return true;
        }

        var bunLock = Path.Combine(directory.FullName, "bun.lock");
        if (File.Exists(bunLock))
        {
            resolution = new AppHostPackageManagerResolution(AppHostPackageManager.Bun, bunLock);
            return true;
        }

        var bunLockb = Path.Combine(directory.FullName, "bun.lockb");
        if (File.Exists(bunLockb))
        {
            resolution = new AppHostPackageManagerResolution(AppHostPackageManager.Bun, bunLockb);
            return true;
        }

        var yarnLock = Path.Combine(directory.FullName, "yarn.lock");
        if (File.Exists(yarnLock))
        {
            var yarn = ClassifyYarn(version: null, directory);
            resolution = new AppHostPackageManagerResolution(yarn, yarnLock);
            return true;
        }

        var npmLock = Path.Combine(directory.FullName, "package-lock.json");
        if (File.Exists(npmLock))
        {
            resolution = new AppHostPackageManagerResolution(AppHostPackageManager.Npm, npmLock);
            return true;
        }

        resolution = null!;
        return false;
    }

    private static AppHostPackageManager ClassifyYarn(string? version, DirectoryInfo directory)
    {
        // Explicit version from packageManager field is the strongest signal.
        if (!string.IsNullOrWhiteSpace(version))
        {
            // Accept SemVer-like prefixes (e.g., "4.1.0", "4.1.0-rc.1").
            var dotIndex = version.IndexOf('.');
            var majorText = dotIndex < 0 ? version : version[..dotIndex];
            if (int.TryParse(majorText, out var major))
            {
                return major >= 2 ? AppHostPackageManager.YarnBerry : AppHostPackageManager.YarnClassic;
            }
        }

        // Berry projects ship .yarnrc.yml and/or a .yarn/ directory at the AppHost root.
        if (File.Exists(Path.Combine(directory.FullName, ".yarnrc.yml")) ||
            Directory.Exists(Path.Combine(directory.FullName, ".yarn")))
        {
            return AppHostPackageManager.YarnBerry;
        }

        return AppHostPackageManager.YarnClassic;
    }
}
