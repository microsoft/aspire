// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class WingetAspireEntryMatcherTests
{
    // Archive (zip + NestedInstallerType: portable) shape — the layout Aspire actually
    // ships. winget writes InstallLocation + (often) InstallDirectoryAddedToPath=1 and
    // omits TargetFullPath entirely because the per-file ARP writes in
    // PortableInstaller::InstallFile are gated behind !RecordToIndex, and
    // GetDesiredStateForPortableInstall always sets RecordToIndex=true when winget
    // extracts a directory. Verified against winget-cli sources cited on
    // WingetAspireEntryMatcher.

    [Fact]
    public void Matches_ArchiveShape_ProcessAtInstallRoot_Matches()
    {
        var installDir = MakePath("C:", "Users", "u", "AppData", "Local", "Microsoft", "WinGet", "Packages", "Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var processPath = Path.Combine(installDir, "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_ArchiveShape_ProcessInSubdirOfInstallLocation_Matches()
    {
        // Future-proofing: if a winget manifest ever declares
        // NestedInstallerFiles[].RelativeFilePath: bin/aspire.exe, the binary will
        // live in a subdirectory of InstallLocation. Containment matching covers it.
        var installDir = MakePath("C:", "ProgramData", "WinGet", "Microsoft.Aspire");
        var processPath = MakePath("C:", "ProgramData", "WinGet", "Microsoft.Aspire", "bin", "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_ArchiveShape_TrailingSeparatorOnInstallLocation_Matches()
    {
        var installDir = MakePath("C:", "tmp", "winget", "aspire") + Path.DirectorySeparatorChar;
        var processPath = MakePath("C:", "tmp", "winget", "aspire", "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_SingleFileShape_TargetFullPathExactMatch_Matches()
    {
        // Single-file portable: winget writes TargetFullPath (NOT
        // "PortableTargetFullPath" — the C++ enum identifier is misleading; the wire
        // name in winget-cli/PortableARPEntry.cpp is L"TargetFullPath"). Some future
        // Aspire installer shape could land here.
        var processPath = MakePath("C:", "Users", "u", "WinGet", "Links", "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: processPath,
            installLocation: null));
    }

    [Fact]
    public void Matches_SingleFileShape_TargetFullPathCaseInsensitive_Matches()
    {
        // Windows paths compare case-insensitively. Drive letter casing differs.
        var processPath = MakePath("C:", "Users", "U", "WinGet", "Links", "aspire.exe");
        var targetFullPath = MakePath("c:", "users", "u", "winget", "links", "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: targetFullPath,
            installLocation: null));
    }

    [Fact]
    public void Matches_PrefixCollision_DoesNotFalselyMatch()
    {
        // C:\Foo is not a prefix of C:\FooBar — the separator boundary in
        // IsProcessUnderDirectory must reject this.
        var installDir = MakePath("C:", "Foo");
        var processPath = MakePath("C:", "FooBar", "aspire.exe");

        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_NonAspirePackage_DoesNotMatch()
    {
        var installDir = MakePath("C:", "tmp", "winget", "other");
        var processPath = Path.Combine(installDir, "aspire.exe");

        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Some.Other.Package",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_AspirePackageIdentifierIsCaseSensitive()
    {
        // The C++ enum and the manifest both use PascalCase "Microsoft.Aspire" — match it ordinal-exact
        // to guard against a future package id collision under different casing.
        var installDir = MakePath("C:", "tmp", "winget", "aspire");
        var processPath = Path.Combine(installDir, "aspire.exe");

        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "microsoft.aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_NonPortableInstallerType_BlocksMatch()
    {
        // Defense in depth: if Aspire ever ships an MSI manifest under the same
        // package id, the probe must not claim it as a portable install.
        var installDir = MakePath("C:", "tmp", "winget", "aspire-msi");
        var processPath = Path.Combine(installDir, "aspire.exe");

        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "msi",
            targetFullPath: null,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_EmptyInstallerType_TreatedAsAcceptableForBackCompat()
    {
        // Older winget builds may not have written WinGetInstallerType on every
        // entry. The matcher tolerates an empty value rather than excluding the
        // entry outright, so the package-identifier gate remains the primary filter.
        var installDir = MakePath("C:", "tmp", "winget", "aspire");
        var processPath = Path.Combine(installDir, "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: null,
            targetFullPath: null,
            installLocation: installDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Matches_EmptyProcessPath_DoesNotMatch(string? processPath)
    {
        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath!,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: MakePath("C:", "tmp")));
    }

    [Fact]
    public void Matches_BothPathValuesEmpty_DoesNotMatch()
    {
        // Identifier and installer type are correct, but no pointer to where the
        // binary lives — nothing to match against.
        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: MakePath("C:", "anywhere", "aspire.exe"),
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: null));
    }

    [Fact]
    public void Matches_TargetFullPathMismatch_FallsBackToInstallLocation()
    {
        // Both shapes coexist in the registry: TargetFullPath points elsewhere
        // (e.g. stale write from an earlier install), but InstallLocation still
        // contains the running binary. Containment match must still fire.
        var installDir = MakePath("C:", "tmp", "winget", "aspire");
        var processPath = Path.Combine(installDir, "aspire.exe");
        var staleTarget = MakePath("C:", "Old", "Path", "aspire.exe");

        Assert.True(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: processPath,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: staleTarget,
            installLocation: installDir));
    }

    [Fact]
    public void Matches_ProcessPathEqualsInstallLocation_DoesNotMatch()
    {
        // A directory path equal to InstallLocation is not a binary inside it.
        var installDir = MakePath("C:", "tmp", "winget", "aspire");

        Assert.False(WingetAspireEntryMatcher.Matches(
            canonicalProcessPath: installDir,
            packageIdentifier: "Microsoft.Aspire",
            installerType: "portable",
            targetFullPath: null,
            installLocation: installDir));
    }

    // Path-construction helper that produces an absolute, OS-appropriate path
    // on every platform the test suite runs on. Tests below run on Linux/macOS CI
    // too, where Windows drive-letter paths aren't valid roots; Path.Combine with
    // a leading "/" segment yields a clean absolute path on Unix.
    private static string MakePath(params string[] segments)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(segments);
        }

        // On Unix, drop "C:" style roots and synthesize an absolute path under /.
        var unixSegments = new List<string> { "/" };
        foreach (var segment in segments)
        {
            if (segment.EndsWith(':'))
            {
                continue;
            }
            unixSegments.Add(segment.TrimEnd('\\', '/'));
        }
        return Path.GetFullPath(Path.Combine([.. unixSegments]));
    }
}
