// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Probes Windows-specific facts that identify whether the currently running
/// CLI binary was placed by a winget portable install. Used by
/// <see cref="WingetFirstRunProbe"/>; abstracted for testability.
/// </summary>
internal interface IWindowsRegistryReader
{
    /// <summary>
    /// Returns <see langword="true"/> when an entry under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c> (or the
    /// 64-bit HKLM hive) carries
    /// <c>WinGetPackageIdentifier == "Microsoft.Aspire"</c> and a
    /// <c>PortableTargetFullPath</c> that matches the supplied
    /// <paramref name="processPath"/> (canonicalized and compared
    /// case-insensitively).
    /// </summary>
    bool HasWingetAspireUninstallEntry(string processPath);
}

/// <summary>
/// Production <see cref="IWindowsRegistryReader"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryReader : IWindowsRegistryReader
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PackageIdentifierValueName = "WinGetPackageIdentifier";
    private const string AspirePackageIdentifier = "Microsoft.Aspire";
    private const string PortableTargetValueName = "PortableTargetFullPath";

    public bool HasWingetAspireUninstallEntry(string processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        string canonicalProcessPath;
        try
        {
            canonicalProcessPath = Path.GetFullPath(processPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        // Winget portable packages register under HKCU by default for per-user installs
        // and under HKLM for machine-wide installs. Probe both so an admin install still
        // self-stamps.
        return MatchesAspireEntry(RegistryHive.CurrentUser, canonicalProcessPath)
            || MatchesAspireEntry(RegistryHive.LocalMachine, canonicalProcessPath);
    }

    private static bool MatchesAspireEntry(RegistryHive hive, string canonicalProcessPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var uninstall = baseKey.OpenSubKey(UninstallSubKey, writable: false);
            if (uninstall is null)
            {
                return false;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName, writable: false);
                if (entry is null)
                {
                    continue;
                }

                if (entry.GetValue(PackageIdentifierValueName) is not string identifier
                    || !string.Equals(identifier, AspirePackageIdentifier, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.GetValue(PortableTargetValueName) is not string portableTarget
                    || string.IsNullOrEmpty(portableTarget))
                {
                    continue;
                }

                string canonicalTarget;
                try
                {
                    canonicalTarget = Path.GetFullPath(portableTarget);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    continue;
                }

                if (string.Equals(canonicalTarget, canonicalProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry probe is best-effort. A locked hive, a denied ACL, or
            // a transient IO failure must not crash startup — the caller will
            // simply treat the install as non-winget for this run.
        }

        return false;
    }
}

/// <summary>
/// Non-Windows <see cref="IWindowsRegistryReader"/>. Always returns <see langword="false"/>.
/// </summary>
internal sealed class NullWindowsRegistryReader : IWindowsRegistryReader
{
    public bool HasWingetAspireUninstallEntry(string processPath) => false;
}
