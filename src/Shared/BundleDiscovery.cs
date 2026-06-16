// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is source-linked into multiple projects:
// - Aspire.Hosting
// - Aspire.Cli
// - Aspire.Managed
// - CreateLayout
// Do not add project-specific dependencies.

using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace Aspire.Shared;

/// <summary>
/// Shared logic for discovering Aspire bundle components.
/// Used by both CLI and Aspire.Hosting to ensure consistent discovery behavior.
/// </summary>
internal static class BundleDiscovery
{
    // ═══════════════════════════════════════════════════════════════════════
    // ENVIRONMENT VARIABLE CONSTANTS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Environment variable for the root of the bundle layout.
    /// </summary>
    public const string LayoutPathEnvVar = "ASPIRE_LAYOUT_PATH";

    /// <summary>
    /// Environment variable for overriding the DCP path.
    /// </summary>
    public const string DcpPathEnvVar = "ASPIRE_DCP_PATH";

    /// <summary>
    /// Environment variable for overriding the Dashboard path.
    /// Still used by DcpOptions/DashboardEventHandlers — value now points to aspire-managed exe.
    /// </summary>
    public const string DashboardPathEnvVar = "ASPIRE_DASHBOARD_PATH";

    /// <summary>
    /// Environment variable for overriding the aspire-managed path.
    /// </summary>
    public const string ManagedPathEnvVar = "ASPIRE_MANAGED_PATH";

    /// <summary>
    /// Environment variable for the terminal host binary path. Read by Aspire.Hosting
    /// to resolve the binary that backs <c>WithTerminal()</c> resources. Injected by
    /// the CLI at launch time pointing at the bundle's <c>aspire-managed</c> exe.
    /// </summary>
    public const string TerminalHostPathEnvVar = "ASPIRE_TERMINAL_HOST_PATH";

    /// <summary>
    /// Environment variable for the invocation args prepended when launching the
    /// terminal host binary. Set to <c>"terminalhost"</c> when the binary is the
    /// multi-mode <c>aspire-managed</c> exe so the dispatcher routes to the
    /// terminal host subcommand. Treated as a pair with
    /// <see cref="TerminalHostPathEnvVar"/>: callers that synthesize one without
    /// the other can produce a launch failure.
    /// </summary>
    public const string TerminalHostInvocationArgsEnvVar = "ASPIRE_TERMINAL_HOST_INVOCATION_ARGS";

    /// <summary>
    /// Environment variable for the bundled watch tool DLL path 
    /// (<c>Microsoft.DotNet.HotReload.Watch.Aspire.dll</c>). 
    /// </summary>
    public const string WatchToolPathEnvVar = "ASPIRE_WATCH_TOOL_PATH";

    /// <summary>
    /// Environment variable for the .NET SDK base path the watch tool should target
    /// (via its <c>--sdk</c> argument). 
    /// </summary>
    /// <remarks>
    /// This injection is best-effort (set only when the CLI can derive it via pure path probing, 
    /// e.g. a private SDK install). When it is absent, the app model resolves the SDK base path itself 
    /// via a lazy, memoized <c>dotnet --info</c> command spawned at watch-server launch.
    /// </remarks>
    public const string WatchSdkPathEnvVar = "ASPIRE_WATCH_SDK_PATH";

    /// <summary>
    /// Environment variable containing the leased version directory for bundle-owned child processes.
    /// </summary>
    public const string BundleVersionDirectoryEnvVar = "ASPIRE_BUNDLE_VERSION_DIR";

    /// <summary>
    /// Environment variable to force SDK mode (skip bundle detection).
    /// </summary>
    public const string UseGlobalDotNetEnvVar = "ASPIRE_USE_GLOBAL_DOTNET";

    /// <summary>
    /// Environment variable indicating development mode (Aspire repo checkout).
    /// </summary>
    public const string RepoRootEnvVar = "ASPIRE_REPO_ROOT";

    // ═══════════════════════════════════════════════════════════════════════
    // BUNDLE LAYOUT DIRECTORY NAMES
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Directory name for DCP in the bundle layout.
    /// </summary>
    public const string DcpDirectoryName = "dcp";

    /// <summary>
    /// Directory name for the managed binary in the bundle layout.
    /// </summary>
    public const string ManagedDirectoryName = "managed";

    /// <summary>
    /// Directory name for the single top-level reparse point that links to the
    /// active versioned bundle directory. Components (<c>managed/</c> and <c>dcp/</c>)
    /// are resolved as subdirectories of this link target.
    /// </summary>
    public const string BundleDirectoryName = "bundle";

    /// <summary>
    /// Directory name for the bundled watch tool (Microsoft.DotNet.HotReload.Watch.Aspire)
    /// in the bundle layout. 
    /// </summary>
    public const string WatchDirectoryName = "watch";

    // ═══════════════════════════════════════════════════════════════════════
    // EXECUTABLE NAMES (without path, just the file name)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executable name for the unified managed binary.
    /// </summary>
    public const string ManagedExecutableName = "aspire-managed";

    /// <summary>
    /// Assembly file name for the bundled watch tool. 
    /// </summary>
    public const string WatchToolDllName = "Microsoft.DotNet.HotReload.Watch.Aspire.dll";

    // Name of the Nuget cache folder where the watch tool is stored.
    internal const string WatchToolNugetCacheFolder = "microsoft.dotnet.hotreload.watch.aspire";
    // Watch tool .NET (target) version
    internal const string WatchToolDotNetVersion = "net10.0";

    // ═══════════════════════════════════════════════════════════════════════
    // DISCOVERY METHODS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to discover DCP from a base directory.
    /// Checks for the expected bundle layout structure.
    /// </summary>
    /// <param name="baseDirectory">The base directory to search from (e.g., CLI location or entry assembly directory).</param>
    /// <param name="dcpCliPath">The full path to the DCP executable if found.</param>
    /// <param name="dcpExtensionsPath">The full path to the DCP extensions directory if found.</param>
    /// <param name="dcpBinPath">The full path to the DCP bin directory if found.</param>
    /// <returns>True if DCP was found, false otherwise.</returns>
    public static bool TryDiscoverDcpFromDirectory(
        string baseDirectory,
        out string? dcpCliPath,
        out string? dcpExtensionsPath,
        out string? dcpBinPath)
    {
        dcpCliPath = null;
        dcpExtensionsPath = null;
        dcpBinPath = null;

        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return false;
        }

        var dcpDir = Path.Combine(baseDirectory, DcpDirectoryName);
        var dcpExePath = GetDcpExecutablePath(dcpDir);

        if (File.Exists(dcpExePath))
        {
            dcpCliPath = dcpExePath;
            dcpExtensionsPath = Path.Combine(dcpDir, "ext");
            dcpBinPath = Path.Combine(dcpExtensionsPath, "bin");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to discover the aspire-managed binary from a base directory.
    /// </summary>
    /// <param name="baseDirectory">The base directory to search from.</param>
    /// <param name="managedPath">The full path to the aspire-managed executable if found.</param>
    /// <returns>True if aspire-managed was found, false otherwise.</returns>
    public static bool TryDiscoverManagedFromDirectory(
        string baseDirectory,
        out string? managedPath)
    {
        managedPath = null;

        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return false;
        }

        var managedDir = Path.Combine(baseDirectory, ManagedDirectoryName);
        var managedExe = Path.Combine(managedDir, GetExecutableFileName(ManagedExecutableName));

        if (File.Exists(managedExe))
        {
            managedPath = managedExe;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to discover DCP relative to the entry assembly.
    /// This is used by Aspire.Hosting when no environment variables are set.
    /// </summary>
    public static bool TryDiscoverDcpFromEntryAssembly(
        out string? dcpCliPath,
        out string? dcpExtensionsPath,
        out string? dcpBinPath)
    {
        dcpCliPath = null;
        dcpExtensionsPath = null;
        dcpBinPath = null;

        var baseDir = GetEntryAssemblyDirectory();
        if (baseDir is null)
        {
            return false;
        }

        return TryDiscoverDcpFromDirectory(baseDir, out dcpCliPath, out dcpExtensionsPath, out dcpBinPath);
    }

    /// <summary>
    /// Attempts to discover aspire-managed relative to the entry assembly.
    /// This is used by Aspire.Hosting when no environment variables are set.
    /// </summary>
    public static bool TryDiscoverManagedFromEntryAssembly(out string? managedPath)
    {
        managedPath = null;

        var baseDir = GetEntryAssemblyDirectory();
        if (baseDir is null)
        {
            return false;
        }

        return TryDiscoverManagedFromDirectory(baseDir, out managedPath);
    }

    /// <summary>
    /// Attempts to discover DCP relative to the current process.
    /// This is used by CLI to find DCP in the bundle layout.
    /// </summary>
    public static bool TryDiscoverDcpFromProcessPath(
        out string? dcpCliPath,
        out string? dcpExtensionsPath,
        out string? dcpBinPath)
    {
        dcpCliPath = null;
        dcpExtensionsPath = null;
        dcpBinPath = null;

        var baseDir = GetProcessDirectory();
        if (baseDir is null)
        {
            return false;
        }

        return TryDiscoverDcpFromDirectory(baseDir, out dcpCliPath, out dcpExtensionsPath, out dcpBinPath);
    }

    /// <summary>
    /// Attempts to discover aspire-managed relative to the current process.
    /// </summary>
    public static bool TryDiscoverManagedFromProcessPath(out string? managedPath)
    {
        managedPath = null;

        var baseDir = GetProcessDirectory();
        if (baseDir is null)
        {
            return false;
        }

        return TryDiscoverManagedFromDirectory(baseDir, out managedPath);
    }

    /// <summary>
    /// Attempts to discover the bundled watch tool DLL from a base directory.
    /// </summary>
    /// <param name="baseDirectory">The base directory to search from (e.g., bundle layout root).</param>
    /// <param name="watchToolPath">The full path to the watch tool entry-point DLL if found.</param>
    /// <returns>True if the watch tool was found, false otherwise.</returns>
    public static bool TryDiscoverWatchToolFromDirectory(
        string baseDirectory,
        out string? watchToolPath)
    {
        watchToolPath = null;

        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return false;
        }

        var watchPath = Path.Combine(baseDirectory, WatchDirectoryName, WatchToolDllName);

        if (File.Exists(watchPath))
        {
            watchToolPath = watchPath;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the path to <c>aspire-managed</c> inside an Aspire repo checkout when the
    /// normal repo build has produced it under <c>artifacts/bin/Aspire.Managed/{Configuration}/{tfm}/</c>.
    /// Used by callers that want to point dev-mode child processes at the repo's just-built
    /// terminal host instead of the user's installed CLI bundle (which may be stale).
    /// Returns <c>null</c> when <paramref name="repoRoot"/> is empty or the artifact is missing.
    /// </summary>
    /// <remarks>
    /// Hardcoded to Debug/net10.0 to keep behavior predictable — Release configurations are
    /// rarely used during inner-loop dev, and probing every TFM/config combination makes the
    /// outcome depend on stale build outputs from earlier sessions.
    /// </remarks>
    public static string? TryGetRepoLocalManagedPath(string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return null;
        }

        var managedPath = Path.Combine(
            repoRoot,
            "artifacts",
            "bin",
            "Aspire.Managed",
            "Debug",
            "net10.0",
            GetExecutableFileName(ManagedExecutableName));
        return File.Exists(managedPath) ? managedPath : null;
    }

    /// <summary>
    /// Returns the directory containing the watch tool DLL from the NuGet global packages cache.
    /// Returns <c>null</c> when the cache or package is missing.
    /// </summary>
    /// <param name="version">
    /// Exact package version to probe for. When <c>null</c>, the newest version
    /// present in the cache is chosen via a single-level directory listing.
    /// </param>
    public static string? TryGetWatchToolDirectoryFromNuGetCache(string? version)
    {
        var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        // Lowercased package id is the cache folder name.
        var packageRoot = Path.Combine(nugetPackages, WatchToolNugetCacheFolder);
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var versionRoot = !string.IsNullOrEmpty(version)
            ? Path.Combine(packageRoot, version)
            : GetNewestNuGetVersionDirectory(packageRoot);

        if (versionRoot is null || !Directory.Exists(versionRoot))
        {
            return null;
        }

        var toolsDir = Path.Combine(versionRoot, "tools", WatchToolDotNetVersion, "any");
        if (File.Exists(Path.Combine(toolsDir, WatchToolDllName)))
        {
            return toolsDir;
        }

        var fallback = Directory.EnumerateFiles(versionRoot, WatchToolDllName, SearchOption.AllDirectories)
            .FirstOrDefault();
        return fallback is not null ? Path.GetDirectoryName(fallback) : null;
    }

    /// <summary>
    /// Returns the path to the watch tool DLL from the NuGet global packages cache.
    /// Returns <c>null</c> when the cache or package is missing.
    /// </summary>
    /// <param name="version">
    /// Exact package version to probe for. When <c>null</c>, the newest version
    /// present in the cache is chosen via a single-level directory listing.
    /// </param>
    public static string? TryGetWatchToolPathFromNuGetCache(string? version)
    {
        var watchToolDirectory = TryGetWatchToolDirectoryFromNuGetCache(version);
        return watchToolDirectory is not null ? Path.Combine(watchToolDirectory, WatchToolDllName) : null;
    }

    private static string? GetNewestNuGetVersionDirectory(string packageRoot)
    {
        string? newestPath = null;
        var hasNewestVersion = false;
        var newestVersion = default(ParsedNuGetPackageVersion);

        foreach (var directory in Directory.GetDirectories(packageRoot))
        {
            var directoryName = Path.GetFileName(directory);
            if (!ParsedNuGetPackageVersion.TryParse(directoryName, out var candidateVersion))
            {
                continue;
            }

            if (!hasNewestVersion || candidateVersion.CompareTo(newestVersion) > 0)
            {
                newestPath = directory;
                newestVersion = candidateVersion;
                hasNewestVersion = true;
            }
        }

        return newestPath;
    }

    private readonly record struct ParsedNuGetPackageVersion(int Major, int Minor, int Patch, int Revision, string? Prerelease) : IComparable<ParsedNuGetPackageVersion>
    {
        public static bool TryParse(string? version, out ParsedNuGetPackageVersion parsed)
        {
            parsed = default;

            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            // NuGet global-package cache directories use normalized package versions, for example:
            //   10.0.301
            //   10.0.301-preview.1
            //   10.0.301-preview.1+sha.abc123
            // System.Version rejects prerelease labels, so parse enough SemVer/NuGet precedence
            // here to select the newest cached package without taking a NuGet dependency in every
            // project that source-links this shared file.
            var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
            var versionWithoutMetadata = metadataIndex >= 0 ? version[..metadataIndex] : version;

            var prereleaseIndex = versionWithoutMetadata.IndexOf('-', StringComparison.Ordinal);
            var releasePart = prereleaseIndex >= 0 ? versionWithoutMetadata[..prereleaseIndex] : versionWithoutMetadata;
            var prerelease = prereleaseIndex >= 0 ? versionWithoutMetadata[(prereleaseIndex + 1)..] : null;
            if (prerelease is "")
            {
                return false;
            }

            var releaseComponents = releasePart.Split('.');
            if (releaseComponents.Length is < 1 or > 4)
            {
                return false;
            }

            Span<int> components = stackalloc int[4];
            for (var i = 0; i < releaseComponents.Length; i++)
            {
                if (!int.TryParse(releaseComponents[i], NumberStyles.None, CultureInfo.InvariantCulture, out var component) ||
                    component < 0)
                {
                    return false;
                }

                components[i] = component;
            }

            parsed = new ParsedNuGetPackageVersion(components[0], components[1], components[2], components[3], prerelease);
            return true;
        }

        public int CompareTo(ParsedNuGetPackageVersion other)
        {
            var result = Major.CompareTo(other.Major);
            if (result != 0)
            {
                return result;
            }

            result = Minor.CompareTo(other.Minor);
            if (result != 0)
            {
                return result;
            }

            result = Patch.CompareTo(other.Patch);
            if (result != 0)
            {
                return result;
            }

            result = Revision.CompareTo(other.Revision);
            return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
        }

        private static int ComparePrerelease(string? left, string? right)
        {
            if (left is null)
            {
                return right is null ? 0 : 1;
            }

            if (right is null)
            {
                return -1;
            }

            var leftIdentifiers = left.Split('.');
            var rightIdentifiers = right.Split('.');
            var count = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);

            for (var i = 0; i < count; i++)
            {
                var result = ComparePrereleaseIdentifier(leftIdentifiers[i], rightIdentifiers[i]);
                if (result != 0)
                {
                    return result;
                }
            }

            return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftNumeric = TryNormalizeNumericIdentifier(left, out var normalizedLeft);
            var rightNumeric = TryNormalizeNumericIdentifier(right, out var normalizedRight);

            if (leftNumeric && rightNumeric && normalizedLeft is not null && normalizedRight is not null)
            {
                var lengthResult = normalizedLeft.Length.CompareTo(normalizedRight.Length);
                return lengthResult != 0 ? lengthResult : string.CompareOrdinal(normalizedLeft, normalizedRight);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNormalizeNumericIdentifier(string identifier, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized)
        {
            normalized = null;
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (var ch in identifier)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    return false;
                }
            }

            normalized = identifier.TrimStart('0');
            if (normalized.Length == 0)
            {
                normalized = "0";
            }

            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets the full path to the DCP executable given a DCP directory.
    /// </summary>
    public static string GetDcpExecutablePath(string dcpDirectory)
    {
        var exeName = GetDcpExecutableName();
        return Path.Combine(dcpDirectory, exeName);
    }

    /// <summary>
    /// Gets the platform-specific DCP executable name.
    /// </summary>
    public static string GetDcpExecutableName()
    {
        return OperatingSystem.IsWindows() ? "dcp.exe" : "dcp";
    }

    /// <summary>
    /// Gets the platform-specific executable name with extension.
    /// </summary>
    /// <param name="baseName">The base executable name without extension (e.g., "aspire-managed").</param>
    /// <returns>The executable name with platform-appropriate extension.</returns>
    public static string GetExecutableFileName(string baseName)
    {
        return OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
    }

    /// <summary>
    /// Gets the platform-specific DLL name.
    /// </summary>
    /// <param name="baseName">The base name without extension (e.g., "aspire-server").</param>
    /// <returns>The DLL name (e.g., "aspire-server.dll").</returns>
    public static string GetDllFileName(string baseName)
    {
        return $"{baseName}.dll";
    }

    /// <summary>
    /// Determines if the given file path points to an aspire-managed binary.
    /// </summary>
    public static bool IsAspireManagedBinary(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.Equals(fileName, ManagedExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the current platform's runtime identifier.
    /// </summary>
    public static string GetCurrentRuntimeIdentifier()
    {
        return RuntimeInformation.RuntimeIdentifier;
    }

    /// <summary>
    /// Gets the archive extension for the current platform.
    /// </summary>
    public static string GetArchiveExtension()
    {
        return OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
    }

    /// <summary>
    /// Gets the directory containing the entry assembly, if available.
    /// For native AOT or single-file apps, uses AppContext.BaseDirectory or ProcessPath fallback.
    /// </summary>
    private static string? GetEntryAssemblyDirectory()
    {
        // For native AOT and single-file apps, Assembly.Location returns empty
        // Use AppContext.BaseDirectory as the primary fallback
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
        {
            // Remove trailing separator if present
            return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // Final fallback: try process path
        return GetProcessDirectory();
    }

    /// <summary>
    /// Gets the directory containing the current process executable.
    /// </summary>
    private static string? GetProcessDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        return Path.GetDirectoryName(processPath);
    }
}
