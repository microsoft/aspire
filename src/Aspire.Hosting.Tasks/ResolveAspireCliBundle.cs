// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Resolves DCP and Dashboard paths from an Aspire CLI bundle layout.
/// </summary>
public sealed class ResolveAspireCliBundle : Microsoft.Build.Utilities.Task
{
    private const string BundleDirectoryName = "bundle";
    private const string VersionsDirectoryName = "versions";
    private const string InstallSidecarFileName = ".aspire-install.json";
    private const string AspireHomeEnvironmentVariable = "ASPIRE_HOME";
    private const int MaxInstallSidecarBytes = 64 * 1024;
    private static readonly Version s_unversionedDirectoryVersion = new(0, 0);

    /// <summary>
    /// Optional path to an Aspire CLI bundle or layout directory.
    /// </summary>
    public string? AspireCliBundlePath { get; set; }

    /// <summary>
    /// Optional path to an Aspire CLI executable.
    /// </summary>
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// The resolved DCP directory.
    /// </summary>
    [Output]
    public string? DcpDir { get; set; }

    /// <summary>
    /// The resolved Dashboard directory.
    /// </summary>
    [Output]
    public string? AspireDashboardDir { get; set; }

    /// <summary>
    /// The resolved Dashboard executable path.
    /// </summary>
    [Output]
    public string? AspireDashboardPath { get; set; }

    /// <summary>
    /// The resolved TerminalHost directory.
    /// </summary>
    [Output]
    public string? AspireTerminalHostDir { get; set; }

    /// <summary>
    /// The resolved TerminalHost executable path.
    /// </summary>
    [Output]
    public string? AspireTerminalHostPath { get; set; }

    /// <summary>
    /// Invocation args that must be prepended when launching the TerminalHost binary.
    /// In the CLI bundle case the binary is the multi-mode <c>aspire-managed</c> exe and
    /// this is set to <c>"terminalhost"</c> so the dispatcher routes to <c>TerminalHostApp.RunAsync</c>.
    /// Empty for the per-RID NuGet package case (the binary is a standalone TerminalHost exe).
    /// </summary>
    [Output]
    public string? AspireTerminalHostInvocationArgs { get; set; }

    public override bool Execute()
    {
        if (!string.IsNullOrWhiteSpace(AspireCliBundlePath))
        {
            if (TryResolveFromLayoutPath(AspireCliBundlePath, out var explicitBundle))
            {
                SetOutputs(explicitBundle);
                return true;
            }

            Log.LogWarning("The AspireCliBundlePath value '{0}' does not point to a valid Aspire CLI bundle layout.", AspireCliBundlePath);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(AspireCliPath))
        {
            if (File.Exists(AspireCliPath) && TryResolveFromCliPath(AspireCliPath, out var explicitCliBundle))
            {
                SetOutputs(explicitCliBundle);
                return true;
            }

            Log.LogWarning("The AspireCliPath value '{0}' does not point to an existing Aspire CLI executable with a valid bundle layout.", AspireCliPath);
            return true;
        }

        if (TryResolveFromPath(out var pathBundle))
        {
            SetOutputs(pathBundle);
            return true;
        }

        if (TryResolveFromLayoutPath(GetDefaultAspireHomeDirectory(), out var aspireHomeBundle))
        {
            SetOutputs(aspireHomeBundle);
            return true;
        }

        return true;
    }

    private void SetOutputs(BundleResolution resolution)
    {
        DcpDir = EnsureTrailingDirectorySeparator(resolution.DcpDir);
        AspireDashboardDir = EnsureTrailingDirectorySeparator(resolution.ManagedDir);
        AspireDashboardPath = resolution.ManagedPath;
        AspireTerminalHostDir = EnsureTrailingDirectorySeparator(resolution.ManagedDir);
        AspireTerminalHostPath = resolution.ManagedPath;
        AspireTerminalHostInvocationArgs = "terminalhost";
    }

    private static bool TryResolveFromPath(out BundleResolution resolution)
    {
        resolution = default!;

        foreach (var cliPath in EnumerateAspireCliPaths())
        {
            if (TryResolveFromCliPath(cliPath, out resolution))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateAspireCliPaths()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var executableNames = GetAspireExecutableNames();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pathEntry in path.Split(Path.PathSeparator))
        {
            var directory = pathEntry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory, executableName);
                if (seenPaths.Add(candidate) && File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string[] GetAspireExecutableNames()
    {
        return IsWindows()
            ? ["aspire.exe", "aspire.cmd", "aspire.bat", "aspire"]
            : ["aspire"];
    }

    private static bool TryResolveFromCliPath(string cliPath, out BundleResolution resolution, bool includeDefaultAspireHomeFallback = true)
    {
        resolution = default!;

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
                if (TryResolveFromLayoutPath(layoutPath, out resolution))
                {
                    return true;
                }
            }

            foreach (var dotnetToolCliPath in EnumerateDotNetToolStoreCliPaths(cliDirectory))
            {
                if (TryResolveFromCliPath(dotnetToolCliPath, out resolution, includeDefaultAspireHomeFallback: false))
                {
                    return true;
                }
            }
        }

        if (includeDefaultAspireHomeFallback && TryResolveFromLayoutPath(GetDefaultAspireHomeDirectory(), out resolution))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveFromLayoutPath(string layoutPath, out BundleResolution resolution)
    {
        var fullPath = Path.GetFullPath(layoutPath);
        if (TryResolveBundleRoot(fullPath, out resolution))
        {
            return true;
        }

        if (TryResolveBundleRoot(Path.Combine(fullPath, BundleDirectoryName), out resolution))
        {
            return true;
        }

        return TryResolveVersionedBundleRoot(fullPath, out resolution);
    }

    private static bool TryResolveBundleRoot(string bundleRoot, out BundleResolution resolution)
    {
        resolution = default!;

        var dcpDir = Path.Combine(bundleRoot, "dcp");
        var dcpPath = Path.Combine(dcpDir, IsWindows() ? "dcp.exe" : "dcp");
        var managedDir = Path.Combine(bundleRoot, "managed");
        var managedPath = Path.Combine(managedDir, IsWindows() ? "aspire-managed.exe" : "aspire-managed");

        if (!File.Exists(dcpPath) || !File.Exists(managedPath))
        {
            return false;
        }

        resolution = new BundleResolution(dcpDir, managedDir, managedPath);
        return true;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
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
            if (!string.IsNullOrEmpty(layoutPath) && seenPaths.Add(layoutPath))
            {
                yield return layoutPath;
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

    private static bool TryResolveVersionedBundleRoot(string layoutPath, out BundleResolution resolution)
    {
        var versionsRoot = Path.Combine(layoutPath, VersionsDirectoryName);
        if (Directory.Exists(versionsRoot))
        {
            foreach (var versionDirectory in EnumerateDirectories(versionsRoot)
                         .Where(IsVersionDirectoryCandidate)
                         .OrderByDescending(GetVersionDirectoryVersion)
                         .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (TryResolveBundleRoot(versionDirectory, out resolution))
                {
                    return true;
                }
            }
        }

        resolution = default!;
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

    private static string GetDefaultAspireHomeDirectory()
    {
        var aspireHome = Environment.GetEnvironmentVariable(AspireHomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(aspireHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire")
            : aspireHome;
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

    private sealed class BundleResolution(string dcpDir, string managedDir, string managedPath)
    {
        public string DcpDir { get; } = dcpDir;

        public string ManagedDir { get; } = managedDir;

        public string ManagedPath { get; } = managedPath;
    }
}
