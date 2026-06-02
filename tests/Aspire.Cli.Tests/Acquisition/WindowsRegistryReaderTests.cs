// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Aspire.Cli.Acquisition;
using Microsoft.Win32;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Round-trip tests for <see cref="WindowsRegistryReader"/> against a real
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c> hive. Each
/// test writes a uniquely-named subkey, exercises the reader, and deletes the
/// subkey in a <c>finally</c>. Crashed leftover subkeys are swept on next run
/// by <see cref="TestUninstallEntry.SweepStaleEntries"/>.
/// </summary>
/// <remarks>
/// All tests skip on non-Windows hosts via <see cref="Assert.SkipUnless(bool, string?)"/>.
/// <see cref="SupportedOSPlatformAttribute"/> on each method also keeps the
/// platform-compat analyzer (CA1416) happy for the <c>Microsoft.Win32</c> calls.
/// </remarks>
public class WindowsRegistryReaderTests
{
    static WindowsRegistryReaderTests()
    {
        // Defense in depth: if a previous test run was killed mid-test (CI
        // timeout, OS reboot, etc.) the uninstall subkey will still be in HKCU.
        // Sweep any "Microsoft.Aspire.Tests.*" leftovers before running so a
        // stray entry can't cause an unrelated test to flake.
        if (OperatingSystem.IsWindows())
        {
            TestUninstallEntry.SweepStaleEntries();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_ReturnsTrue_WhenHkcuEntryMatches()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        using var binary = new TestTempDirectory();
        var portableTarget = Path.Combine(binary.Path, "aspire.exe");
        File.WriteAllBytes(portableTarget, []);

        using var entry = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: portableTarget);

        var reader = new WindowsRegistryReader();
        Assert.True(reader.HasWingetAspireUninstallEntry(portableTarget));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_ReturnsFalse_WhenIdentifierDoesNotMatch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        using var binary = new TestTempDirectory();
        var portableTarget = Path.Combine(binary.Path, "aspire.exe");
        File.WriteAllBytes(portableTarget, []);

        // Same shape as a real winget Aspire entry except the identifier is wrong.
        // The reader must not match: it's specifically checking for "Microsoft.Aspire".
        using var entry = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.NotAspire",
            portableTarget: portableTarget);

        var reader = new WindowsRegistryReader();
        Assert.False(reader.HasWingetAspireUninstallEntry(portableTarget));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_ReturnsFalse_WhenPortableTargetMissing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        // A real Aspire winget portable install always writes PortableTargetFullPath;
        // an entry that omits it is malformed and the reader must skip it rather
        // than match on identifier alone.
        using var entry = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: null);

        var reader = new WindowsRegistryReader();
        Assert.False(reader.HasWingetAspireUninstallEntry(@"C:\anything\aspire.exe"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_ReturnsFalse_WhenPortableTargetPointsElsewhere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        using var binary = new TestTempDirectory();
        var registeredTarget = Path.Combine(binary.Path, "aspire.exe");
        var queriedTarget = Path.Combine(binary.Path, "different.exe");

        using var entry = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: registeredTarget);

        var reader = new WindowsRegistryReader();
        Assert.False(reader.HasWingetAspireUninstallEntry(queriedTarget));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_MatchesPathCaseInsensitively()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        // Windows filesystem paths are case-insensitive on NTFS by default. The
        // reader documents OrdinalIgnoreCase comparison; this test pins that.
        using var binary = new TestTempDirectory();
        var registeredTarget = Path.Combine(binary.Path, "aspire.exe").ToLowerInvariant();
        var queriedTarget = registeredTarget.ToUpperInvariant();

        using var entry = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: registeredTarget);

        var reader = new WindowsRegistryReader();
        Assert.True(reader.HasWingetAspireUninstallEntry(queriedTarget));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_ReturnsFalse_ForEmptyProcessPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        var reader = new WindowsRegistryReader();
        Assert.False(reader.HasWingetAspireUninstallEntry(string.Empty));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasWingetAspireUninstallEntry_IgnoresUnrelatedEntries()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsRegistryReader is Windows-only.");

        // Surrounding the matching entry with unrelated identifier+target rows
        // (mimicking the noisy real-world Uninstall hive) must not change the
        // result. Both arms of the iterator are exercised: skip-on-identifier
        // and skip-on-missing-target.
        using var binary = new TestTempDirectory();
        var portableTarget = Path.Combine(binary.Path, "aspire.exe");
        File.WriteAllBytes(portableTarget, []);

        using var unrelated1 = TestUninstallEntry.Create(
            packageIdentifier: "Some.Other.Package",
            portableTarget: portableTarget);
        using var unrelated2 = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: null);
        using var matching = TestUninstallEntry.Create(
            packageIdentifier: "Microsoft.Aspire",
            portableTarget: portableTarget);

        var reader = new WindowsRegistryReader();
        Assert.True(reader.HasWingetAspireUninstallEntry(portableTarget));
    }
}

/// <summary>
/// Creates a fake winget portable uninstall entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall</c> with a
/// unique <c>Microsoft.Aspire.Tests.&lt;guid&gt;</c> subkey name, and deletes
/// it on dispose. Static <see cref="SweepStaleEntries"/> removes any leftovers
/// from crashed test runs so a stale entry can never affect a fresh run.
/// </summary>
file sealed class TestUninstallEntry : IDisposable
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    // Distinctive prefix so leftover entries can be swept without affecting
    // unrelated keys under Uninstall.
    private const string TestSubKeyPrefix = "Microsoft.Aspire.Tests.";

    private readonly string _subKeyName;

    private TestUninstallEntry(string subKeyName)
    {
        _subKeyName = subKeyName;
    }

    [SupportedOSPlatform("windows")]
    public static TestUninstallEntry Create(string packageIdentifier, string? portableTarget)
    {
        var subKeyName = TestSubKeyPrefix + Guid.NewGuid().ToString("N");

        using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create HKCU\\{UninstallPath}.");
        using var entry = uninstall.CreateSubKey(subKeyName, writable: true)
            ?? throw new InvalidOperationException($"Could not create HKCU\\{UninstallPath}\\{subKeyName}.");

        entry.SetValue("WinGetPackageIdentifier", packageIdentifier, RegistryValueKind.String);
        if (portableTarget is not null)
        {
            entry.SetValue("PortableTargetFullPath", portableTarget, RegistryValueKind.String);
        }

        return new TestUninstallEntry(subKeyName);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TryDeleteSubKey(_subKeyName);
    }

    [SupportedOSPlatform("windows")]
    public static void SweepStaleEntries()
    {
        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(UninstallPath, writable: true);
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
                        // Best-effort sweep; tolerate per-entry deletion failure.
                    }
                }
            }
        }
        catch
        {
            // Best-effort sweep; tolerate the open failing entirely.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryDeleteSubKey(string subKeyName)
    {
        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(UninstallPath, writable: true);
            uninstall?.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort cleanup; stale entries are swept by the static
            // constructor on the next run.
        }
    }
}
