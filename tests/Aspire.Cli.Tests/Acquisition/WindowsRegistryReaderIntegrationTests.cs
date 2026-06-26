// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Aspire.Cli.Acquisition;
using Microsoft.Win32;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Windows-only integration tests for <see cref="WindowsRegistryReader"/> that
/// write a real-shaped winget Add/Remove Programs entry to <c>HKCU</c> and walk
/// the live registry, then clean up.
///
/// These complement <see cref="WingetAspireEntryMatcherTests"/>: the matcher
/// tests cover the pure predicate, but they cannot catch a regression that
/// flips one of the registry value-name constants
/// (<c>WingetAspireEntryMatcher.TargetFullPathValueName</c>,
/// <c>InstallLocationValueName</c>, <c>PackageIdentifierValueName</c>,
/// <c>InstallerTypeValueName</c>) to the wrong wire string — exactly the class
/// of bug that shipped (<c>"PortableTargetFullPath"</c> when winget actually
/// writes <c>"TargetFullPath"</c>).
///
/// Tests use a unique GUID-suffixed subkey under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c>, plus
/// process paths under a fresh temp directory, so they cannot collide with or
/// be confused by a real Aspire winget install that may exist on the host. The
/// matcher iterates all subkeys and matches purely on values, so the subkey
/// name only affects cleanup precision.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsRegistryReaderIntegrationTests
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string TestSubKeyPrefix = "Microsoft.Aspire_AspireCliTests_";

    static WindowsRegistryReaderIntegrationTests()
    {
        // Purge any subkeys leaked by crashed prior runs before any test
        // creates a new entry. Without this, the orphans accumulate in
        // HKCU\...\Uninstall and show up in Settings -> Apps as
        // "Aspire CLI (...test) 0.0.0" entries on developer machines. Each
        // entry's unique GUID-suffixed name makes the prefix scan safe.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var uninstall = baseKey.OpenSubKey(UninstallSubKey, writable: true);
            if (uninstall is null)
            {
                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                if (name.StartsWith(TestSubKeyPrefix, StringComparison.Ordinal))
                {
                    try
                    {
                        uninstall.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    }
                    catch
                    {
                        // Best-effort: one stuck orphan must not block the
                        // rest of the reaping pass or the test run.
                    }
                }
            }
        }
        catch
        {
            // Reaping is best-effort; the per-test Dispose still runs.
        }
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_ArchivePortableShape_MatchesViaInstallLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var installDir = Path.Combine(workspace.Path, "WinGet", "Packages", "Microsoft.Aspire_TestSource");
        Directory.CreateDirectory(installDir);
        var processPath = Path.Combine(installDir, "aspire.exe");

        using var entry = TestUninstallEntry.CreatePortableArchiveShape(installDir);

        var reader = new WindowsRegistryReader();

        Assert.True(reader.HasWingetAspireUninstallEntry(processPath),
            "Reader must match the archive-portable shape via InstallLocation (the shape Aspire's winget manifest produces).");
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_SingleFilePortableShape_MatchesViaTargetFullPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var installDir = Path.Combine(workspace.Path, "WinGet", "Packages", "Microsoft.Aspire_SingleFile");
        Directory.CreateDirectory(installDir);
        var processPath = Path.Combine(installDir, "aspire.exe");

        using var entry = TestUninstallEntry.CreatePortableSingleFileShape(processPath);

        var reader = new WindowsRegistryReader();

        Assert.True(reader.HasWingetAspireUninstallEntry(processPath),
            "Reader must match the single-file portable shape via TargetFullPath.");
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_WrongPackageIdentifier_DoesNotMatch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var installDir = Path.Combine(workspace.Path, "Other.Package");
        Directory.CreateDirectory(installDir);
        var processPath = Path.Combine(installDir, "aspire.exe");

        using var entry = TestUninstallEntry.Create(values: new()
        {
            ["WinGetPackageIdentifier"] = "Microsoft.NotAspire",
            ["WinGetInstallerType"] = "portable",
            ["InstallLocation"] = installDir,
        });

        var reader = new WindowsRegistryReader();

        Assert.False(reader.HasWingetAspireUninstallEntry(processPath),
            "Reader must not match entries whose WinGetPackageIdentifier is not Microsoft.Aspire.");
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_NonPortableInstallerType_DoesNotMatch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var installDir = Path.Combine(workspace.Path, "MsiInstall");
        Directory.CreateDirectory(installDir);
        var processPath = Path.Combine(installDir, "aspire.exe");

        using var entry = TestUninstallEntry.Create(values: new()
        {
            ["WinGetPackageIdentifier"] = "Microsoft.Aspire",
            ["WinGetInstallerType"] = "msi",
            ["InstallLocation"] = installDir,
        });

        var reader = new WindowsRegistryReader();

        Assert.False(reader.HasWingetAspireUninstallEntry(processPath),
            "Reader must reject non-portable installer types so a hypothetical future MSI install is not silently treated as portable.");
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_AspireEntryButPathOutsideInstallDir_DoesNotMatch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var installDir = Path.Combine(workspace.Path, "real-winget-dir");
        Directory.CreateDirectory(installDir);
        // Process lives somewhere else entirely.
        var unrelatedProcessPath = Path.Combine(workspace.Path, "elsewhere", "aspire.exe");

        using var entry = TestUninstallEntry.CreatePortableArchiveShape(installDir);

        var reader = new WindowsRegistryReader();

        Assert.False(reader.HasWingetAspireUninstallEntry(unrelatedProcessPath),
            "Reader must not match when the running process path is not inside the winget InstallLocation, even when an Aspire entry exists.");
    }

    [Fact]
    public void HasWingetAspireUninstallEntry_AspireEntryWithNoPathEvidence_DoesNotMatch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Real-registry test is Windows-only.");

        using var workspace = new TestTempDirectory();
        var processPath = Path.Combine(workspace.Path, "aspire.exe");

        using var entry = TestUninstallEntry.Create(values: new()
        {
            ["WinGetPackageIdentifier"] = "Microsoft.Aspire",
            ["WinGetInstallerType"] = "portable",
            // No InstallLocation, no TargetFullPath: no way to attribute the path.
            ["DisplayName"] = "Aspire CLI (no path)",
        });

        var reader = new WindowsRegistryReader();

        Assert.False(reader.HasWingetAspireUninstallEntry(processPath),
            "Reader must require at least one path-evidence value (TargetFullPath or InstallLocation) before claiming an entry owns the running process.");
    }

    /// <summary>
    /// IDisposable wrapper that creates a uniquely-named subkey under
    /// HKCU\...\Uninstall, populates it with the supplied values using the
    /// exact wire names winget writes, and deletes the subkey on dispose.
    /// Writing to HKCU does not require elevation.
    /// </summary>
    private sealed class TestUninstallEntry : IDisposable
    {
        private readonly string _subKeyName;

        private TestUninstallEntry(string subKeyName)
        {
            _subKeyName = subKeyName;
        }

        public static TestUninstallEntry Create(Dictionary<string, string> values)
        {
            // Unique per-test subkey so concurrent test runs and any real Aspire
            // winget install on the machine cannot interfere with cleanup. The
            // reader matches on values, not names, so this naming is purely a
            // cleanup-precision concern.
            var subKeyName = $"{TestSubKeyPrefix}{Guid.NewGuid():N}";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            // HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall is created lazily
            // and may not exist on a fresh user profile (observed on a GitHub Actions
            // windows-latest runneradmin profile where nothing had yet registered a
            // user-scope ARP entry). CreateSubKey opens-if-exists / creates-if-missing,
            // and HKCU writes do not require elevation.
            using var uninstall = baseKey.CreateSubKey(UninstallSubKey, writable: true)
                ?? throw new InvalidOperationException($"HKCU\\{UninstallSubKey} could not be opened or created for write.");
            using var entry = uninstall.CreateSubKey(subKeyName, writable: true);
            foreach (var (name, value) in values)
            {
                entry.SetValue(name, value, RegistryValueKind.String);
            }
            return new TestUninstallEntry(subKeyName);
        }

        /// <summary>
        /// Mirrors what winget actually writes for an archive/zip portable
        /// install (the shape Aspire's manifest produces). No TargetFullPath
        /// is written for this shape; InstallLocation is the only path
        /// pointer. Live registry verified on a current Aspire winget install.
        /// </summary>
        public static TestUninstallEntry CreatePortableArchiveShape(string installLocation)
            => Create(new Dictionary<string, string>
            {
                ["WinGetPackageIdentifier"] = "Microsoft.Aspire",
                ["WinGetSourceIdentifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                ["WinGetInstallerType"] = "portable",
                ["InstallLocation"] = installLocation,
                ["InstallDirectoryAddedToPath"] = "1",
                ["DisplayName"] = "Aspire CLI (archive portable, test)",
                ["DisplayVersion"] = "0.0.0",
                ["Publisher"] = "Microsoft Corporation",
            });

        /// <summary>
        /// Mirrors what winget writes for a single-file portable install: the
        /// TargetFullPath value (registry wire name <c>TargetFullPath</c> —
        /// note the C++ enum identifier <c>PortableTargetFullPath</c> maps to
        /// this wire string — see winget-cli
        /// <c>src/AppInstallerCommonCore/PortableARPEntry.cpp</c>).
        /// </summary>
        public static TestUninstallEntry CreatePortableSingleFileShape(string targetFullPath)
            => Create(new Dictionary<string, string>
            {
                ["WinGetPackageIdentifier"] = "Microsoft.Aspire",
                ["WinGetSourceIdentifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                ["WinGetInstallerType"] = "portable",
                ["TargetFullPath"] = targetFullPath,
                ["DisplayName"] = "Aspire CLI (single-file portable, test)",
                ["DisplayVersion"] = "0.0.0",
                ["Publisher"] = "Microsoft Corporation",
            });

        public void Dispose()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var uninstall = baseKey.OpenSubKey(UninstallSubKey, writable: true);
                uninstall?.DeleteSubKeyTree(_subKeyName, throwOnMissingSubKey: false);
            }
            catch
            {
                // Best-effort cleanup. A leaked subkey under HKCU is named with
                // a unique guid prefix so it is grep-able and safe to clean by
                // hand if needed.
            }
        }
    }
}
