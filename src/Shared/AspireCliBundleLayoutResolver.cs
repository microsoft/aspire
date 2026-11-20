// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is source-linked into Aspire.Hosting.Tasks (net472 and net10.0) and Aspire.Hosting.
// The build resolves bundle paths with it and embeds them in AppHost assembly metadata; Aspire.Hosting
// uses the same logic at runtime when that metadata is missing, so both must probe identical layouts.
// Keep it free of project-specific dependencies: callers supply PATH lookup results because each
// project has its own PATH helper (the tasks must build for net472).

using System.Text.RegularExpressions;

namespace Aspire.Shared;

/// <summary>
/// Read-only discovery of extracted Aspire CLI bundle layouts (DCP, dashboard, and terminal host).
/// </summary>
internal static class AspireCliBundleLayoutResolver
{
    public const string AspireHomeEnvironmentVariable = "ASPIRE_HOME";

    private const string BundleDirectoryName = "bundle";
    private const string VersionsDirectoryName = "versions";
    private const string InstallSidecarFileName = ".aspire-install.json";
    private const string PinnedLayoutsDirectoryName = "dnx";
    private const string PinnedCliPackageDirectoryName = "aspire.cli";
    private const int MaxInstallSidecarBytes = 64 * 1024;
    private static readonly Version s_unversionedDirectoryVersion = new(0, 0);

    /// <summary>
    /// Gets the Aspire home directory from a configured <c>ASPIRE_HOME</c> value, defaulting to <c>~/.aspire</c>.
    /// </summary>
    public static string GetAspireHomeDirectory(string? configuredAspireHome)
    {
        return string.IsNullOrWhiteSpace(configuredAspireHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire")
            : configuredAspireHome!;
    }

    /// <summary>
    /// Gets the directory that DNX-pinned bundle setup extracts the layout for <paramref name="version"/> into.
    /// </summary>
    public static string GetPinnedLayoutDirectory(string aspireHomeDirectory, string version)
    {
        // Package versions only contain alphanumerics, '.', '-' and '+'. Replace anything else so
        // the version cannot escape the pinned layouts directory.
        var directoryName = new string(version.Trim().Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '+' ? ch : '_').ToArray());
        if (directoryName.Trim('.').Length == 0)
        {
            directoryName = directoryName.Replace('.', '_');
        }

        return Path.Combine(aspireHomeDirectory, PinnedLayoutsDirectoryName, PinnedCliPackageDirectoryName, directoryName);
    }

    /// <summary>
    /// Resolves a layout for an AppHost whose build did not embed bundle paths.
    /// </summary>
    /// <remarks>
    /// Mirrors the build's default (non-explicit) discovery: the layout pinned to <paramref name="hostingVersion"/>
    /// is preferred because it is paired with this exact Aspire.Hosting version, then the layouts of
    /// <paramref name="pathCliPaths"/> (the <c>aspire</c> executables found on PATH, in lookup order), then the
    /// shared Aspire home layout.
    /// </remarks>
    public static bool TryResolveRuntimeFallback(
        string aspireHomeDirectory,
        string? hostingVersion,
        IEnumerable<string> pathCliPaths,
        out AspireCliBundleLayout layout)
    {
        if (!string.IsNullOrWhiteSpace(hostingVersion) &&
            TryResolveFromLayoutPath(GetPinnedLayoutDirectory(aspireHomeDirectory, hostingVersion!), out layout))
        {
            return true;
        }

        if (TryResolveFromCliPaths(pathCliPaths, aspireHomeDirectory, out layout))
        {
            return true;
        }

        return TryResolveFromLayoutPath(aspireHomeDirectory, out layout);
    }

    /// <summary>
    /// Resolves the layout for the first of <paramref name="cliPaths"/> that has one.
    /// </summary>
    public static bool TryResolveFromCliPaths(IEnumerable<string> cliPaths, string aspireHomeDirectory, out AspireCliBundleLayout layout)
    {
        foreach (var cliPath in cliPaths)
        {
            if (TryResolveFromCliPath(cliPath, aspireHomeDirectory, out layout))
            {
                return true;
            }
        }

        layout = default!;
        return false;
    }

    /// <summary>
    /// Resolves the layout that belongs to the Aspire CLI at <paramref name="cliPath"/>, falling back to
    /// the Aspire home layout for install routes that extract there.
    /// </summary>
    public static bool TryResolveFromCliPath(string cliPath, string aspireHomeDirectory, out AspireCliBundleLayout layout)
    {
        return TryResolveFromCliPath(cliPath, aspireHomeDirectory, includeAspireHomeFallback: true, out layout);
    }

    /// <summary>
    /// Resolves a layout rooted at <paramref name="layoutPath"/>, its <c>bundle</c> link, or its newest <c>versions</c> entry.
    /// </summary>
    public static bool TryResolveFromLayoutPath(string layoutPath, out AspireCliBundleLayout layout)
    {
        var fullPath = Path.GetFullPath(layoutPath);
        if (TryResolveBundleRoot(fullPath, out layout))
        {
            return true;
        }

        if (TryResolveBundleRoot(Path.Combine(fullPath, BundleDirectoryName), out layout))
        {
            return true;
        }

        return TryResolveVersionedBundleRoot(fullPath, out layout);
    }

    private static bool TryResolveFromCliPath(string cliPath, string aspireHomeDirectory, bool includeAspireHomeFallback, out AspireCliBundleLayout layout)
    {
        layout = default!;

        var candidateCliPaths = EnumerateCandidateCliPaths(Path.GetFullPath(cliPath));
        foreach (var candidateCliPath in candidateCliPaths)
        {
            var cliDirectory = Path.GetDirectoryName(candidateCliPath);
            if (string.IsNullOrEmpty(cliDirectory))
            {
                continue;
            }

            foreach (var layoutPath in EnumerateLayoutPathsForCliDirectory(cliDirectory))
            {
                if (TryResolveFromLayoutPath(layoutPath, out layout))
                {
                    return true;
                }
            }

            foreach (var dotnetToolCliPath in EnumerateDotNetToolStoreCliPaths(cliDirectory))
            {
                if (TryResolveFromCliPath(dotnetToolCliPath, aspireHomeDirectory, includeAspireHomeFallback: false, out layout))
                {
                    return true;
                }
            }
        }

        if (includeAspireHomeFallback && TryResolveFromLayoutPath(aspireHomeDirectory, out layout))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveBundleRoot(string bundleRoot, out AspireCliBundleLayout layout)
    {
        layout = default!;

        var dcpDir = Path.Combine(bundleRoot, "dcp");
        var dcpPath = Path.Combine(dcpDir, IsWindows() ? "dcp.exe" : "dcp");
        var managedDir = Path.Combine(bundleRoot, "managed");
        var managedPath = Path.Combine(managedDir, IsWindows() ? "aspire-managed.exe" : "aspire-managed");

        // Current bundles isolate the Native AOT Dashboard and its static assets in dashboard/.
        // Preserve compatibility with transitional bundles that placed Aspire.Dashboard in managed/;
        // if there is no dashboard/ component or transitional executable, fall back to the older aspire-managed
        // dispatcher, which launches the Dashboard through its "dashboard" subcommand.
        var dashboardDir = Path.Combine(bundleRoot, "dashboard");
        var dashboardPath = Path.Combine(dashboardDir, IsWindows() ? "Aspire.Dashboard.exe" : "Aspire.Dashboard");
        if (Directory.Exists(dashboardDir) && !File.Exists(dashboardPath))
        {
            // An incomplete current bundle must not fall back to aspire-managed, which no longer
            // supports the dashboard subcommand. Only older layouts omit dashboard/ entirely.
            return false;
        }

        if (!File.Exists(dashboardPath))
        {
            dashboardDir = managedDir;
            dashboardPath = Path.Combine(managedDir, IsWindows() ? "Aspire.Dashboard.exe" : "Aspire.Dashboard");
        }

        if (!File.Exists(dcpPath) || !File.Exists(managedPath))
        {
            return false;
        }

        var terminalHostDir = Path.Combine(bundleRoot, "terminalhost");
        var terminalHostPath = Path.Combine(terminalHostDir, IsWindows() ? "Aspire.TerminalHost.exe" : "Aspire.TerminalHost");
        if (Directory.Exists(terminalHostDir) && !File.Exists(terminalHostPath))
        {
            return false;
        }

        // Older CLI bundles omit terminalhost/ entirely. Try their legacy dispatcher as a
        // best-effort fallback; an existing but incomplete component was rejected above.
        var hasStandaloneTerminalHost = File.Exists(terminalHostPath);
        layout = new AspireCliBundleLayout(
            dcpDir,
            managedDir,
            managedPath,
            dashboardDir,
            File.Exists(dashboardPath) ? dashboardPath : managedPath,
            hasStandaloneTerminalHost ? terminalHostDir : managedDir,
            hasStandaloneTerminalHost ? terminalHostPath : managedPath,
            hasStandaloneTerminalHost ? string.Empty : "terminalhost");
        return true;
    }

    private static bool IsWindows() => Path.DirectorySeparatorChar == '\\';

    private static IEnumerable<string> EnumerateCandidateCliPaths(string cliPath)
    {
        var seenPaths = new HashSet<string>(GetPathComparer());

        if (seenPaths.Add(cliPath))
        {
            yield return cliPath;
        }

        var resolvedCliPath = ResolveSymlinkOrOriginalPath(cliPath);
        if (seenPaths.Add(resolvedCliPath))
        {
            yield return resolvedCliPath;
        }
    }

    private static IEnumerable<string> EnumerateLayoutPathsForCliDirectory(string cliDirectory)
    {
        var seenPaths = new HashSet<string>(GetPathComparer());

        foreach (var layoutPath in EnumerateLayoutPathsForCliDirectoryCore(cliDirectory))
        {
            if (!string.IsNullOrEmpty(layoutPath) && seenPaths.Add(layoutPath!))
            {
                yield return layoutPath!;
            }
        }
    }

    private static IEnumerable<string?> EnumerateLayoutPathsForCliDirectoryCore(string cliDirectory)
    {
        yield return cliDirectory;

        // Install-route sidecars are written as:
        //   { "source": "winget" }
        // Known package-manager routes extract beside the binary and script/localhive
        // routes extract under the parent prefix. Sidecar-less/unknown routes fall
        // back to ASPIRE_HOME after any dotnet-tool store payload has been probed.
        yield return TryReadInstallSource(cliDirectory) switch
        {
            "winget" or "brew" or "dotnet-tool" => cliDirectory,
            "script" or "pr" or "localhive" => Path.GetDirectoryName(cliDirectory) ?? cliDirectory,
            _ => null,
        };

        yield return Path.GetDirectoryName(cliDirectory);
    }

    private static IEnumerable<string> EnumerateDotNetToolStoreCliPaths(string cliDirectory)
    {
        var storeRoot = Path.Combine(cliDirectory, ".store", "aspire.cli");
        if (!Directory.Exists(storeRoot))
        {
            yield break;
        }

        foreach (var executableName in GetNativeAspireExecutableNames())
        {
            foreach (var candidate in EnumerateFiles(storeRoot, executableName))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.GetFiles(path, searchPattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (IsPathDiscoveryException(ex))
        {
            return [];
        }
    }

    private static string[] GetNativeAspireExecutableNames()
    {
        return IsWindows()
            ? ["aspire.exe"]
            : ["aspire"];
    }

    private static bool TryResolveVersionedBundleRoot(string layoutPath, out AspireCliBundleLayout layout)
    {
        var versionsRoot = Path.Combine(layoutPath, VersionsDirectoryName);
        if (Directory.Exists(versionsRoot))
        {
            foreach (var versionDirectory in EnumerateDirectories(versionsRoot)
                         .Where(IsVersionDirectoryCandidate)
                         .OrderByDescending(GetVersionDirectoryVersion)
                         .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (TryResolveBundleRoot(versionDirectory, out layout))
                {
                    return true;
                }
            }
        }

        layout = default!;
        return false;
    }

    private static Version GetVersionDirectoryVersion(string path)
    {
        var directoryName = Path.GetFileName(path);
        return Version.TryParse(GetLeadingVersion(StripVersionIdHash(directoryName)), out var version)
            ? version
            : s_unversionedDirectoryVersion;
    }

    private static string GetLeadingVersion(string directoryName)
    {
        // BundleService.ComputeVersionId keeps the informational-version prefix and appends a hash:
        //   13.10.0-preview.1.25301.1_gdae6efcb-bbbbbbbbbbbbbbbb
        // System.Version cannot parse the prerelease/hash suffix, but it can compare the leading
        // numeric version that determines which extracted bundle is newer.
        var versionEnd = 0;
        while (versionEnd < directoryName.Length && directoryName[versionEnd] is (>= '0' and <= '9') or '.')
        {
            versionEnd++;
        }

        return versionEnd == 0
            ? string.Empty
            : directoryName.Substring(0, versionEnd).TrimEnd('.');
    }

    private static string StripVersionIdHash(string directoryName)
    {
        // BundleService version directories are named "<sanitized-version>-<16-hex-xxhash>",
        // for example "9.10.0-bbbbbbbbbbbbbbbb". Strip the hash so System.Version
        // compares the semantic version instead of the full directory name.
        var separatorIndex = directoryName.LastIndexOf('-');
        if (separatorIndex < 0 ||
            directoryName.Length - separatorIndex - 1 != 16 ||
            !ContainsOnlyHexDigits(directoryName, separatorIndex + 1))
        {
            return directoryName;
        }

        return directoryName.Substring(0, separatorIndex);
    }

    private static bool ContainsOnlyHexDigits(string value, int startIndex)
    {
        for (var i = startIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is not ((>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVersionDirectoryCandidate(string path)
    {
        var directoryName = Path.GetFileName(path);
        return !directoryName.Contains(".tmp.", StringComparison.Ordinal)
            && !directoryName.Contains(".bad.", StringComparison.Ordinal)
            && !directoryName.Contains(".old.", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (IsPathDiscoveryException(ex))
        {
            return [];
        }
    }

    private static string? TryReadInstallSource(string cliDirectory)
    {
        var sidecarPath = Path.Combine(cliDirectory, InstallSidecarFileName);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(sidecarPath).Length > MaxInstallSidecarBytes)
            {
                return null;
            }

            var sidecar = File.ReadAllText(sidecarPath);
            var sourceMatch = Regex.Match(sidecar, @"""source""\s*:\s*""(?<source>[^""]*)""");
            return sourceMatch.Success ? sourceMatch.Groups["source"].Value : null;
        }
        catch (Exception ex) when (IsPathDiscoveryException(ex))
        {
            return null;
        }
    }

    private static StringComparer GetPathComparer()
    {
        return IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private static string ResolveSymlinkOrOriginalPath(string path)
    {
#if NET8_0_OR_GREATER
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (IsPathDiscoveryException(ex))
        {
            return path;
        }
#else
        return path;
#endif
    }

    private static bool IsPathDiscoveryException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException;
    }
}

/// <summary>
/// Component paths within a resolved Aspire CLI bundle layout.
/// </summary>
internal sealed class AspireCliBundleLayout(string dcpDir, string managedDir, string managedPath, string dashboardDir, string dashboardPath,
    string terminalHostDir, string terminalHostPath, string terminalHostInvocationArgs)
{
    public string DcpDir { get; } = dcpDir;

    public string ManagedDir { get; } = managedDir;

    public string ManagedPath { get; } = managedPath;

    public string DashboardDir { get; } = dashboardDir;

    public string DashboardPath { get; } = dashboardPath;

    public string TerminalHostDir { get; } = terminalHostDir;

    public string TerminalHostPath { get; } = terminalHostPath;

    /// <summary>
    /// Arguments to prepend when launching <see cref="TerminalHostPath"/>: <c>"terminalhost"</c> for the legacy
    /// multi-mode aspire-managed dispatcher, otherwise empty.
    /// </summary>
    public string TerminalHostInvocationArgs { get; } = terminalHostInvocationArgs;
}
