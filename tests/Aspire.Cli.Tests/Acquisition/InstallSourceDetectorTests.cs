// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

public class InstallSourceDetectorTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("script")]
    [InlineData("pr")]
    [InlineData("winget")]
    [InlineData("brew")]
    [InlineData("dotnet-tool")]
    [InlineData("localhive")]
    [InlineData("nix")]
    public void Detect_PrefersKnownSidecarToOtherSignals(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var binaryDirectory = workspace.CreateDirectory(Path.Combine("mise", "installs", "aspire", "13.5.0"));
        File.WriteAllText(Path.Combine(binaryDirectory.FullName, InstallSidecarReader.SidecarFileName), $$"""{"source":"{{source}}"}""");
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName
        });
        var detector = CreateDetector(Path.Combine(binaryDirectory.FullName, "aspire.exe"), environment, new TestWindowsRegistryReader(true));

        Assert.Equal(source, detector.Detect());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{bad-json")]
    [InlineData("""{"source":"future-route-with-private-data"}""")]
    [InlineData("""{"source":42}""")]
    [InlineData("""{"source":""}""")]
    public void Detect_ReturnsUnknownWithoutRecognizedProvenance(string? sidecar)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        if (sidecar is not null)
        {
            File.WriteAllText(Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName), sidecar);
        }
        var detector = CreateDetector(Path.Combine(workspace.Path, "aspire"), TestEnvironment.CreateLinux());

        Assert.Equal("unknown", detector.Detect());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aspire")]
    [InlineData("invalid\0path")]
    public void Detect_ReturnsUnknownForUnavailableProcessPath(string? processPath)
    {
        var detector = CreateDetector(processPath, TestEnvironment.CreateLinux());

        Assert.Equal("unknown", detector.Detect());
    }

    [Fact]
    public void Detect_DoesNotAttributeManagedHostFromInheritedNpmMarker()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName
        });
        var detector = CreateDetector(Path.Combine(workspace.Path, "dotnet"), environment);

        Assert.Equal("unknown", detector.Detect());
    }

    [Theory]
    [InlineData("@microsoft/aspire-cli", "npm")]
    [InlineData("@Microsoft/Aspire-Cli", "unknown")]
    [InlineData("aspire-cli", "unknown")]
    [InlineData("", "unknown")]
    public void Detect_RecognizesOnlyCanonicalNpmPackage(string packageName, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = packageName
        });
        var detector = CreateDetector(Path.Combine(workspace.Path, "aspire"), environment);

        Assert.Equal(expected, detector.Detect());
    }

    [Theory]
    [InlineData(true, "winget")]
    [InlineData(false, "unknown")]
    public void Detect_ProbesWingetWithoutWritingSidecar(bool registryClaim, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processPath = Path.Combine(workspace.Path, "aspire.exe");
        var registry = new TestWindowsRegistryReader
        {
            ProbeCallback = path =>
            {
                Assert.Equal(processPath, path);
                return registryClaim;
            }
        };
        var detector = CreateDetector(processPath, TestEnvironment.CreateWindows(), registry);

        Assert.Equal(expected, detector.Detect());
        Assert.Empty(Directory.GetFiles(workspace.Path));
    }

    [Fact]
    public void Detect_DoesNotProbeRegistryOnUnix()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var registry = new TestWindowsRegistryReader
        {
            ProbeCallback = _ => throw new InvalidOperationException("The registry must not be read on Unix.")
        };
        var detector = CreateDetector(Path.Combine(workspace.Path, "aspire"), TestEnvironment.CreateLinux(), registry);

        Assert.Equal("unknown", detector.Detect());
    }

    [Fact]
    public void Detect_ReturnsUnknownWhenProbeCannotReadInstallation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var registry = new TestWindowsRegistryReader
        {
            ProbeCallback = _ => throw new UnauthorizedAccessException()
        };
        var detector = CreateDetector(Path.Combine(workspace.Path, "aspire.exe"), TestEnvironment.CreateWindows(), registry);

        Assert.Equal("unknown", detector.Detect());
    }

    [Theory]
    [InlineData(".local/share/mise/installs/aspire/13.5.0/aspire", "mise")]
    [InlineData("AppData/Local/mise/installs/aspire/13.5.0/bin/aspire", "mise")]
    [InlineData("usr/local/share/mise/installs/aspire/13.5.0/aspire", "mise")]
    [InlineData("mise/installs/aspire/latest/aspire", "mise")]
    [InlineData("mise/installs/github-microsoft-aspire/13.5.0/aspire", "mise")]
    [InlineData("mise/installs/github-microsoft-aspire/13.5.0/bin/aspire", "mise")]
    [InlineData("mise/installs/aspire/13.5.0/tools/aspire", "unknown")]
    [InlineData("mise/installs/another-tool/13.5.0/aspire", "unknown")]
    [InlineData("mise/installs/aspire/aspire", "unknown")]
    [InlineData("mise/installs/aspire-extra/13.5.0/aspire", "unknown")]
    [InlineData("mise/installs/github-other-aspire/13.5.0/aspire", "unknown")]
    [InlineData("not-mise/installs/aspire/13.5.0/aspire", "unknown")]
    [InlineData("mise/downloads/aspire/13.5.0/aspire", "unknown")]
    [InlineData("mise/shims/aspire", "unknown")]
    public void Detect_MatchesMiseInstallationBoundaries(string relativePath, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processPath = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var detector = CreateDetector(processPath, TestEnvironment.CreateLinux());

        Assert.Equal(expected, detector.Detect());
    }

    [Theory]
    [InlineData("MISE_INSTALLS_DIR", "custom", "custom/aspire/13.5.0/aspire", "mise")]
    [InlineData("MISE_SYSTEM_INSTALLS_DIR", "custom", "custom/aspire/13.5.0/aspire", "mise")]
    [InlineData("MISE_DATA_DIR", "custom", "custom/installs/aspire/13.5.0/aspire", "mise")]
    [InlineData("MISE_INSTALLS_DIR", "custom", "custom/github-microsoft-aspire/13.5.0/aspire", "mise")]
    [InlineData("MISE_DATA_DIR", "custom", "custom/downloads/aspire/13.5.0/aspire", "unknown")]
    [InlineData("MISE_INSTALLS_DIR", "custom", "custom-extra/aspire/13.5.0/aspire", "unknown")]
    [InlineData("MISE_DATA_DIR", "custom", "unrelated/aspire", "unknown")]
    [InlineData("MISE_INSTALLS_DIR", "custom", "custom/other/13.5.0/aspire", "unknown")]
    public void Detect_RequiresMatchingPathForMiseOverrides(string variable, string root, string relativePath, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [variable] = Path.Combine(workspace.Path, root) + Path.DirectorySeparatorChar
        });
        var processPath = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var detector = CreateDetector(processPath, environment);

        Assert.Equal(expected, detector.Detect());
    }

    [Theory]
    [InlineData("MISE_INSTALLS_DIR", true)]
    [InlineData("MISE_INSTALLS_DIR", false)]
    [InlineData("MISE_SYSTEM_INSTALLS_DIR", true)]
    [InlineData("MISE_SYSTEM_INSTALLS_DIR", false)]
    [InlineData("MISE_DATA_DIR", true)]
    [InlineData("MISE_DATA_DIR", false)]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.Linux, "Firmlink normalization only applies on macOS.")]
    public void Detect_NormalizesMacOSMiseRootAliases(string variable, bool configuredRootUsesPrivatePrefix)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = Path.Combine("/tmp", workspace.WorkspaceRoot.Name, "custom");
        var configuredRoot = configuredRootUsesPrivatePrefix ? "/private" + root : root;
        var processRoot = configuredRootUsesPrivatePrefix ? root : "/private" + root;
        var installsRoot = variable == "MISE_DATA_DIR" ? Path.Combine(processRoot, "installs") : processRoot;
        var environment = TestEnvironment.CreateMacOS(new Dictionary<string, string?>
        {
            [variable] = configuredRoot + Path.DirectorySeparatorChar
        });
        var detector = CreateDetector(Path.Combine(installsRoot, "aspire", "13.5.0", "aspire"), environment);

        Assert.Equal("mise", detector.Detect());
    }

    [Theory]
    [InlineData(true, "mise")]
    [InlineData(false, "unknown")]
    public void Detect_UsesPlatformPathComparisonForMise(bool windows, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = windows ? TestEnvironment.CreateWindows() : TestEnvironment.CreateLinux();
        var processPath = Path.Combine(workspace.Path, "MISE", "INSTALLS", "ASPIRE", "13.5.0", windows ? "ASPIRE.EXE" : "aspire");
        var detector = CreateDetector(processPath, environment);

        Assert.Equal(expected, detector.Detect());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{bad-json")]
    [InlineData("""{"source":"unrecognized"}""")]
    public void Detect_UsesNpmMarkerBeforeMisePathWhenSidecarDoesNotIdentifySource(string? sidecar)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.CreateDirectory(Path.Combine("mise", "installs", "aspire", "13.5.0"));
        if (sidecar is not null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, InstallSidecarReader.SidecarFileName), sidecar);
        }
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName
        });
        var detector = CreateDetector(Path.Combine(directory.FullName, "aspire"), environment);

        Assert.Equal("npm", detector.Detect());
    }

    [Theory]
    [InlineData(".dotnet/tools/aspire")]
    [InlineData("custom/.store/aspire.cli/13.5.0/aspire.cli.linux-x64/13.5.0/tools/any/linux-x64/aspire")]
    public void Detect_RecognizesDotNetToolsWithoutSidecar(string relativePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processPath = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var detector = CreateDetector(processPath, TestEnvironment.CreateLinux());

        Assert.Equal("dotnet-tool", detector.Detect());
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Symlink creation requires additional privileges on Windows.")]
    public void Detect_ReadsSidecarBesideResolvedExecutable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var installDirectory = workspace.CreateDirectory("installed");
        var processPath = Path.Combine(installDirectory.FullName, "aspire");
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(Path.Combine(installDirectory.FullName, InstallSidecarReader.SidecarFileName), """{"source":"brew"}""");
        var linkPath = Path.Combine(workspace.Path, "aspire");
        File.CreateSymbolicLink(linkPath, processPath);
        var detector = CreateDetector(linkPath, TestEnvironment.CreateLinux());

        Assert.Equal("brew", detector.Detect());
    }

    private InstallSourceDetector CreateDetector(string? processPath, IEnvironment environment, IWindowsRegistryReader? registry = null)
    {
        return new InstallSourceDetector(
            new TestProcessPathProvider(processPath),
            CliTestHelper.CreateSidecarReader(outputHelper),
            registry ?? new TestWindowsRegistryReader(),
            environment,
            NullLogger<InstallSourceDetector>.Instance);
    }
}
