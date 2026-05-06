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
    public void Resolve_NoSidecar_ReturnsUnknown()
    {
        using var temp = new TestTempDirectory();
        var binDir = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(binDir);

        var binaryPath = Path.Combine(binDir, ExeName("aspire"));
        File.WriteAllText(binaryPath, string.Empty);

        var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);

        Assert.Equal(InstallMode.Unknown, mode);
        Assert.Equal(string.Empty, prefix);
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

    private static string ExeName(string baseName)
        => OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}
