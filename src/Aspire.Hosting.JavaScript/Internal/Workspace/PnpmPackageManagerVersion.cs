// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class PnpmPackageManagerVersion
{
    /// <summary>
    /// Reads the pinned pnpm major version from the workspace root's package.json
    /// (<c>packageManager</c> / <c>devEngines.packageManager</c>), or <see langword="null"/>
    /// when no exact pnpm version is pinned.
    /// </summary>
    public static int? TryReadMajorVersion(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);

        var packageManager = TryReadPackageManagerField(workspaceRoot);
        if (packageManager is null)
        {
            return null;
        }

        return TryParseMajorVersion(packageManager);
    }

    /// <summary>
    /// Parses the major version from a <c>pnpm@&lt;version&gt;</c> package-manager string,
    /// or <see langword="null"/> when the string is not a pnpm pin or the major cannot be parsed.
    /// </summary>
    public static int? TryParseMajorVersion(string packageManager)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageManager);

        const string Prefix = "pnpm@";
        if (!packageManager.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var versionStart = Prefix.Length;
        var versionEnd = packageManager.IndexOfAny(['.', '-', '+'], versionStart);
        var majorText = versionEnd < 0
            ? packageManager[versionStart..]
            : packageManager[versionStart..versionEnd];
        return int.TryParse(majorText, out var major) ? major : null;
    }

    private static string? TryReadPackageManagerField(string rootPath)
    {
        var path = Path.Combine(rootPath, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var packageJson = JsonSerializer.Deserialize<PackageJsonPackageManagerInfo>(stream);

            // Corepack treats top-level packageManager as the primary package-manager contract.
            // If that is absent, it can fall back to devEngines.packageManager:
            // https://nodejs.org/api/corepack.html#devenginespackagemanager
            //
            // Raw shape:
            // "devEngines": {
            //   "packageManager": {
            //     "name": "pnpm",
            //     "version": "11.0.8+sha224..."
            //   }
            // }
            //
            // Only exact pnpm versions are useful for Dockerfile deploy-mode routing. Ranges
            // such as ">=11.0.0" intentionally remain unknown and use the compatibility path.
            if (packageJson?.PackageManager is { } packageManager)
            {
                return packageManager;
            }

            if (packageJson?.DevEngines?.PackageManager is { Version: { } versionString } devEnginesPackageManager &&
                string.Equals(devEnginesPackageManager.Name, "pnpm", StringComparison.Ordinal))
            {
                return "pnpm@" + versionString;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        return null;
    }

    private sealed class PackageJsonPackageManagerInfo
    {
        [JsonPropertyName("packageManager")]
        public string? PackageManager { get; set; }

        [JsonPropertyName("devEngines")]
        public DevEnginesInfo? DevEngines { get; set; }
    }

    private sealed class DevEnginesInfo
    {
        [JsonPropertyName("packageManager")]
        public DevEnginesPackageManagerInfo? PackageManager { get; set; }
    }

    private sealed class DevEnginesPackageManagerInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
