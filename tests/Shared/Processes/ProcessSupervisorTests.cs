// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !NET11_0_OR_GREATER
extern alias RemoteHost;
#endif

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

#if NET11_0_OR_GREATER
using TestProcessStartInfoHelper = global::ProcessStartInfoHelper;
#else
using TestProcessStartInfoHelper = RemoteHost::ProcessStartInfoHelper;
#endif

namespace Aspire.Shared.Tests;

[RequiresTools(["dotnet"])]
[Collection(ProcessTestCollection.Name)]
public class ProcessSupervisorTests(ProcessTestFixture fixture)
{
    [Fact]
    public async Task OwnedTree_WindowsBatchShim_PreservesFinalChildArguments()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows batch commands require cmd.exe.");
        var directory = fixture.CreateDirectory().CreateSubdirectory("literal %TEMP%! with spaces");
        var shim = Path.Combine(directory.FullName, "literal %PATH%! shim.cmd");
        await File.WriteAllTextAsync(shim, """
            @echo off
            "%ASPIRE_TEST_DOTNET%" "%ASPIRE_TEST_HOST%" argv %*
            """);
        string[] arguments =
        [
            "literal %PATH%! & value", "", "a^b|c", "(group)>file<other",
            "a \"quoted\" value", "quoted \"& text\" remains literal",
            "backslash\\\"quote", @"C:\tools\trailing\"
        ];
        var startInfo = new ProcessStartInfo { WorkingDirectory = directory.FullName };
        TestProcessStartInfoHelper.SetCommand(startInfo, shim, arguments, isWindows: true);
        startInfo.Environment["ASPIRE_TEST_DOTNET"] = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe");
        startInfo.Environment["ASPIRE_TEST_HOST"] = fixture.AssemblyPath;
        var stdout = new ConcurrentQueue<string>();
        await using var process = ProcessTestFixture.CreateProcess(startInfo, new ChildProcessOptions
        {
            Lifetime = ChildProcessLifetime.OwnedTree,
            CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo,
            StandardOutputCallback = stdout.Enqueue
        });

        await process.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(Assert.Single(stdout)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task CommandExit_PreservesCompletionCodeAndReapsWorkers(int exitCode)
    {
        var directory = fixture.CreateDirectory();
        using var readiness = new ProcessTestReadiness();
        var completionPath = Path.Combine(directory.FullName, "exit-code");
        var sink = new TestSink();
        var stderr = new ConcurrentQueue<string>();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree-exit", readiness.Name, exitCode.ToString(CultureInfo.InvariantCulture)),
            completionPath), new TestLogger("guardian owner", sink, enabled: true),
            new ChildProcessOptions { StandardErrorCallback = stderr.Enqueue });
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        var guardianPid = guardian.ProcessId;
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test completion");
        var identities = await readiness.ReadTreeAsync();

        await scope.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        await scope.DisposeAsync();

        Assert.Equal(exitCode.ToString(CultureInfo.InvariantCulture), await File.ReadAllTextAsync(completionPath));
        var exit = Assert.Single(sink.Writes, entry => entry.Message?.Contains("Supervised command", StringComparison.Ordinal) == true);
        Assert.Equal(exitCode == 0 ? LogLevel.Information : LogLevel.Warning, exit.LogLevel);
        Assert.Equal($"Process supervisor {guardianPid}: Supervised command 'dotnet' exited with code {exitCode}.", exit.Message);
        Assert.Empty(stderr);
        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task Cancellation_ReapsGuardianRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree", readiness.Name)), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test cancellation");
        var guardianIdentity = ProcessTestIdentity.Capture(scope.ProcessId);
        var identities = await readiness.ReadTreeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.WaitForExitAsync(cancellation.Token));
        await scope.DisposeAsync();

        await Task.WhenAll(identities.Append(guardianIdentity).Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task GuardianCrash_ReapsRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree", readiness.Name)), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test guardian crash");
        var identities = await readiness.ReadTreeAsync();
        using var observedGuardian = Process.GetProcessById(scope.ProcessId);

        observedGuardian.Kill(entireProcessTree: false);
        await scope.DisposeAsync();

        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionWithoutScope_ReapsDescendantsAfterGuardianExit(bool cancel)
    {
        using var readiness = new ProcessTestReadiness();
        await using var guardian = ProcessTestFixture.CreateProcess(fixture.CreateSupervisorStartInfo(
            fixture.CreateStartInfo("tree", readiness.Name)), new ChildProcessOptions());
        await guardian.StartAsync(TestContext.Current.CancellationToken);
        var identities = await readiness.ReadTreeAsync();
        if (cancel)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guardian.WaitForExitAsync(cancellation.Token));
        }
        else
        {
            using var observedGuardian = Process.GetProcessById(guardian.ProcessId);
            observedGuardian.Kill(entireProcessTree: false);
        }

        // Execution itself must verify containment. No supervising owner wrapper
        // should be required to reap orphaned workers.
        await guardian.DisposeAsync();
        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
    }

    [Fact]
    public async Task OwnerCrash_GuardianIndependentlyReapsRuntimeAndWorker()
    {
        using var readiness = new ProcessTestReadiness();
        await using var owner = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("owner", readiness.Name), new ChildProcessOptions());
        await owner.StartAsync(TestContext.Current.CancellationToken);
        var guardianIdentity = await readiness.ReadGuardianAsync();
        ProcessTestIdentity[] identities = [];
        try
        {
            identities = await readiness.ReadTreeAsync();
            using var observedOwner = Process.GetProcessById(owner.ProcessId);

            observedOwner.Kill(entireProcessTree: false);
            await owner.WaitForRootExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

            await Task.WhenAll(identities.Append(guardianIdentity).Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            ProcessTestFixture.KillIfRunning(guardianIdentity);
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }
}
