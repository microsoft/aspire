// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Utils;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Utils;

public class CliPathHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CreateGuestAppHostSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetFileName(socketPath1), socketPath1);
            Assert.Equal(Path.GetFileName(socketPath2), socketPath2);
            Assert.Matches("^h[A-Za-z0-9_-]{8}$", socketPath1);
            Assert.Matches("^h[A-Za-z0-9_-]{8}$", socketPath2);
        }
        else
        {
            var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "bch");
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
            Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath1));
            Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath2));
        }
    }

    [Fact]
    public void CreateUnixDomainSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "bch");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath1));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ComputeStagingFeedCacheKey_ReturnsNull_ForNullOrWhitespace(string? feedUrl)
    {
        Assert.Null(CliPathHelper.ComputeStagingFeedCacheKey(feedUrl));
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_DefaultsToEightLowercaseHexChars()
    {
        var key = CliPathHelper.ComputeStagingFeedCacheKey("https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json");

        Assert.NotNull(key);
        Assert.Matches("^[0-9a-f]{8}$", key);
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_IsDeterministic_ForSameInput()
    {
        const string feedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json";

        var first = CliPathHelper.ComputeStagingFeedCacheKey(feedUrl);
        var second = CliPathHelper.ComputeStagingFeedCacheKey(feedUrl);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_DifferentUrls_ProduceDifferentKeys()
    {
        var a = CliPathHelper.ComputeStagingFeedCacheKey("https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json");
        var b = CliPathHelper.ComputeStagingFeedCacheKey("https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-cafef00d/nuget/v3/index.json");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_NormalizesWhitespaceAndCasing()
    {
        // Trim + lowercase normalization keeps the cache from fragmenting when the same feed
        // shows up with stray whitespace from a config file or with a mixed-case hostname.
        const string baseUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json";

        var baseKey = CliPathHelper.ComputeStagingFeedCacheKey(baseUrl);
        var spacedKey = CliPathHelper.ComputeStagingFeedCacheKey("  " + baseUrl + "\t\n");
        var upperKey = CliPathHelper.ComputeStagingFeedCacheKey(baseUrl.ToUpperInvariant());

        Assert.Equal(baseKey, spacedKey);
        Assert.Equal(baseKey, upperKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void ComputeStagingFeedCacheKey_RespectsExplicitLength(int length)
    {
        var key = CliPathHelper.ComputeStagingFeedCacheKey(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json",
            length);

        Assert.NotNull(key);
        Assert.Equal(length, key.Length);
        Assert.Matches($"^[0-9a-f]{{{length}}}$", key);
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_LengthAboveHashWidth_ReturnsFullHash()
    {
        // XxHash3 is 64 bits -> 16 hex chars. Asking for more than 16 must not crash and must
        // return all available hash chars rather than padding with garbage.
        var key = CliPathHelper.ComputeStagingFeedCacheKey("https://example/index.json", length: 999);

        Assert.NotNull(key);
        Assert.Equal(16, key.Length);
        Assert.Matches("^[0-9a-f]{16}$", key);
    }

    [Fact]
    public void ComputeStagingFeedCacheKey_NonZeroLength_RejectsZeroOrNegative()
    {
        Assert.Null(CliPathHelper.ComputeStagingFeedCacheKey("https://example/index.json", length: 0));
        Assert.Null(CliPathHelper.ComputeStagingFeedCacheKey("https://example/index.json", length: -1));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("localhive")]
    public void TryGetAspireHomeDirectoryFromInstallRoute_SharedPrefixRoute_ReturnsInstallPrefix(string source)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        var binDir = Path.Combine(installPrefix, "bin");
        var binaryPath = WriteBinaryWithSidecar(binDir, source);

        var result = CliPathHelper.TryGetAspireHomeDirectoryFromInstallRoute(binaryPath);

        Assert.Equal(installPrefix, result);
    }

    [Fact]
    public void TryGetAspireHomeDirectoryFromInstallRoute_PrRoute_ReturnsOuterInstallPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-pr-test");
        var binDir = Path.Combine(installPrefix, "dogfood", "pr-17159", "bin");
        var binaryPath = WriteBinaryWithSidecar(binDir, "pr");

        var result = CliPathHelper.TryGetAspireHomeDirectoryFromInstallRoute(binaryPath);

        Assert.Equal(installPrefix, result);
    }

    [Theory]
    [InlineData("brew")]
    [InlineData("winget")]
    [InlineData("dotnet-tool")]
    [InlineData("unknown")]
    public void TryGetAspireHomeDirectoryFromInstallRoute_PackageManagerOrUnknownRoute_ReturnsNull(string source)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var binaryPath = WriteBinaryWithSidecar(workspace.WorkspaceRoot.FullName, source);

        var result = CliPathHelper.TryGetAspireHomeDirectoryFromInstallRoute(binaryPath);

        Assert.Null(result);
    }

    [Fact]
    public void GetAspireHomeDirectory_PrRoute_UsesOuterInstallPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "portable");
        var binDir = Path.Combine(installPrefix, "dogfood", "pr-17159", "bin");
        var binaryPath = WriteBinaryWithSidecar(binDir, "pr");

        var result = CliPathHelper.GetAspireHomeDirectory(binaryPath);

        Assert.Equal(installPrefix, result);
    }

    [Fact]
    public void GetDefaultAspireHomeDirectory_UsesConfiguredAspireHome()
    {
        var result = CliPathHelper.GetDefaultAspireHomeDirectory("/custom/aspire-home", "/home/user");

        Assert.Equal("/custom/aspire-home", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDefaultAspireHomeDirectory_WithoutConfiguredAspireHome_UsesUserProfile(string? configuredAspireHome)
    {
        var result = CliPathHelper.GetDefaultAspireHomeDirectory(configuredAspireHome, "/home/user");

        Assert.Equal(Path.Combine("/home/user", ".aspire"), result);
    }

    [Fact]
    public void ResolveSymlinkOrOriginalPath_NonLink_ReturnsOriginalPath()
    {
        const string path = "relative/path/aspire";

        var result = CliPathHelper.ResolveSymlinkOrOriginalPath(path);

        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolveSymlinkToFullPath_NonLink_ReturnsNormalizedFullPath()
    {
        var path = Path.Combine("relative", "path", "aspire");

        var result = CliPathHelper.ResolveSymlinkToFullPath(path);

        Assert.Equal(Path.GetFullPath(path), result);
    }

    [Fact]
    public void ResolveSymlinkToFullPath_InvalidPath_ReturnsNull()
    {
        var result = CliPathHelper.ResolveSymlinkToFullPath("invalid\0path");

        Assert.Null(result);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.")]
    public void ResolveSymlinkHelpers_Link_ReturnsTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var target = Path.Combine(workspace.WorkspaceRoot.FullName, "target-aspire");
        File.WriteAllText(target, string.Empty);

        var link = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(target, CliPathHelper.ResolveSymlinkOrOriginalPath(link));
        Assert.Equal(target, CliPathHelper.ResolveSymlinkToFullPath(link));
    }

    // macOS uses APFS firmlinks (/var → /private/var, /tmp → /private/tmp,
    // /etc → /private/etc) that .NET's Environment.ProcessPath and libc
    // realpath() resolve to the /private/* form, while Path.GetFullPath,
    // PathLookupHelper $PATH walks, and NuGet packageSourceMapping lookups
    // use the un-prefixed form. The stripper rewrites the /private/* form
    // back to the user-facing logical form so every comparison site agrees.
    // Tests are table-driven and OS-agnostic — the stripper itself runs the
    // same logic regardless of host OS; OS-gating happens in the wrapping
    // resolve helpers (covered below).
    [Theory]
    [InlineData("/private/var/folders/X/aspire", "/var/folders/X/aspire")]
    [InlineData("/private/tmp/aspire-pr-test/bin/aspire", "/tmp/aspire-pr-test/bin/aspire")]
    [InlineData("/private/etc/hosts", "/etc/hosts")]
    // Exact-prefix matches: the input is exactly the firmlink, no trailing slash or path component.
    [InlineData("/private/var", "/var")]
    [InlineData("/private/tmp", "/tmp")]
    [InlineData("/private/etc", "/etc")]
    // Trailing-slash form preserves the trailing slash after the rewrite.
    [InlineData("/private/var/", "/var/")]
    // Boundary safety: matching segments must end at a path separator. /private/varlog
    // is NOT a firmlink (varlog is a non-existent sibling of var). Preserve as-is.
    [InlineData("/private/varlog", "/private/varlog")]
    [InlineData("/private/varfoo/X", "/private/varfoo/X")]
    [InlineData("/private/tmpfile", "/private/tmpfile")]
    [InlineData("/private/etchosts", "/private/etchosts")]
    // No-firmlink shapes pass through unchanged.
    [InlineData("/var/folders/X/aspire", "/var/folders/X/aspire")]
    [InlineData("/tmp/aspire", "/tmp/aspire")]
    [InlineData("/etc/hosts", "/etc/hosts")]
    [InlineData("/usr/local/bin/aspire", "/usr/local/bin/aspire")]
    [InlineData("/Users/ankj/.aspire/bin/aspire", "/Users/ankj/.aspire/bin/aspire")]
    // /private without a firmlink subdir, or with a non-firmlink subdir, must not strip.
    // /Users, /Applications, /Library are real on macOS (not firmlinked through /private).
    [InlineData("/private", "/private")]
    [InlineData("/private/", "/private/")]
    [InlineData("/private/Users/foo", "/private/Users/foo")]
    [InlineData("/private/opt/X", "/private/opt/X")]
    // Empty input passes through unchanged.
    [InlineData("", "")]
    // Windows-style path: starts with a drive letter, so the /private/ prefix can't match.
    [InlineData(@"C:\Users\X\.aspire\bin\aspire.exe", @"C:\Users\X\.aspire\bin\aspire.exe")]
    public void StripMacOSFirmlinkPrefix_RewritesFirmlinksAndPreservesEverythingElse(string input, string expected)
    {
        Assert.Equal(expected, CliPathHelper.StripMacOSFirmlinkPrefix(input));
    }

    [Fact]
    public void StripMacOSFirmlinkPrefix_IsCaseSensitive()
    {
        // APFS case-sensitive volumes can host a literal /Private/Var/... directory tree
        // that is not the firmlink. Use Ordinal comparison so we never rewrite a real
        // user-created path that only differs in case from the firmlink prefix.
        Assert.Equal("/Private/Var/folders/X", CliPathHelper.StripMacOSFirmlinkPrefix("/Private/Var/folders/X"));
        Assert.Equal("/PRIVATE/VAR/folders/X", CliPathHelper.StripMacOSFirmlinkPrefix("/PRIVATE/VAR/folders/X"));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.Linux, "Firmlink stripping in resolve helpers only applies on macOS.")]
    public void ResolveSymlinkToFullPath_OnMacOS_StripsFirmlinkPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Place a real file under the workspace (which sits on /var/folders on macOS),
        // then construct the firmlinked-form input by prepending /private. Both forms
        // resolve to the same physical file at the kernel level, so File.Exists
        // returns true for either spelling.
        var realPath = Path.Combine(workspace.WorkspaceRoot.FullName, "binary-under-test");
        File.WriteAllText(realPath, string.Empty);

        var firmlinkedPath = "/private" + realPath;
        Assert.True(File.Exists(firmlinkedPath), "firmlinked /private/var path should resolve to the same physical file");

        var result = CliPathHelper.ResolveSymlinkToFullPath(firmlinkedPath);

        Assert.Equal(realPath, result);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.Linux, "Firmlink stripping in resolve helpers only applies on macOS.")]
    public void ResolveSymlinkOrOriginalPath_OnMacOS_StripsFirmlinkPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var realPath = Path.Combine(workspace.WorkspaceRoot.FullName, "binary-under-test");
        File.WriteAllText(realPath, string.Empty);

        var firmlinkedPath = "/private" + realPath;

        var result = CliPathHelper.ResolveSymlinkOrOriginalPath(firmlinkedPath);

        Assert.Equal(realPath, result);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.Linux, "Firmlink propagation only applies on macOS.")]
    public void GetAspireHomeDirectory_OnMacOS_PrRouteWithFirmlinkedProcessPath_ReturnsUnfirmlinkedPrefix()
    {
        // Bug B regression: when Environment.ProcessPath comes back firmlinked (/private/var/...),
        // every derivation hanging off it (AspireHome → HivesDirectory → PackagingService source path)
        // inherits the /private/ form and lands in nuget.config in a shape NuGet's packageSourceMapping
        // lookup silently drops. The fix lives in the resolve helpers; this test pins the propagation
        // so a future refactor doesn't reintroduce the asymmetry.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "portable");
        var binDir = Path.Combine(installPrefix, "dogfood", "pr-17105", "bin");
        var binaryPath = WriteBinaryWithSidecar(binDir, "pr");

        // Construct the firmlinked-form input. workspace.WorkspaceRoot is rooted under
        // /var/folders, so prepending /private produces the form Environment.ProcessPath
        // would carry on macOS.
        var firmlinkedProcessPath = "/private" + binaryPath;

        var result = CliPathHelper.GetAspireHomeDirectory(firmlinkedProcessPath);

        // Expected un-firmlinked AspireHome: drop /private/ from the prefix.
        Assert.Equal(installPrefix, result);
        Assert.DoesNotContain("/private/", result, StringComparison.Ordinal);
    }

    private static string WriteBinaryWithSidecar(string binaryDir, string source)
    {
        Directory.CreateDirectory(binaryDir);
        var binaryPath = Path.Combine(binaryDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName), $$"""{"source":"{{source}}"}""");

        return binaryPath;
    }

    [Fact]
    public void CleanupStaleCliSockets_DeletesFilesOlderThanThreshold()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-cli-sockets-");
        try
        {
            var staleFile = BackchannelConstants.ComputeCliSocketPath(tempRoot.FullName, "cli.sock");
            Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
            File.WriteAllText(staleFile, string.Empty);
            var freshFile = BackchannelConstants.ComputeCliSocketPath(tempRoot.FullName, "cli.sock");
            File.WriteAllText(freshFile, string.Empty);

            var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            File.SetLastWriteTimeUtc(staleFile, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(freshFile, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));

            var socketDirectory = Path.GetDirectoryName(staleFile)!;
            var deleted = CliPathHelper.CleanupStaleCliSockets(socketDirectory, TimeSpan.FromHours(24), fakeTime);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(staleFile));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void CleanupStaleCliSockets_OnlyMatchesCliSockPrefix()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-cli-sockets-");
        try
        {
            var matching = BackchannelConstants.ComputeCliSocketPath(tempRoot.FullName, "cli.sock");
            Directory.CreateDirectory(Path.GetDirectoryName(matching)!);
            File.WriteAllText(matching, string.Empty);
            var unrelated = BackchannelConstants.ComputeCliSocketPath(tempRoot.FullName, "apphost.sock");
            File.WriteAllText(unrelated, string.Empty);

            var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            File.SetLastWriteTimeUtc(matching, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(unrelated, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));

            var socketDirectory = Path.GetDirectoryName(matching)!;
            var deleted = CliPathHelper.CleanupStaleCliSockets(socketDirectory, TimeSpan.FromHours(24), fakeTime);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(matching));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void CleanupStaleCliSockets_MissingDirectoryIsNoOp()
    {
        // Create-then-delete to guarantee a unique path we know doesn't exist on disk.
        var probe = Directory.CreateTempSubdirectory("aspire-cli-sockets-missing-");
        var missingDir = probe.FullName;
        probe.Delete();

        var deleted = CliPathHelper.CleanupStaleCliSockets(missingDir, TimeSpan.FromHours(24));

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void CleanupStaleCliSockets_EmptyDirectoryReturnsZero()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-cli-sockets-");
        try
        {
            var deleted = CliPathHelper.CleanupStaleCliSockets(tempDir.FullName, TimeSpan.FromHours(24));

            Assert.Equal(0, deleted);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
