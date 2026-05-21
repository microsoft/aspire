// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;

namespace Aspire.Cli.Tests;

// Producer↔consumer coverage for BundleService.ComputeDefaultExtractDir: each
// distribution shape that ships an .aspire-install.json sidecar must land
// extraction at the install prefix the packager owns so versions/<id>/ stays
// inside the directory the packager actually manages. These tests construct the
// real on-disk layout each route produces, then assert the helper picks the
// directory whose versions/ subdirectory the bundle should populate. They are
// the regression net against a packager moving the sidecar or the bundle
// pipeline changing its switch on the sidecar's source field.
public class BundleServiceComputeDefaultExtractDirTests
{
    private const string SidecarFileName = ".aspire-install.json";

    [Theory]
    [InlineData("script")]    // get-aspire-cli.{sh,ps1}
    [InlineData("localhive")] // localhive.{sh,ps1}
    public void ComputeDefaultExtractDir_SharedPrefixSource_ReturnsParentOfBinaryDir(string source)
    {
        // Both the get-aspire-cli script route and the localhive route lay the
        // CLI out at <prefix>/bin/aspire with a colocated .aspire-install.json,
        // so extraction must land at <prefix>/ (parent of the binary's dir) so
        // the eventual versions/<id>/ tree sits next to the bin/ directory
        // rather than inside it.
        using var temp = new TestTempDirectory();
        var prefixDir = Path.Combine(temp.Path, "aspire");
        var binDir = Path.Combine(prefixDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, SidecarFileName), $"{{\"source\":\"{source}\"}}");

        var result = BundleService.ComputeDefaultExtractDir(binaryPath);

        Assert.Equal(prefixDir, result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_PRSource_ReturnsPRSubprefix()
    {
        // get-aspire-cli-pr.{sh,ps1} stages each PR build under a channel-isolated
        // dogfood/pr-<N>/bin subdirectory with its own colocated sidecar. The
        // sidecar value is always source=pr; the per-PR namespace lives in the
        // directory layout. Extraction must land at the dogfood/pr-<N>/ directory
        // — not the outer install root — to keep stable and PR-route versions/
        // trees from colliding.
        using var temp = new TestTempDirectory();
        var prDir = Path.Combine(temp.Path, "dogfood", "pr-12345");
        var binDir = Path.Combine(prDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, SidecarFileName), "{\"source\":\"pr\"}");

        var result = BundleService.ComputeDefaultExtractDir(binaryPath);

        Assert.Equal(prDir, result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_WingetSource_ReturnsInstallDirectory()
    {
        // Winget extracts the zip flat: aspire.exe and .aspire-install.json land in the
        // same install dir. Extraction must land in that directory (beside the binary),
        // not its parent — otherwise versions/<id>/ would leak above the package-managed
        // prefix that winget owns.
        using var temp = new TestTempDirectory();
        var installDir = Path.Combine(temp.Path, "WindowsApps", "Microsoft.Aspire_8wekyb3d8bbwe");
        Directory.CreateDirectory(installDir);

        var binaryPath = Path.Combine(installDir, "aspire.exe");
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(installDir, SidecarFileName), "{\"source\":\"winget\"}");

        var result = BundleService.ComputeDefaultExtractDir(binaryPath);

        Assert.Equal(installDir, result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_BrewSymlinkSource_ReturnsCellarPrefix_NotSymlinkParent()
    {
        // Regression net for the brew bug: /opt/homebrew/bin/aspire is a symlink into
        // the cellar (e.g. /opt/homebrew/Caskroom/aspire/<version>/aspire) where the
        // brew cask writes the binary AND .aspire-install.json side-by-side. The helper
        // follows the symlink before sidecar lookup; extraction must land at the cellar
        // prefix so `brew uninstall aspire` (which wipes that prefix per the cask
        // template) cleans the versions/<id>/ tree too. Using the symlink's parent
        // (/opt/homebrew/) would leak versions/ outside brew's ownership.
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var temp = new TestTempDirectory();

        var cellarDir = Path.Combine(temp.Path, "Caskroom", "aspire", "13.2.0");
        Directory.CreateDirectory(cellarDir);
        var cellarBinary = Path.Combine(cellarDir, "aspire");
        File.WriteAllText(cellarBinary, string.Empty);
        File.WriteAllText(Path.Combine(cellarDir, SidecarFileName), "{\"source\":\"brew\"}");

        var brewBinDir = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(brewBinDir);
        var symlinkPath = Path.Combine(brewBinDir, "aspire");
        File.CreateSymbolicLink(symlinkPath, cellarBinary);

        var result = BundleService.ComputeDefaultExtractDir(symlinkPath);

        Assert.Equal(cellarDir, result);
        Assert.NotEqual(brewBinDir, result);
        Assert.NotEqual(Path.GetDirectoryName(brewBinDir), result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_DotnetToolSource_ReturnsToolStoreDirectory()
    {
        // RID-specific dotnet-tool nupkg lands at
        // ~/.dotnet/tools/.store/aspire.cli/<ver>/aspire.cli.<rid>/<ver>/tools/net10.0/<rid>/
        // with aspire and .aspire-install.json colocated. The dotnet-tool runtime
        // launches the binary directly from this RID directory; extraction must stay
        // within that same directory so versions/<id>/ moves with the tool.
        using var temp = new TestTempDirectory();
        var ridDir = Path.Combine(
            temp.Path,
            ".dotnet",
            "tools",
            ".store",
            "aspire.cli",
            "13.2.0",
            "aspire.cli.linux-x64",
            "13.2.0",
            "tools",
            "net10.0",
            "linux-x64");
        Directory.CreateDirectory(ridDir);

        var binaryPath = Path.Combine(ridDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(ridDir, SidecarFileName), "{\"source\":\"dotnet-tool\"}");

        var result = BundleService.ComputeDefaultExtractDir(binaryPath);

        Assert.Equal(ridDir, result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_UnmanagedFallback_PreservesLegacyParentOfBinaryDirHeuristic()
    {
        // No sidecar anywhere → fall back to "parent of binary's directory" so
        // installs that pre-date the sidecar continue to see the historical
        // ~/.aspire/bin/aspire → ~/.aspire/ mapping. Without this backwards-compat
        // path, existing CLI installs would silently move their extraction target
        // on upgrade and re-extract into the binary's own dir.
        using var temp = new TestTempDirectory();
        var prefixDir = Path.Combine(temp.Path, ".aspire");
        var binDir = Path.Combine(prefixDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        // No sidecar at <binDir>/.aspire-install.json.

        var result = BundleService.ComputeDefaultExtractDir(binaryPath);

        Assert.Equal(prefixDir, result);
    }

    [Fact]
    public void ComputeDefaultExtractDir_EmptyProcessPath_ReturnsNull()
    {
        var result = BundleService.ComputeDefaultExtractDir(string.Empty);

        Assert.Null(result);
    }

    private static string ExeName(string baseName)
        => OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}
