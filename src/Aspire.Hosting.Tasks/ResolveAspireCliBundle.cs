// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Resolves DCP and Dashboard paths from an Aspire CLI bundle layout.
/// </summary>
public sealed class ResolveAspireCliBundle : Microsoft.Build.Utilities.Task
{
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

    public override bool Execute()
    {
        if (!string.IsNullOrWhiteSpace(AspireCliBundlePath))
        {
            if (TryResolveFromLayoutPath(AspireCliBundlePath, out var explicitBundle))
            {
                SetOutputs(explicitBundle);
                return true;
            }

            LogBundleNotFound($"The Aspire CLI bundle path '{AspireCliBundlePath}' does not contain a valid bundle layout.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(AspireCliPath))
        {
            if (File.Exists(AspireCliPath) && TryResolveFromCliPath(AspireCliPath, out var explicitCliBundle))
            {
                SetOutputs(explicitCliBundle);
                return true;
            }

            LogBundleNotFound($"The Aspire CLI path '{AspireCliPath}' does not resolve to a valid bundle layout.");
            return false;
        }

        if (TryResolveFromPath(out var pathBundle))
        {
            SetOutputs(pathBundle);
            return true;
        }

        LogBundleNotFound("Unable to locate an Aspire CLI bundle. Ensure 'aspire' is installed on PATH, set 'AspireCliBundlePath' to a valid bundle layout, or set 'DcpDir' and 'AspireDashboardDir' explicitly.");
        return false;
    }

    private void SetOutputs(BundleResolution resolution)
    {
        DcpDir = EnsureTrailingDirectorySeparator(resolution.DcpDir);
        AspireDashboardDir = EnsureTrailingDirectorySeparator(resolution.ManagedDir);
        AspireDashboardPath = resolution.ManagedPath;
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

    private static bool TryResolveFromCliPath(string cliPath, out BundleResolution resolution)
    {
        resolution = default!;

        var cliDirectory = Path.GetDirectoryName(Path.GetFullPath(cliPath));
        if (string.IsNullOrEmpty(cliDirectory))
        {
            return false;
        }

        if (TryResolveFromLayoutPath(cliDirectory, out resolution))
        {
            return true;
        }

        var parentDirectory = Path.GetDirectoryName(cliDirectory);
        return !string.IsNullOrEmpty(parentDirectory) && TryResolveFromLayoutPath(parentDirectory, out resolution);
    }

    private static bool TryResolveFromLayoutPath(string layoutPath, out BundleResolution resolution)
    {
        var fullPath = Path.GetFullPath(layoutPath);
        if (TryResolveBundleRoot(fullPath, out resolution))
        {
            return true;
        }

        return TryResolveBundleRoot(Path.Combine(fullPath, "bundle"), out resolution);
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

    private void LogBundleNotFound(string message)
    {
        Log.LogError(
            subcategory: null,
            errorCode: "ASPIRE009",
            helpKeyword: null,
            file: null,
            lineNumber: 0,
            columnNumber: 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: message);
    }

    private sealed class BundleResolution(string dcpDir, string managedDir, string managedPath)
    {
        public string DcpDir { get; } = dcpDir;

        public string ManagedDir { get; } = managedDir;

        public string ManagedPath { get; } = managedPath;
    }
}
