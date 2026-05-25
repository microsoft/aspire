// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Tests the startup-time invocation contract for <see cref="WingetFirstRunProbe"/>.
/// </summary>
/// <remarks>
/// <para>
/// The probe is fired exactly once from <see cref="Program.TryRunWingetFirstRunProbe(IServiceProvider)"/>,
/// which is itself called from the <c>CliExecutionContext</c> DI singleton factory in
/// <c>Program.BuildApplicationAsync</c>. The "factory invokes this helper" claim is a one-line
/// code-review check at the factory body; the helper's own contract (resolves the probe, derives
/// the binary directory from <see cref="Environment.ProcessPath"/>, swallows failures) is what is
/// pinned here.
/// </para>
/// <para>
/// The probe early-returns when the sidecar already exists at the target binary directory.
/// In a normal test run the test host binary directory (the dotnet install root) carries no
/// sidecar, but a stray sidecar would silently turn these tests into no-ops. Each test checks
/// that precondition explicitly and skips with a diagnostic message otherwise.
/// </para>
/// </remarks>
public class WingetStartupProbeTests
{
    [Fact]
    public void TryRunWingetFirstRunProbe_InvokesProbeAgainstProcessBinaryDirectory()
    {
        AssertProbeReachableFromTestRunner();

        var registry = new CapturingWindowsRegistryReader();
        using var provider = BuildMinimalProvider(registry);

        Program.TryRunWingetFirstRunProbe(provider);

        // The probe reached the registry-check arm exactly once, which means it
        // resolved WingetFirstRunProbe from DI, derived the binary directory from
        // Environment.ProcessPath, found no pre-existing sidecar, and queried our
        // capturing reader for ARP membership. Returning false from the reader
        // also means no sidecar was written near the test runner's binary.
        Assert.Equal(1, registry.CallCount);
        Assert.Equal(Environment.ProcessPath, registry.LastProcessPath);
    }

    [Fact]
    public void TryRunWingetFirstRunProbe_SwallowsRegistryReaderException()
    {
        AssertProbeReachableFromTestRunner();

        using var provider = BuildMinimalProvider(new ThrowingWindowsRegistryReader());

        // No exception must escape: a registry probe failure during CLI startup
        // is logged and treated as "not a winget install for this run", not a
        // fatal error that prevents the CLI from launching.
        var ex = Record.Exception(() => Program.TryRunWingetFirstRunProbe(provider));
        Assert.Null(ex);
    }

    /// <summary>
    /// Skips the test if <see cref="Environment.ProcessPath"/> is unavailable or
    /// if a stray <c>.aspire-install.json</c> exists at the test host's binary
    /// directory. Either condition would make the probe early-return before
    /// reaching the registry reader, which would silently invalidate the assertion.
    /// </summary>
    private static void AssertProbeReachableFromTestRunner()
    {
        var processPath = Environment.ProcessPath;
        Assert.SkipUnless(
            !string.IsNullOrEmpty(processPath),
            "Environment.ProcessPath is unavailable in this test host; the startup probe cannot run.");

        var binaryDir = Path.GetDirectoryName(processPath);
        Assert.SkipUnless(
            !string.IsNullOrEmpty(binaryDir),
            $"Could not derive a binary directory from Environment.ProcessPath='{processPath}'.");

        var sidecarPath = Path.Combine(binaryDir, ".aspire-install.json");
        Assert.SkipUnless(
            !File.Exists(sidecarPath),
            $"Test host binary directory '{binaryDir}' already contains '{InstallSidecarReader.SidecarFileName}'; the probe would early-return before consulting the registry. " +
            "Remove the stray sidecar or run the tests from a different host to exercise this scenario.");
    }

    private static ServiceProvider BuildMinimalProvider(IWindowsRegistryReader registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        // WingetFirstRunProbe's constructor takes ILogger<WingetFirstRunProbe>, so the
        // registration must be against the interface — registering the concrete
        // NullLogger<T> alone would leave DI unable to resolve the ctor parameter,
        // and the outer try/catch in TryRunWingetFirstRunProbe would silently swallow
        // the resolution failure instead of reaching the registry probe.
        services.AddSingleton<ILogger<WingetFirstRunProbe>>(NullLogger<WingetFirstRunProbe>.Instance);
        services.AddSingleton<WingetFirstRunProbe>();
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Captures every <see cref="HasWingetAspireUninstallEntry"/> call and always
/// returns <see langword="false"/> so the probe does not write a sidecar near
/// the test host's binary.
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
