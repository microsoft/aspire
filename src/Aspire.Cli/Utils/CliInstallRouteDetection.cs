// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Aspire.Cli.Utils;

/// <summary>
/// Resolves the install route for CLI package-manager installations.
/// </summary>
internal static class CliInstallRouteDetection
{
    private const string SidecarFileName = ".aspire-install.json";
    private const string WinGetRoute = "winget";
    private const string WinGetPackageIdentifier = "Microsoft.Aspire";
    private const string WinGetSourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    private const string WinGetProductCode = $"{WinGetPackageIdentifier}_{WinGetSourceIdentifier}";
    private const string WinGetUpdateCommand = "winget upgrade Microsoft.Aspire";
    private const string WinGetInstallerType = "portable";
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly AsyncLocal<string?> s_processPathOverride = new();

    internal static string? GetUpdateCommand(Action<Exception>? onSidecarWriteFailure = null)
    {
        return GetUpdateCommand(s_processPathOverride.Value ?? Environment.ProcessPath, cacheDetectedRoute: true, onSidecarWriteFailure);
    }

    internal static string? GetUpdateCommand(string? processPath, bool cacheDetectedRoute, Action<Exception>? onSidecarWriteFailure = null)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        if (TryReadSidecar(processPath, out var sidecarRoute))
        {
            return string.IsNullOrWhiteSpace(sidecarRoute.UpdateCommand) ? null : sidecarRoute.UpdateCommand;
        }

        if (OperatingSystem.IsWindows() && TryDetectWinGetInstall(processPath, GetWinGetPortableInstallEntries(), out var detectedRoute))
        {
            if (cacheDetectedRoute)
            {
                TryWriteSidecar(processPath, detectedRoute, onSidecarWriteFailure);
            }

            return detectedRoute.UpdateCommand;
        }

        return null;
    }

    internal static bool TryDetectWinGetInstall(string? processPath, IEnumerable<WinGetPortableInstallEntry> entries, out CliInstallRoute route)
    {
        route = default;

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.PackageIdentifier, WinGetPackageIdentifier, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.SourceIdentifier, WinGetSourceIdentifier, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.InstallerType, WinGetInstallerType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasExecutablePath = !string.IsNullOrWhiteSpace(entry.TargetFullPath) ||
                !string.IsNullOrWhiteSpace(entry.SymlinkFullPath);
            var executablePathMatches = PathMatches(entry.TargetFullPath, processPath) ||
                PathMatches(entry.SymlinkFullPath, processPath);

            if (hasExecutablePath ? !executablePathMatches : !InstallLocationMatches(entry.InstallLocation, processPath))
            {
                continue;
            }

            var sidecarDirectory = GetWindowsDirectoryName(entry.TargetFullPath) ?? entry.InstallLocation;
            route = new CliInstallRoute(WinGetRoute, WinGetUpdateCommand, sidecarDirectory);
            return true;
        }

        return false;
    }

    internal static bool TryWriteSidecar(string processPath, CliInstallRoute route, Action<Exception>? onFailure = null)
    {
        var sidecarDirectory = !string.IsNullOrWhiteSpace(route.SidecarDirectory)
            ? route.SidecarDirectory
            : Path.GetDirectoryName(processPath);

        if (string.IsNullOrWhiteSpace(sidecarDirectory))
        {
            return false;
        }

        var sidecarPath = Path.Combine(sidecarDirectory, SidecarFileName);
        if (File.Exists(sidecarPath))
        {
            return false;
        }

        var tempPath = Path.Combine(sidecarDirectory, $"{SidecarFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("route", route.Route);
                writer.WriteString("updateCommand", route.UpdateCommand);
                writer.WriteEndObject();
            }

            var content = System.Text.Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, sidecarPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
        {
            TryDeleteFile(tempPath);
            onFailure?.Invoke(ex);
            return false;
        }
    }

    internal static IDisposable UseProcessPathForTesting(string? processPath)
    {
        var previousValue = s_processPathOverride.Value;
        s_processPathOverride.Value = processPath;
        return new ProcessPathOverrideScope(previousValue);
    }

    private static bool TryReadSidecar(string processPath, out CliInstallRoute route)
    {
        route = default;

        foreach (var sidecarPath in GetSidecarCandidatePaths(processPath))
        {
            if (!File.Exists(sidecarPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                var root = document.RootElement;
                if (!root.TryGetProperty("route", out var routeProperty))
                {
                    continue;
                }

                var routeName = routeProperty.GetString();
                if (string.IsNullOrWhiteSpace(routeName))
                {
                    continue;
                }

                var updateCommand = root.TryGetProperty("updateCommand", out var updateCommandProperty)
                    ? updateCommandProperty.GetString()
                    : null;

                route = new CliInstallRoute(routeName, updateCommand, Path.GetDirectoryName(sidecarPath));
                return true;
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static IEnumerable<string> GetSidecarCandidatePaths(string processPath)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidateDirectories = new HashSet<string>(comparer);

        AddSidecarCandidateDirectories(processPath, candidateDirectories);

        foreach (var resolvedProcessPath in GetResolvedProcessPaths(processPath))
        {
            AddSidecarCandidateDirectories(resolvedProcessPath, candidateDirectories);
        }

        foreach (var candidateDirectory in candidateDirectories)
        {
            yield return Path.Combine(candidateDirectory, SidecarFileName);
        }
    }

    private static void AddSidecarCandidateDirectories(string processPath, HashSet<string> candidateDirectories)
    {
        var processDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(processDirectory))
        {
            return;
        }

        candidateDirectories.Add(processDirectory);

        var parentDirectory = Path.GetDirectoryName(processDirectory);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            candidateDirectories.Add(parentDirectory);
        }
    }

    private static IEnumerable<string> GetResolvedProcessPaths(string processPath)
    {
        FileSystemInfo? linkTarget;
        try
        {
            linkTarget = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        if (linkTarget is null)
        {
            yield break;
        }

        var targetPath = linkTarget.FullName;
        if (!Path.IsPathRooted(targetPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (string.IsNullOrWhiteSpace(processDirectory))
            {
                yield break;
            }

            targetPath = Path.GetFullPath(Path.Combine(processDirectory, targetPath));
        }

        yield return targetPath;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<WinGetPortableInstallEntry> GetWinGetPortableInstallEntries()
    {
        var entries = new List<WinGetPortableInstallEntry>();
        AddWinGetPortableInstallEntry(entries, RegistryHive.CurrentUser, RegistryView.Registry64);
        AddWinGetPortableInstallEntry(entries, RegistryHive.LocalMachine, RegistryView.Registry64);
        AddWinGetPortableInstallEntry(entries, RegistryHive.LocalMachine, RegistryView.Registry32);
        return entries;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWinGetPortableInstallEntry(List<WinGetPortableInstallEntry> entries, RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey);
        using var packageKey = uninstallKey?.OpenSubKey(WinGetProductCode);
        if (packageKey is null)
        {
            return;
        }

        entries.Add(new WinGetPortableInstallEntry(
            GetStringValue(packageKey, "WinGetPackageIdentifier"),
            GetStringValue(packageKey, "WinGetSourceIdentifier"),
            GetStringValue(packageKey, "WinGetInstallerType"),
            GetStringValue(packageKey, "InstallLocation"),
            GetStringValue(packageKey, "TargetFullPath"),
            GetStringValue(packageKey, "SymlinkFullPath")));
    }

    [SupportedOSPlatform("windows")]
    private static string? GetStringValue(RegistryKey key, string name)
    {
        return key.GetValue(name) as string;
    }

    private static bool InstallLocationMatches(string? installLocation, string processPath)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        return PathMatches(CombineWindowsPath(installLocation, "aspire.exe"), processPath) ||
            PathMatches(CombineWindowsPath(installLocation, "aspire"), processPath);
    }

    private static string CombineWindowsPath(string directory, string fileName)
    {
        return $"{directory.TrimEnd('\\', '/')}\\{fileName}";
    }

    private static bool PathMatches(string? candidatePath, string processPath)
    {
        return !string.IsNullOrWhiteSpace(candidatePath) &&
            string.Equals(NormalizeWindowsPath(candidatePath), NormalizeWindowsPath(processPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWindowsPath(string path)
    {
        return path.Trim().Replace('/', '\\').TrimEnd('\\');
    }

    private static string? GetWindowsDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizeWindowsPath(path);
        var separatorIndex = normalized.LastIndexOf('\\');
        return separatorIndex <= 0 ? null : normalized[..separatorIndex];
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal readonly record struct CliInstallRoute(string Route, string? UpdateCommand, string? SidecarDirectory = null);

    internal readonly record struct WinGetPortableInstallEntry(
        string? PackageIdentifier,
        string? SourceIdentifier,
        string? InstallerType,
        string? InstallLocation,
        string? TargetFullPath,
        string? SymlinkFullPath);

    private sealed class ProcessPathOverrideScope(string? previousValue) : IDisposable
    {
        public void Dispose()
        {
            s_processPathOverride.Value = previousValue;
        }
    }
}
