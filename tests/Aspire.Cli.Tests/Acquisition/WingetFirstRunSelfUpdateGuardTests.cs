// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Regression guard for the WinGet first-run sidecar bypass. WinGet sidecars
/// are stamped lazily by <see cref="WingetFirstRunProbe"/> from
/// <see cref="Aspire.Cli.Bundles.BundleService"/>'s extract path. If a fresh
/// WinGet install's very first command is <c>aspire update --self</c>, the
/// bundle path never runs, no sidecar exists, and a naive resolver would
/// classify the route as <see cref="InstallSource.Unknown"/> — which routes
/// to in-process update and silently overwrites the WinGet-owned binary.
/// PR α adds a <see cref="WingetFirstRunProbe.Run"/> call inside
/// <c>UpdateCommand.ResolveRunningInstall</c> (and
/// <c>CliUpdateNotifier.GetRouteAwareUpdateCommand</c>) so the sidecar is
/// stamped before the route decision is read.
/// </summary>
public class WingetFirstRunSelfUpdateGuardTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Probe_WingetClassifiedBinary_StampsSidecarAtomically()
    {
        using var tempDir = new TempDirectory();
        var binaryName = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
        File.WriteAllText(Path.Combine(tempDir.Path, binaryName), string.Empty);

        var fakeRegistry = new FakeWingetRegistryReader { ShouldClassifyAsWingetPortable = true };
        var probe = new WingetFirstRunProbe(fakeRegistry, NullLogger<WingetFirstRunProbe>.Instance);

        Assert.False(File.Exists(Path.Combine(tempDir.Path, ".aspire-install.json")));

        probe.Run(tempDir.Path);

        var info = new InstallSidecarReader().TryRead(tempDir.Path);
        Assert.NotNull(info);
        Assert.Equal(InstallSource.Winget, info!.Source);
    }

    [Fact]
    public void Probe_NotAWingetInstall_DoesNotWriteSidecar()
    {
        using var tempDir = new TempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "aspire"), string.Empty);

        var fakeRegistry = new FakeWingetRegistryReader { ShouldClassifyAsWingetPortable = false };
        var probe = new WingetFirstRunProbe(fakeRegistry, NullLogger<WingetFirstRunProbe>.Instance);

        probe.Run(tempDir.Path);

        Assert.False(
            File.Exists(Path.Combine(tempDir.Path, ".aspire-install.json")),
            "Probe must NOT stamp a sidecar when the registry says this is not a WinGet install.");
    }

    /// <summary>
    /// End-to-end integration: drives <c>aspire update --self</c> against a
    /// controllable temp binary directory. Initially the sidecar is absent
    /// (mirrors a fresh WinGet install state); the fake registry classifies
    /// the running binary as a WinGet portable install. After the SUT runs:
    /// (a) the sidecar must exist on disk (proves the probe was invoked
    /// before the route decision), and (b) the printed refusal must contain
    /// the WinGet update command (proves the route was re-resolved post-probe
    /// and the gating took the WinGet branch instead of falling through to
    /// in-process update).
    /// </summary>
    [Fact]
    public async Task SelfUpdate_FirstRunWingetInstall_StampsSidecarAndRefusesWithWingetCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Controllable binary location — discovery surfaces this as
        // CanonicalPath, the production code derives binaryDir from it,
        // and the probe writes the sidecar here.
        using var binaryHome = new TempDirectory();
        var binaryName = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
        var binaryPath = Path.Combine(binaryHome.Path, binaryName);
        File.WriteAllText(binaryPath, string.Empty);
        var sidecarPath = Path.Combine(binaryHome.Path, ".aspire-install.json");
        Assert.False(File.Exists(sidecarPath), "Pre-condition: no sidecar yet (first-run state).");

        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };
        });

        services.AddSingleton<IWindowsRegistryReader>(new FakeWingetRegistryReader { ShouldClassifyAsWingetPortable = true });
        services.AddSingleton(sp => new WingetFirstRunProbe(
            sp.GetRequiredService<IWindowsRegistryReader>(),
            NullLogger<WingetFirstRunProbe>.Instance));
        // SidecarBackedDiscovery re-reads the sidecar on every DescribeSelf
        // call — so once the probe stamps it, the second describe sees Winget.
        services.AddSingleton<IInstallationDiscovery>(_ => new SidecarBackedDiscovery(binaryPath));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("update --self").InvokeAsync().DefaultTimeout();

        Assert.True(File.Exists(sidecarPath),
            "WingetFirstRunProbe must have stamped the sidecar during ResolveRunningInstall.");
        var stamped = new InstallSidecarReader().TryRead(binaryHome.Path);
        Assert.NotNull(stamped);
        Assert.Equal(InstallSource.Winget, stamped!.Source);

        Assert.Equal(0, exitCode);
        Assert.NotNull(interactionService);
        Assert.Contains(
            interactionService!.DisplayedPlainText,
            line => line.Contains("winget upgrade Microsoft.Aspire", StringComparison.Ordinal));
    }

    private sealed class FakeWingetRegistryReader : IWindowsRegistryReader
    {
        public bool ShouldClassifyAsWingetPortable { get; set; }

        public bool HasWingetAspireUninstallEntry(string processPath) => ShouldClassifyAsWingetPortable;
    }

    private sealed class SpyWingetRegistryReader : IWindowsRegistryReader
    {
        public int QueryCount { get; private set; }

        public bool HasWingetAspireUninstallEntry(string processPath)
        {
            QueryCount++;
            return false;
        }
    }

    private sealed class SidecarBackedDiscovery : IInstallationDiscovery
    {
        private readonly string _binaryPath;
        private readonly InstallSidecarReader _reader = new();

        public SidecarBackedDiscovery(string binaryPath)
        {
            _binaryPath = binaryPath;
        }

        public InstallationInfo DescribeSelf()
        {
            var binaryDir = Path.GetDirectoryName(_binaryPath)!;
            var sidecar = _reader.TryRead(binaryDir);
            return new InstallationInfo
            {
                Path = _binaryPath,
                CanonicalPath = _binaryPath,
                Route = sidecar?.Source.ToWireString() ?? sidecar?.RawSource,
                Status = InstallationInfoStatus.Ok,
            };
        }

        public Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstallationInfo>>([DescribeSelf()]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("aspire-winget-firstrun-").FullName;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

