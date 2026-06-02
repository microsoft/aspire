// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Tests the startup-time invocation contract for <see cref="WingetSidecarBackfill"/>.
/// </summary>
/// <remarks>
/// <para>
/// The back-fill is fired exactly once from <see cref="Program.TryEnsureWingetSidecar(IServiceProvider)"/>,
/// which is itself called from the <c>CliExecutionContext</c> DI singleton factory in
/// <c>Program.BuildApplicationAsync</c>. The "factory invokes this helper" claim is a one-line
/// code-review check at the factory body; the helper's own contract (resolves the back-fill from
/// DI, derives the binary directory from the supplied process path, swallows failures, and threads
/// the same process path through to the registry probe) is what is pinned here.
/// </para>
/// <para>
/// Tests use the two-arg test-seam overload (<see cref="Program.TryEnsureWingetSidecar(IServiceProvider, string?)"/>)
/// with a fake binary under a per-test temp directory so the sidecar write never touches the
/// testhost binary directory. That keeps these tests race-free against any other test in the
/// suite that resolves <c>CliExecutionContext</c> via <c>Program.BuildApplicationAsync</c> on
/// Windows (e.g. <c>CliBootstrapTests</c>), which would otherwise pre-populate the testhost
/// binary directory's sidecar via the production singleton factory.
/// </para>
/// </remarks>
public class WingetSidecarBackfillStartupTests
{
    [Fact]
    public void TryEnsureWingetSidecar_InvokesBackfillAgainstSuppliedProcessPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WingetSidecarBackfill is a Windows-only back-fill; off Windows it early-returns before resolving DI.");

        using var workspace = new TestTempDirectory();
        var fakeBinaryPath = Path.Combine(workspace.Path, "aspire.exe");

        var registry = new CapturingWindowsRegistryReader();
        using var provider = BuildMinimalProvider(registry);

        Program.TryEnsureWingetSidecar(provider, fakeBinaryPath);

        // The back-fill reached the registry-check arm exactly once, which means it
        // resolved WingetSidecarBackfill from DI, derived the binary directory from
        // the supplied process path, found no pre-existing sidecar, and queried our
        // capturing reader for ARP membership using the same process path. Returning
        // false from the reader means the back-fill wrote the negative-result
        // sentinel (`{"backfilled":true}`) into the temp workspace; TestTempDirectory
        // cleans it up on dispose so nothing leaks into the testhost binary directory.
        Assert.Equal(1, registry.CallCount);
        Assert.Equal(fakeBinaryPath, registry.LastProcessPath);

        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at '{sidecarPath}' after back-fill.");
    }

    [Fact]
    public void TryEnsureWingetSidecar_SwallowsRegistryReaderException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WingetSidecarBackfill is a Windows-only back-fill; off Windows it early-returns before consulting the registry reader, so a throwing reader is unreachable.");

        using var workspace = new TestTempDirectory();
        var fakeBinaryPath = Path.Combine(workspace.Path, "aspire.exe");

        using var provider = BuildMinimalProvider(new ThrowingWindowsRegistryReader());

        // No exception must escape: a registry probe failure during CLI startup
        // is logged and treated as "not a winget install for this run", not a
        // fatal error that prevents the CLI from launching.
        var ex = Record.Exception(() => Program.TryEnsureWingetSidecar(provider, fakeBinaryPath));
        Assert.Null(ex);
    }

    private static ServiceProvider BuildMinimalProvider(IWindowsRegistryReader registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        // WingetSidecarBackfill's constructor takes ILogger<WingetSidecarBackfill>, so the
        // registration must be against the interface — registering the concrete
        // NullLogger<T> alone would leave DI unable to resolve the ctor parameter,
        // and the outer try/catch in TryEnsureWingetSidecar would silently swallow
        // the resolution failure instead of reaching the registry probe.
        services.AddSingleton<ILogger<WingetSidecarBackfill>>(NullLogger<WingetSidecarBackfill>.Instance);
        services.AddSingleton<WingetSidecarBackfill>();
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Captures every <see cref="HasWingetAspireUninstallEntry"/> call and always
/// reports the WinGet Add/Remove Programs entry is missing. Returning
/// <see langword="false"/> drives the back-fill down the negative-result arm,
/// which writes the <c>{"backfilled":true}</c> sentinel to the supplied
/// binary directory.
/// </summary>
file sealed class CapturingWindowsRegistryReader : IWindowsRegistryReader
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    public string? LastProcessPath { get; private set; }

    public bool HasWingetAspireUninstallEntry(string processPath)
    {
        Interlocked.Increment(ref _callCount);
        LastProcessPath = processPath;
        return false;
    }
}

file sealed class ThrowingWindowsRegistryReader : IWindowsRegistryReader
{
    public bool HasWingetAspireUninstallEntry(string processPath)
        => throw new InvalidOperationException("Simulated registry probe failure for test.");
}
