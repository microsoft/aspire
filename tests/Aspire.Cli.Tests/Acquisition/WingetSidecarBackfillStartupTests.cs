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
/// code-review check at the factory body; the helper's own contract (resolves the back-fill, derives
/// the binary directory from <see cref="Environment.ProcessPath"/>, swallows failures) is what is
/// pinned here.
/// </para>
/// <para>
/// The back-fill early-returns when the sidecar already exists at the target binary directory.
/// In a normal test run the test host binary directory (the dotnet install root) carries no
/// sidecar, but a stray sidecar would silently turn these tests into no-ops. Each test checks
/// that precondition explicitly and skips with a diagnostic message otherwise.
/// </para>
/// </remarks>
public class WingetSidecarBackfillStartupTests
{
    [Fact]
    public void TryEnsureWingetSidecar_InvokesBackfillAgainstProcessBinaryDirectory()
    {
        var sidecarPath = AssertBackfillReachableFromTestRunner();

        var registry = new CapturingWindowsRegistryReader();
        using var provider = BuildMinimalProvider(registry);

        try
        {
            Program.TryEnsureWingetSidecar(provider);

            // The back-fill reached the registry-check arm exactly once, which means it
            // resolved WingetSidecarBackfill from DI, derived the binary directory from
            // Environment.ProcessPath, found no pre-existing sidecar, and queried our
            // capturing reader for ARP membership. Returning false from the reader
            // means the back-fill wrote the negative-result sentinel
            // (`{"backfilled":true}`) near the test runner's binary; the finally block
            // below deletes it so subsequent test runs aren't short-circuited by the
            // existing-sidecar fast path.
            Assert.Equal(1, registry.CallCount);
            Assert.Equal(Environment.ProcessPath, registry.LastProcessPath);
        }
        finally
        {
            TryDeleteBackfilledSentinel(sidecarPath);
        }
    }

    [Fact]
    public void TryEnsureWingetSidecar_SwallowsRegistryReaderException()
    {
        var sidecarPath = AssertBackfillReachableFromTestRunner();

        using var provider = BuildMinimalProvider(new ThrowingWindowsRegistryReader());

        try
        {
            // No exception must escape: a registry probe failure during CLI startup
            // is logged and treated as "not a winget install for this run", not a
            // fatal error that prevents the CLI from launching.
            var ex = Record.Exception(() => Program.TryEnsureWingetSidecar(provider));
            Assert.Null(ex);
        }
        finally
        {
            // The throwing reader runs before any write attempt, so in normal
            // execution no sentinel is written here — but a previous test
            // failure or an interleaved run might have left one. Clean up
            // defensively so the next test reaches the registry reader.
            TryDeleteBackfilledSentinel(sidecarPath);
        }
    }

    /// <summary>
    /// Skips the test if <see cref="Environment.ProcessPath"/> is unavailable or
    /// if a stray <c>.aspire-install.json</c> exists at the test host's binary
    /// directory. Either condition would make the back-fill early-return before
    /// reaching the registry reader, which would silently invalidate the assertion.
    /// Returns the resolved sidecar path so the caller can clean it up after the run.
    /// </summary>
    private static string AssertBackfillReachableFromTestRunner()
    {
        var processPath = Environment.ProcessPath;
        Assert.SkipUnless(
            !string.IsNullOrEmpty(processPath),
            "Environment.ProcessPath is unavailable in this test host; the startup back-fill cannot run.");

        var binaryDir = Path.GetDirectoryName(processPath);
        Assert.SkipUnless(
            !string.IsNullOrEmpty(binaryDir),
            $"Could not derive a binary directory from Environment.ProcessPath='{processPath}'.");

        var sidecarPath = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        Assert.SkipUnless(
            !File.Exists(sidecarPath),
            $"Test host binary directory '{binaryDir}' already contains '{InstallSidecarReader.SidecarFileName}'; the back-fill would early-return before consulting the registry. " +
            "Remove the stray sidecar or run the tests from a different host to exercise this scenario.");

        return sidecarPath;
    }

    /// <summary>
    /// Deletes the negative-result sentinel that <see cref="WingetSidecarBackfill.EnsureSidecar"/>
    /// writes next to the test runner binary when <see cref="IWindowsRegistryReader.HasWingetAspireUninstallEntry"/>
    /// returns <see langword="false"/>. Without this, the sentinel persists in the build
    /// output and the back-fill's fast path short-circuits every subsequent run of these
    /// tests. Failures are tolerated so test cleanup never masks the test's real failure.
    /// </summary>
    private static void TryDeleteBackfilledSentinel(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            // Only delete what we wrote. A pre-existing sidecar (e.g. a real winget
            // install) is filtered out by AssertBackfillReachableFromTestRunner's
            // File.Exists skip, so anything we find here is a sentinel we wrote
            // ourselves — but check the content anyway in case a parallel test
            // wrote a different payload.
            var content = File.ReadAllText(sidecarPath);
            if (content.Contains("\"backfilled\":true", StringComparison.Ordinal))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
