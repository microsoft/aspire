// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class CliAppHostCommandsTests
{
    public static bool SupportsShell => !OperatingSystem.IsWindows();

    [Fact]
    public void StopAlwaysSuppliesExactIdentityAndUsesNoShell()
    {
        var executable = Path.GetFullPath("cli path/aspire");
        var host = Host();
        var startInfo = CliAppHostCommands.CreateStopStartInfo(executable, host);

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(Arguments(host), startInfo.ArgumentList);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.RedirectStandardInput);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StopRejectsInvalidPid(int pid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CliAppHostCommands.CreateStopStartInfo(Path.GetFullPath("aspire"), Host() with { AppHostPid = pid }));
    }

    [Fact]
    public void StopRejectsRelativePaths()
    {
        Assert.Throws<ArgumentException>(() => CliAppHostCommands.CreateStopStartInfo("aspire", Host()));
        Assert.Throws<ArgumentException>(() =>
            CliAppHostCommands.CreateStopStartInfo(Path.GetFullPath("aspire"), Host() with { AppHostPath = "apphost.cs" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void StopNeverFallsBackToPidOnlyIdentity(long? startedAt)
    {
        Assert.Throws<ArgumentException>(() => CliAppHostCommands.CreateStopStartInfo(
            Path.GetFullPath("aspire"), Host() with { ProcessStartTimeUnixMilliseconds = startedAt }));
    }

    [Theory(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    [InlineData(0)]
    [InlineData(17)]
    public async Task StopDrainsBothPipesAndPreservesTypedOutcomeAndExitCode(int exitCode)
    {
        var outcome = exitCode == 0 ? "stopped" : "stop_failed";
        using var cli = new FixtureCli($$"""
            printf '{"version":1,"outcome":"{{outcome}}","exitCode":{{exitCode}},"padding":"'
            i=0
            while [ "$i" -lt 4096 ]; do
                printf '%080d' "$i"
                printf '%080d\n' "$i" >&2
                i=$((i + 1))
            done
            printf '"}\n'
            exit {{exitCode}}
            """);
        var commands = new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(30));
        var host = Host();

        Assert.Equal(new StopResult(exitCode == 0 ? StopOutcome.Stopped : StopOutcome.Failed, exitCode),
            await commands.StopAsync(host, TestContext.Current.CancellationToken));
        Assert.Equal(Arguments(host), cli.ReadArguments());
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task CancellationTerminatesOnlyTheOwnedCliChild()
    {
        using var cli = new FixtureCli("exec /bin/sleep 60");
        using var otherCli = new FixtureCli("exec /bin/sleep 60");
        using var otherProcess = Process.Start(CliProcess.CreateStartInfo(otherCli.Path))
            ?? throw new InvalidOperationException("The other fixture process could not be started.");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var commands = new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(30));
        var task = commands.StopAsync(Host(), shutdown.Token);
        try
        {
            var pid = await cli.WaitForPidAsync(TestContext.Current.CancellationToken);
            using var process = Process.GetProcessById(pid);
            await shutdown.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(process.HasExited);
            Assert.False(otherProcess.HasExited);
        }
        finally
        {
            try
            {
                await shutdown.CancelAsync();
                try
                {
                    await task.ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                }
            }
            finally
            {
                await CliProcess.TerminateOwnedChildAsync(otherProcess);
            }
        }
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task TimeoutReportsUncertainShutdownAndCleansUpTheChild()
    {
        using var cli = new FixtureCli("exec /bin/sleep 60");
        var commands = new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(5));
        var task = commands.StopAsync(Host(), TestContext.Current.CancellationToken);
        var pid = await cli.WaitForPidAsync(TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(pid);

        Assert.Equal(new StopResult(StopOutcome.TimedOut, null), await task.ConfigureAwait(true));
        Assert.True(process.HasExited);
    }

    [Theory(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    [InlineData("exit 0")]
    [InlineData("printf 'Stopped successfully\\n'")]
    [InlineData("printf '{\"version\":2,\"outcome\":\"stopped\",\"exitCode\":0}\\n'")]
    [InlineData("printf '{\"version\":1,\"outcome\":\"stopped\",\"exitCode\":0}\\n'; exit 7")]
    [InlineData("printf '{\"version\":1,\"outcome\":\"stopped\",\"exitCode\":0}\\n{\"version\":1,\"outcome\":\"stopped\",\"exitCode\":0}\\n'")]
    public async Task InvalidOutputIsNotSuccessfulEvenWhenTheProcessExitsZero(string script)
    {
        using var cli = new FixtureCli(script);
        var result = await new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(30))
            .StopAsync(Host(), TestContext.Current.CancellationToken);
        Assert.Equal(StopOutcome.Incompatible, result.Outcome);
    }

    [Fact(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    public async Task SuccessPayloadStillRequiresTheCliProcessToExit()
    {
        using var cli = new FixtureCli("""
            printf '{"version":1,"outcome":"stopped","exitCode":0}\n'
            exec 1>&-
            exec /bin/sleep 60
            """);
        var commands = new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(2));
        var task = commands.StopAsync(Host(), TestContext.Current.CancellationToken);
        using var process = Process.GetProcessById(await cli.WaitForPidAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new StopResult(StopOutcome.TimedOut, null), await task.ConfigureAwait(true));
        Assert.True(process.HasExited);
    }

    [Theory(Skip = "The fixture requires /bin/sh.", SkipUnless = nameof(SupportsShell))]
    [InlineData("not_found", "NotFound")]
    [InlineData("ambiguous", "Ambiguous")]
    [InlineData("identity_mismatch", "IdentityMismatch")]
    [InlineData("identity_unavailable", "IdentityUnavailable")]
    public async Task StructuredFailuresDoNotRequireParsingDiagnosticText(string outcome, string expected)
    {
        using var cli = new FixtureCli($$"""
            printf 'Localized diagnostic text is not the contract\n' >&2
            printf '{"version":1,"outcome":"{{outcome}}","exitCode":7}\n'
            exit 7
            """);
        var result = await new CliAppHostCommands(cli.Path, TimeSpan.FromSeconds(30))
            .StopAsync(Host(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task CanceledRequestDoesNotStartTheCli()
    {
        var commands = new CliAppHostCommands(Path.GetFullPath("does-not-exist"), TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.StopAsync(Host(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void StopRequiresAFinitePositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CliAppHostCommands(Path.GetFullPath("aspire"), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CliAppHostCommands(Path.GetFullPath("aspire"), Timeout.InfiniteTimeSpan));
    }

    private static AppHostId Host() => new(Path.GetFullPath("project with spaces & apostrophe's/apphost.cs"), 42, 1000);

    private static string[] Arguments(AppHostId host) =>
        ["stop", "--apphost", host.AppHostPath, "--pid", "42", "--started-at", "1000",
        "--format", "json", "--protocol-version", "1", "--non-interactive", "--nologo"];
}
