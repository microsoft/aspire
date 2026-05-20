// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Utils;

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
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", socketPath1);
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", socketPath2);
        }
        else
        {
            var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "runtime", "sockets");
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath1));
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath2));
        }
    }

    [Fact]
    public void CreateUnixDomainSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "runtime", "sockets");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
        Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath1));
        Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath2));
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
}
