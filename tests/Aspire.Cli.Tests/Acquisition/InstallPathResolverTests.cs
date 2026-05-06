// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class InstallPathResolverTests
{
    private const string SidecarFileName = ".aspire-install.json";

    [Fact]
    public void Resolve_SidecarNextToBinary_ReturnsModeB()
    {
        using var temp = new TestTempDirectory();
        var installDir = Path.Combine(temp.Path, "install");
        Directory.CreateDirectory(installDir);

        var binaryPath = Path.Combine(installDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(installDir, SidecarFileName), "{\"route\":\"winget\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(installDir, prefix);
    }

    [Fact]
    public void Resolve_SidecarOneDirectoryAbove_ReturnsModeA()
    {
        using var temp = new TestTempDirectory();
        var prefixDir = Path.Combine(temp.Path, "aspire");
        var binDir = Path.Combine(prefixDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(prefixDir, SidecarFileName), "{\"route\":\"script\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeA, mode);
        Assert.Equal(prefixDir, prefix);
    }

    [Fact]
    public void Resolve_NoSidecar_ReturnsUnknownWithBinaryDir()
    {
        // Spec §2.4: when neither the Mode B sibling nor Mode A parent sidecar is present,
        // the resolver returns (Unknown, binaryDir) — not (Unknown, ""). Downstream consumers
        // (PR3) still need a real prefix so they can locate the binary in the no-sidecar
        // fallback (an unmanaged install of the standalone archive on linux, for example).
        using var temp = new TestTempDirectory();
        var binDir = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.Unknown, mode);
        Assert.Equal(Path.GetDirectoryName(binaryPath), prefix);
    }

    [Fact]
    public void Resolve_NoSidecar_FollowsSymlink_ThenReturnsRealBinaryDirAsPrefix()
    {
        // Spec §2.4 + §2.3: even in the no-sidecar fallback, the resolver follows symlinks
        // first and uses the *real* binary's directory as the prefix — not the launcher's.
        // This matches the Mode B / Mode A behavior so callers see a consistent prefix
        // semantic regardless of which branch they end up in.
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var temp = new TestTempDirectory();

        var realInstallDir = Path.Combine(temp.Path, "real-install");
        Directory.CreateDirectory(realInstallDir);
        var realBinaryPath = Path.Combine(realInstallDir, "aspire");
        File.WriteAllText(realBinaryPath, string.Empty);
        // No sidecar anywhere.

        var launcherDir = Path.Combine(temp.Path, "launcher");
        Directory.CreateDirectory(launcherDir);
        var launcherPath = Path.Combine(launcherDir, "aspire");
        File.CreateSymbolicLink(launcherPath, realBinaryPath);

        var (mode, prefix) = new InstallPathResolver().Resolve(launcherPath);

        Assert.Equal(InstallMode.Unknown, mode);
        Assert.Equal(realInstallDir, prefix);
    }

    [Fact]
    public void Resolve_ModeBWinsOverModeA_WhenBothSidecarsExist()
    {
        using var temp = new TestTempDirectory();
        var prefixDir = Path.Combine(temp.Path, "aspire");
        var binDir = Path.Combine(prefixDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(binDir, SidecarFileName), "{\"route\":\"dotnet-tool\"}");
        File.WriteAllText(Path.Combine(prefixDir, SidecarFileName), "{\"route\":\"script\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(binDir, prefix);
    }

    [Fact]
    public void Resolve_FollowsSymlinkBeforeSidecarLookup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var temp = new TestTempDirectory();

        var realInstallDir = Path.Combine(temp.Path, "real-install");
        Directory.CreateDirectory(realInstallDir);
        var realBinaryPath = Path.Combine(realInstallDir, "aspire");
        File.WriteAllText(realBinaryPath, string.Empty);
        File.WriteAllText(Path.Combine(realInstallDir, SidecarFileName), "{\"route\":\"brew\"}");

        var launcherDir = Path.Combine(temp.Path, "launcher");
        Directory.CreateDirectory(launcherDir);
        var launcherPath = Path.Combine(launcherDir, "aspire");
        File.CreateSymbolicLink(launcherPath, realBinaryPath);

        var (mode, prefix) = new InstallPathResolver().Resolve(launcherPath);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(realInstallDir, prefix);
    }

    [Fact]
    public void Resolve_ThrowsForNullOrEmptyBinaryPath()
    {
        var resolver = new InstallPathResolver();

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!));
        Assert.Throws<ArgumentException>(() => resolver.Resolve(string.Empty));
    }

    // Multi-hop symlink chain (link1 → link2 → real-binary). The resolver uses
    // File.ResolveLinkTarget(returnFinalTarget: true), which must walk the entire chain to
    // the real file, then locate the sidecar relative to the real binary's directory — not
    // any of the intermediate launcher directories.
    [Fact]
    public void Resolve_FollowsMultiHopSymlinkChain_ToRealBinaryDir()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Multi-hop symlink test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var temp = new TestTempDirectory();

        var realInstallDir = Path.Combine(temp.Path, "real-install");
        Directory.CreateDirectory(realInstallDir);
        var realBinaryPath = Path.Combine(realInstallDir, "aspire");
        File.WriteAllText(realBinaryPath, string.Empty);
        File.WriteAllText(Path.Combine(realInstallDir, SidecarFileName), "{\"route\":\"brew\"}");

        var hop2Dir = Path.Combine(temp.Path, "hop2");
        Directory.CreateDirectory(hop2Dir);
        var hop2Path = Path.Combine(hop2Dir, "aspire");
        File.CreateSymbolicLink(hop2Path, realBinaryPath);

        var hop1Dir = Path.Combine(temp.Path, "hop1");
        Directory.CreateDirectory(hop1Dir);
        var hop1Path = Path.Combine(hop1Dir, "aspire");
        File.CreateSymbolicLink(hop1Path, hop2Path);

        var (mode, prefix) = new InstallPathResolver().Resolve(hop1Path);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(realInstallDir, prefix);
    }

    // Windows filenames are case-insensitive. The resolver must locate a
    // sidecar regardless of casing on disk so a manually-renamed sidecar (or one written by
    // a tool that uppercased the extension) still resolves the install route.
    [Fact]
    public void Resolve_WindowsCasing_FindsSidecar_RegardlessOfCase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Case-insensitive filesystem behavior is only reliable on Windows (NTFS default).");

        using var temp = new TestTempDirectory();
        var installDir = Path.Combine(temp.Path, "install");
        Directory.CreateDirectory(installDir);

        var binaryPath = Path.Combine(installDir, "aspire.exe");
        File.WriteAllText(binaryPath, string.Empty);
        // Write the sidecar with non-canonical casing — File.Exists comparison on Windows is
        // case-insensitive, so the resolver must still find it.
        File.WriteAllText(Path.Combine(installDir, ".Aspire-Install.JSON"), "{\"route\":\"winget\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(installDir, prefix);
    }

    // Paths containing spaces and Unicode characters must round-trip through the
    // resolver. Cross-platform — every supported OS supports Unicode filenames; spaces are
    // already common on Windows ("Program Files") and acceptable on POSIX.
    [Fact]
    public void Resolve_PathWithSpacesAndUnicode_FindsSidecar()
    {
        using var temp = new TestTempDirectory();
        var installDir = Path.Combine(temp.Path, "with spaces", "aspire-tëst-Ω");
        Directory.CreateDirectory(installDir);

        var binaryPath = Path.Combine(installDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(installDir, SidecarFileName), "{\"route\":\"dotnet-tool\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeB, mode);
        Assert.Equal(installDir, prefix);
    }

    // Spaces+Unicode under Mode A layout (sidecar one dir above
    // binary). Verifies the parent-dir traversal is path-encoding agnostic.
    [Fact]
    public void Resolve_ModeA_PathWithSpacesAndUnicode_FindsSidecar()
    {
        using var temp = new TestTempDirectory();
        var prefixDir = Path.Combine(temp.Path, "with spaces", "aspire-tëst-Ω");
        var binDir = Path.Combine(prefixDir, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(Path.Combine(prefixDir, SidecarFileName), "{\"route\":\"script\"}");

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.ModeA, mode);
        Assert.Equal(prefixDir, prefix);
    }

    private static string ExeName(string baseName)
        => OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}
