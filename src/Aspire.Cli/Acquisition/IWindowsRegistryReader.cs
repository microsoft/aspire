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
    /// 64-bit HKLM hive) carries <c>WinGetPackageIdentifier == "Microsoft.Aspire"</c>,
    /// <c>WinGetInstallerType == "portable"</c>, and either a <c>TargetFullPath</c>
    /// that resolves to <paramref name="processPath"/> (single-file portable) or
    /// an <c>InstallLocation</c> directory that contains it (archive/zip
    /// portable, the shape Aspire actually ships). See
    /// <see cref="WingetAspireEntryMatcher"/> for the matching contract and the
    /// winget-cli sources behind it.
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
            // Winget always writes to the 64-bit registry view (winget-cli:
            // src/AppInstallerCommonCore/PortableARPEntry.cpp uses
            // KEY_WOW64_64KEY for both HKCU and machine x64 scopes).
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

                // Read the identifier first and short-circuit on non-Aspire entries
                // before incurring three more RegQueryValueEx calls per subkey.
                // Uninstall has hundreds of subkeys on a typical machine and this
                // probe runs on every CLI invocation that lacks the sidecar.
                var identifier = entry.GetValue(WingetAspireEntryMatcher.PackageIdentifierValueName) as string;
                if (!WingetAspireEntryMatcher.HasAspirePackageIdentifier(identifier))
                {
                    continue;
                }

                var installerType = entry.GetValue(WingetAspireEntryMatcher.InstallerTypeValueName) as string;
                var targetFullPath = entry.GetValue(WingetAspireEntryMatcher.TargetFullPathValueName) as string;
                var installLocation = entry.GetValue(WingetAspireEntryMatcher.InstallLocationValueName) as string;

                if (WingetAspireEntryMatcher.Matches(canonicalProcessPath, identifier, installerType, targetFullPath, installLocation))
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
/// Pure matcher that decides whether a single uninstall-registry entry
/// represents the winget portable install that placed the running CLI binary.
/// Pulled out of <see cref="WindowsRegistryReader"/> so the matching rules can
/// be unit-tested on every platform without touching the Windows registry.
/// </summary>
/// <remarks>
/// Two registry shapes are accepted, both confirmed against the winget-cli
/// source at <see href="https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCommonCore/PortableARPEntry.cpp"/>:
/// <list type="number">
/// <item>
/// <description>
/// Single-file portable: winget writes the <c>TargetFullPath</c> value (note
/// the C++ enum identifier is <c>PortableTargetFullPath</c> but the actual
/// registry string is just <c>"TargetFullPath"</c> — see
/// <c>PortableARPEntry.cpp</c> <c>s_PortableTargetFullPath = L"TargetFullPath"</c>)
/// to the full path of the installed exe.
/// </description>
/// </item>
/// <item>
/// <description>
/// Archive portable (<c>InstallerType: zip</c> + <c>NestedInstallerType: portable</c>,
/// the shape <c>eng/winget/microsoft.aspire/Aspire.installer.yaml.template</c>
/// declares): winget extracts the archive and skips per-file ARP writes
/// (<c>PortableInstaller::InstallFile</c> guards
/// <c>CommitToARPEntry(PortableTargetFullPath)</c> behind <c>!RecordToIndex</c>,
/// and <c>PortableFlow::GetDesiredStateForPortableInstall</c> sets
/// <c>RecordToIndex = true</c> for any directory extraction). The only
/// always-written pointer is <c>InstallLocation</c>, the extraction directory
/// the running binary lives inside.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class WingetAspireEntryMatcher
{
    internal const string PackageIdentifierValueName = "WinGetPackageIdentifier";
    internal const string InstallerTypeValueName = "WinGetInstallerType";
    internal const string TargetFullPathValueName = "TargetFullPath";
    internal const string InstallLocationValueName = "InstallLocation";

    internal const string AspirePackageIdentifier = "Microsoft.Aspire";
    internal const string PortableInstallerType = "portable";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="packageIdentifier"/>
    /// matches the Aspire winget package identifier. Exposed so callers that
    /// iterate registry subkeys can short-circuit non-Aspire entries before
    /// reading the other matcher inputs.
    /// </summary>
    public static bool HasAspirePackageIdentifier(string? packageIdentifier)
        => string.Equals(packageIdentifier, AspirePackageIdentifier, StringComparison.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> when the supplied registry-entry values
    /// identify a winget portable install of the Aspire CLI that placed
    /// <paramref name="canonicalProcessPath"/>. The caller is responsible for
    /// canonicalizing <paramref name="canonicalProcessPath"/> via
    /// <see cref="Path.GetFullPath(string)"/> before calling.
    /// </summary>
    public static bool Matches(
        string canonicalProcessPath,
        string? packageIdentifier,
        string? installerType,
        string? targetFullPath,
        string? installLocation)
    {
        if (string.IsNullOrEmpty(canonicalProcessPath))
        {
            return false;
        }

        if (!HasAspirePackageIdentifier(packageIdentifier))
        {
            return false;
        }

        // WinGetInstallerType filters out any future non-portable Aspire winget
        // installer (e.g. MSI) so this probe stays narrowly scoped to the
        // portable shapes it knows how to reason about. Empty/missing values
        // are tolerated for backward compat with older winget builds that may
        // not have written the field on every entry.
        if (!string.IsNullOrEmpty(installerType)
            && !string.Equals(installerType, PortableInstallerType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(targetFullPath)
            && TryCanonicalize(targetFullPath) is string canonicalTarget
            && string.Equals(canonicalTarget, canonicalProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(installLocation)
            && TryCanonicalize(installLocation) is string canonicalInstallDir
            && IsProcessUnderDirectory(canonicalProcessPath, canonicalInstallDir))
        {
            return true;
        }

        return false;
    }

    private static string? TryCanonicalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsProcessUnderDirectory(string canonicalProcessPath, string canonicalDirectory)
    {
        // Trim a single trailing separator so "C:\\Foo\\" and "C:\\Foo" behave
        // the same. The separator boundary check below prevents "C:\\Foo" from
        // falsely matching "C:\\FooBar\\aspire.exe".
        var directory = canonicalDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (directory.Length == 0)
        {
            return false;
        }

        if (canonicalProcessPath.Length <= directory.Length + 1)
        {
            return false;
        }

        if (!canonicalProcessPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nextChar = canonicalProcessPath[directory.Length];
        return nextChar == Path.DirectorySeparatorChar || nextChar == Path.AltDirectorySeparatorChar;
    }
}

/// <summary>
/// Non-Windows <see cref="IWindowsRegistryReader"/>. Always returns <see langword="false"/>.
/// </summary>
internal sealed class NullWindowsRegistryReader : IWindowsRegistryReader
{
    public bool HasWingetAspireUninstallEntry(string processPath) => false;
}
