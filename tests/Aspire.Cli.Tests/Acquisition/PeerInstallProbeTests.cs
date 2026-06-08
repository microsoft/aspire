// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="PeerInstallProbe"/>. These tests spawn
/// a real child process — a tiny test helper binary built into the test
/// project — to exercise the timeout / stdout-cap / kill paths against
/// real process semantics.
/// </summary>
public class PeerInstallProbeTests(ITestOutputHelper outputHelper) : IDisposable
{
    // Route internal probe diagnostics (LogDebug for "JSON without an
    // installation row", "invalid JSON", etc.) into the xunit test output
    // so a failure log tells us why the probe took whichever code path it
    // took. Keep the factory alive for the lifetime of the test class so
    // logs aren't cut off mid-probe by an early dispose.
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper, LogLevel.Trace));

    public void Dispose() => _loggerFactory.Dispose();

    private ILogger<PeerInstallProbe> ProbeLogger => _loggerFactory.CreateLogger<PeerInstallProbe>();

    // Surface the actual Failed.Reason on Ok-expected assertions. Without
    // this helper, Assert.IsType<Ok>(result) discards the (often
    // multi-line) failure reason and reports only "expected Ok, got
    // Failed" — useless for diagnosing CI-only failures.
    private static PeerProbeResult.Ok AssertProbeOk(PeerProbeResult result)
    {
        if (result is PeerProbeResult.Failed failed)
        {
            Assert.Fail($"Expected PeerProbeResult.Ok, got PeerProbeResult.Failed. Reason:{Environment.NewLine}{failed.Reason}");
        }

        return Assert.IsType<PeerProbeResult.Ok>(result);
    }

    // Construct a probe with a much wider timeout than production's 5s default.
    //
    // These positive-path tests assert how the probe interprets a successful
    // peer's output, not the timeout behavior — but the FakePeerScript helper
    // on Windows shells out to cmd.exe (and powershell.exe in the stderr
    // variant), which under heavy CI load (saturated CPU, slow disk) can take
    // several seconds just to start. With the production 5s timeout we
    // intermittently see the probe synthesize
    // `Failed: "Peer probe timed out after 5.0s."` before the fake peer even
    // produces stdout, even though the peer would complete instantly given a
    // bit more wallclock.
    //
    // The timeout path itself is covered by ProbeAsync_PeerHangs_TimesOutAndReturnsFailed
    // and ProbeAsync_CallerCancels_KillsSpawnedProcess, so widening the
    // budget here removes the CI flake without losing coverage of the 5s
    // production behavior.
    private PeerInstallProbe CreateProbeWithGenerousTimeout()
        => new(TimeSpan.FromSeconds(30), ProbeLogger);

    [Fact]
    public async Task ProbeAsync_BinaryNotFound_ReturnsFailed()
    {
        var probe = CreateProbeWithGenerousTimeout();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missing = Path.Combine(workspace.WorkspaceRoot.FullName, "does-not-exist");

        var result = await probe.ProbeAsync(missing, TestContext.Current.CancellationToken);

        Assert.IsType<PeerProbeResult.Failed>(result);
    }

    [Fact]
    public async Task ProbeAsync_InvokesPeerWithDoctorSelfFormatJson()
    {
        // The peer must be asked to describe ONLY itself. Without --self,
        // `aspire doctor` would run full installation discovery and the peer would
        // recursively probe back into us — and into every other peer it
        // finds — turning a single discovery invocation into a fan-out bounded
        // only by the per-level timeout. `--format json` selects the
        // machine-readable contract because the human-readable table is the
        // default when `--format` is omitted.
        using var fakePeer = FakePeerScript.BuildArgvRecorder(outputHelper);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        AssertProbeOk(result);
        Assert.NotNull(fakePeer.ArgvFile);
        Assert.True(File.Exists(fakePeer.ArgvFile), $"Expected argv recorder file at {fakePeer.ArgvFile} to exist.");
        var argv = await File.ReadAllLinesAsync(fakePeer.ArgvFile, TestContext.Current.CancellationToken);
        Assert.Equal(["doctor", "--self", "--format", "json"], argv);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsValidJsonArray_ReturnsOk()
    {
        using var fakePeer = FakePeerScript.Build(
            outputHelper,
            stdout: """
                    {
                      "checks": [],
                      "summary": { "passed": 0, "warnings": 0, "failed": 0 },
                      "installations": [
                        {
                          "path": "/peer/aspire",
                          "version": "12.5.0",
                          "channel": "stable",
                          "route": "script",
                          "pathStatus": "shadowed",
                          "status": "ok"
                        }
                      ]
                    }
                    """,
            exitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("12.5.0", ok.Info.Version);
        Assert.Equal("stable", ok.Info.Channel);
        Assert.Equal("script", ok.Info.Route);
        Assert.Equal(InstallationPathStatus.Shadowed, ok.Info.PathStatus);
    }

    [Fact]
    public async Task ProbeAsync_PeerOmitsPathStatus_DefaultsToNotOnPath()
    {
        using var fakePeer = FakePeerScript.Build(
            outputHelper,
            stdout: """
                    [
                      {
                        "path": "/peer/aspire",
                        "version": "12.5.0",
                        "status": "ok"
                      }
                    ]
                    """,
            exitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal(InstallationPathStatus.NotOnPath, ok.Info.PathStatus);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsInvalidPathStatus_DefaultsToNotOnPath()
    {
        using var fakePeer = FakePeerScript.Build(
            outputHelper,
            stdout: """
                    [
                      {
                        "path": "/peer/aspire",
                        "version": "12.5.0",
                        "pathStatus": 123,
                        "status": "ok"
                      }
                    ]
                    """,
            exitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal(InstallationPathStatus.NotOnPath, ok.Info.PathStatus);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_ReturnsFailedWhenVersionAlsoFails()
    {
        // doctor path scripted to exit 7; --version not supported by this
        // script (the default EmitExit body) → fallback path also fails
        // and the user sees the failure.
        using var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Contains("code 7", failed.Reason);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_IncludesCapturedStderr()
    {
        using var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", stderr: "peer exploded", exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Contains("Peer exited with code 7", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("stderr: peer exploded", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_PeerFailureStderr_StripsAnsiEscapes()
    {
        using var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", stderr: "\u001b[31mhello\u001b[0m", exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Contains("stderr: hello", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[0m", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_PeerFailureStderr_StripsControlCharactersExceptNewline()
    {
        using var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", stderr: "first\0\u0001\nsecond\u0002", exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Contains("stderr: first\nsecond", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0002", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_PeerFailureStderr_ReportsTruncationWhenByteCapIsExceeded()
    {
        using var fakePeer = FakePeerScript.BuildRepeatedStderr(outputHelper, PeerInstallProbe.OutputCap + 10, exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Contains("... [truncated]", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_WithEmptyStderr_KeepsReasonUnchanged()
    {
        using var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", stderr: string.Empty, exitCode: 7);

        var failed = await ProbeFakeFailureAsync(fakePeer);

        Assert.Equal("Peer exited with code 7 (and --version fallback).", failed.Reason);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_FallsBackToVersionAndReturnsPartialOk()
    {
        // Older peers (predating rich self-probe support) exit non-zero for
        // the primary probe but support `--version`. The probe must fall back so we
        // still surface a version string for those installs.
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: string.Empty,
            doctorExitCode: 1,
            versionStdout: "13.4.0-pr.16817.g790d6fa3\n",
            versionExitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("13.4.0-pr.16817.g790d6fa3", ok.Info.Version);
        // Fallback can't read route or channel from the older peer; the
        // discovery layer overlays the route from the local sidecar.
        Assert.Null(ok.Info.Channel);
    }

    [Fact]
    public async Task ProbeAsync_BothInfoAndVersionFail_ReturnsFailed()
    {
        // When both attempts fail, the primary failure reason is what the
        // user sees (with a (and --version fallback) suffix so they know
        // we tried).
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: string.Empty,
            doctorExitCode: 1,
            versionStdout: string.Empty,
            versionExitCode: 1);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("--version fallback", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsEmptyArray_FallsBackToVersion()
    {
        // Empty rich output is treated as "doctor didn't tell us anything useful"
        // and triggers the --version fallback. With no version response
        // scripted either, the overall probe fails.
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: "[]",
            doctorExitCode: 0,
            versionStdout: string.Empty,
            versionExitCode: 1);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        Assert.IsType<PeerProbeResult.Failed>(result);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsInvalidJson_FallsBackToVersion()
    {
        // Invalid JSON on the doctor path is treated as a peer failure mode
        // where the command emits help / error text, and triggers the
        // --version fallback.
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: "not json at all",
            doctorExitCode: 0,
            versionStdout: "9.0.0\n",
            versionExitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("9.0.0", ok.Info.Version);
    }

    [Theory]
    [InlineData("[1]",          "number")]
    [InlineData("[null]",       "null")]
    [InlineData("[\"string\"]", "string")]
    [InlineData("[[]]",         "nested array")]
    public async Task ProbeAsync_PeerEmitsArrayWithNonObjectFirstElement_FallsBackToVersion(string doctorStdout, string kind)
    {
        // The peer emitted a syntactically valid JSON array but the first
        // element is not an object. InstallationInfoParser.Parse calls
        // TryGetProperty on the element, which throws InvalidOperationException
        // for non-object kinds — that would otherwise abort the whole
        // discovery walk for the caller. The probe must treat it as a
        // wrong-shape response and fall back to --version.
        _ = kind; // surfaced in test name for debuggability
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: doctorStdout,
            doctorExitCode: 0,
            versionStdout: "9.0.0\n",
            versionExitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("9.0.0", ok.Info.Version);
    }

    [Fact]
    public async Task ProbeAsync_PeerHangs_TimesOutAndReturnsFailed()
    {
        // Sleep significantly longer than the probe timeout we configure so
        // the timeout path is the one that completes the await.
        var fakeSleep = TimeSpan.FromSeconds(30);
        using var fakePeer = FakePeerScript.BuildSleeper(outputHelper, sleepSeconds: (int)fakeSleep.TotalSeconds);

        // Construct a probe with a deliberately tight timeout so the test
        // doesn't have to wait the production 5s budget.
        var probe = new PeerInstallProbe(TimeSpan.FromMilliseconds(300), ProbeLogger);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);
        sw.Stop();

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("timed out", failed.Reason, StringComparison.OrdinalIgnoreCase);
        // The probe is configured with a 300ms timeout; the outer budget here
        // is a sanity bound against a probe that ignores its configured
        // timeout entirely (the bug class this test catches). Windows CI under
        // saturated CPU / slow disk has been observed to take ~5s just for the
        // fake-peer cmd.exe spawn + kill round-trip, so the budget needs to be
        // well above 5s to avoid noise without losing the bound. The important
        // invariant is that the probe returns through its timeout path well
        // before the fake peer could exit on its own.
        Assert.True(sw.Elapsed < fakeSleep / 2,
            $"Expected probe to return before the fake peer could exit on its own; took {sw.Elapsed}.");
    }

    [Fact]
    public async Task ProbeAsync_CallerCancels_KillsSpawnedProcess()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "This regression test records the shell process id using POSIX $$; Windows process-tree cancellation is covered by production code.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pidFile = Path.Combine(workspace.WorkspaceRoot.FullName, "peer.pid");
        using var fakePeer = FakePeerScript.BuildSleeperWithPidFile(outputHelper, pidFile, sleepSeconds: 30);

        var probe = new PeerInstallProbe(TimeSpan.FromSeconds(30), ProbeLogger);
        using var cts = new CancellationTokenSource();
        var probeTask = probe.ProbeAsync(fakePeer.Path, cts.Token);

        using var process = await WaitForProcessIdAsync(pidFile, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => probeTask);
        await WaitForExitAsync(process, TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
    }

    private async Task<PeerProbeResult.Failed> ProbeFakeFailureAsync(FakeScriptResult fakePeer)
    {
        // Spawn the production probe against the scripted peer and assert the
        // result is Failed. Centralizing the spawn + assertion keeps each
        // negative-path test focused on the failure reason it cares about.
        //
        // The 30s timeout is well above the production 5s default. Under heavy
        // CI load (saturated CPU, slow disk) the fake peer script — which on
        // Windows shells out to powershell.exe to emit raw stderr bytes — can
        // take several seconds just to start. These tests are about how the
        // probe formats a Failed result from real peer stderr/exit semantics,
        // not about the timeout behavior (see ProbeAsync_PeerHangs_TimesOutAndReturnsFailed
        // for that). A wider budget here removes the CI flake without changing
        // what's being tested.
        var probe = new PeerInstallProbe(TimeSpan.FromSeconds(30), ProbeLogger);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);
        return Assert.IsType<PeerProbeResult.Failed>(result);
    }

    private static async Task<Process> WaitForProcessIdAsync(string pidFile, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (File.Exists(pidFile))
            {
                var pidText = await File.ReadAllTextAsync(pidFile, cancellationToken);
                if (int.TryParse(pidText.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var pid))
                {
                    return Process.GetProcessById(pid);
                }
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
            process.Refresh();
        }
    }
}

/// <summary>
/// Builds a tiny shell/batch script in a temp dir that emits scripted
/// stdout/stderr and exits with a given code. Used as a stand-in peer in
/// PeerInstallProbeTests so we don't have to spawn a real Aspire CLI.
/// </summary>
internal static class FakePeerScript
{
    /// <summary>
    /// Produces a script that writes <paramref name="stdout"/> verbatim
    /// and exits with <paramref name="exitCode"/>. The script dispatches on
    /// its first argument, so it works with both the probe's
    /// <c>doctor --self --format json</c> invocation and the <c>--version</c>
    /// fallback.
    /// </summary>
    internal static FakeScriptResult Build(ITestOutputHelper outputHelper, string stdout, int exitCode)
    {
        return Build(outputHelper, stdout, stderr: string.Empty, exitCode);
    }

    internal static FakeScriptResult Build(ITestOutputHelper outputHelper, string stdout, string stderr, int exitCode)
    {
        return BuildInternal(outputHelper, body: ScriptBody.EmitAndExit(stdout, stderr, exitCode));
    }

    /// <summary>
    /// Builds a script that responds differently to <c>doctor</c> vs
    /// <c>--version</c> arguments so PeerInstallProbeTests can exercise
    /// the rich-probe → version fallback path.
    /// </summary>
    internal static FakeScriptResult BuildDoctorOrVersion(
        ITestOutputHelper outputHelper,
        string doctorStdout,
        int doctorExitCode,
        string versionStdout,
        int versionExitCode)
    {
        return BuildInternal(outputHelper, body: ScriptBody.DoctorOrVersion(
            doctorStdout, doctorExitCode, versionStdout, versionExitCode));
    }

    internal static FakeScriptResult BuildSleeper(ITestOutputHelper outputHelper, int sleepSeconds)
    {
        return BuildInternal(outputHelper, body: ScriptBody.Sleep(sleepSeconds));
    }

    internal static FakeScriptResult BuildSleeperWithPidFile(ITestOutputHelper outputHelper, string pidFile, int sleepSeconds)
    {
        return BuildInternal(outputHelper, body: ScriptBody.SleepWithPidFile(pidFile, sleepSeconds));
    }

    internal static FakeScriptResult BuildRepeatedStderr(ITestOutputHelper outputHelper, int byteCount, int exitCode)
    {
        return BuildInternal(outputHelper, body: ScriptBody.StderrRepeat(byteCount, exitCode));
    }

    /// <summary>
    /// Builds a script that records each positional argument (one per line)
    /// to an in-workspace file and then emits a minimal valid doctor JSON so
    /// the probe completes via the primary path without falling back to
    /// <c>--version</c>. The recorded argv file path is exposed on the
    /// returned <see cref="FakeScriptResult.ArgvFile"/>.
    /// </summary>
    internal static FakeScriptResult BuildArgvRecorder(ITestOutputHelper outputHelper)
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var argvFile = Path.Combine(workspace.WorkspaceRoot.FullName, "argv.txt");
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(workspace.WorkspaceRoot.FullName, "peer.cmd")
            : Path.Combine(workspace.WorkspaceRoot.FullName, "peer");

        var body = ScriptBody.ArgvRecorder(argvFile);
        var content = OperatingSystem.IsWindows() ? body.RenderBatch() : body.RenderShell();
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        DumpScript(outputHelper, path, content);
        return new FakeScriptResult(path, workspace, ArgvFile: argvFile);
    }

    private static FakeScriptResult BuildInternal(ITestOutputHelper outputHelper, ScriptBody body)
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(workspace.WorkspaceRoot.FullName, "peer.cmd")
            : Path.Combine(workspace.WorkspaceRoot.FullName, "peer");

        var content = OperatingSystem.IsWindows() ? body.RenderBatch() : body.RenderShell();
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            // chmod +x for /bin/sh execution. File.SetUnixFileMode is the
            // .NET-supported way to do this on Unix.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        DumpScript(outputHelper, path, content);
        return new FakeScriptResult(path, workspace);
    }

    // Write the rendered script body to the test output so a failed run's
    // log shows exactly what the probe executed (and where). xUnit only
    // surfaces test output for failing tests, so passing runs aren't
    // affected.
    private static void DumpScript(ITestOutputHelper outputHelper, string path, string content)
    {
        outputHelper.WriteLine($"[FakePeerScript] --- begin script at {path} ---");
        outputHelper.WriteLine(content);
        outputHelper.WriteLine($"[FakePeerScript] --- end script at {path} ---");
    }
}

internal sealed record FakeScriptResult(string Path, TemporaryWorkspace Workspace, string? ArgvFile = null) : IDisposable
{
    public void Dispose() => Workspace.Dispose();
}

internal abstract record ScriptBody
{
    public abstract string RenderShell();
    public abstract string RenderBatch();

    public static ScriptBody EmitAndExit(string stdout, string stderr, int exitCode) => new EmitExit(stdout, stderr, exitCode);
    public static ScriptBody Sleep(int seconds) => new SleepScript(seconds);
    public static ScriptBody SleepWithPidFile(string pidFile, int seconds) => new SleepWithPidFileScript(pidFile, seconds);
    public static ScriptBody StderrRepeat(int byteCount, int exitCode) => new StderrRepeatScript(byteCount, exitCode);
    public static ScriptBody DoctorOrVersion(string doctorStdout, int doctorExitCode, string versionStdout, int versionExitCode)
        => new DoctorOrVersionScript(doctorStdout, doctorExitCode, versionStdout, versionExitCode);
    public static ScriptBody ArgvRecorder(string argvFile) => new ArgvRecorderScript(argvFile);

    private sealed record EmitExit(string Stdout, string Stderr, int ExitCode) : ScriptBody
    {
        public override string RenderShell()
        {
            // The script behaves differently based on its first arg:
            // - "doctor" → emit the scripted stdout and exit with the scripted code
            // - anything else (e.g. "--version") → emit nothing and exit 127
            // This lets PeerInstallProbeTests isolate the "rich probe failed"
            // case without the fallback `--version` accidentally succeeding
            // by virtue of the script ignoring its args.
            return $"""
                    #!/bin/sh
                    if [ "$1" != "doctor" ]; then
                      exit 127
                    fi
                    cat <<'__ASPIRE_PEER_EOF__'
                    {Stdout}
                    __ASPIRE_PEER_EOF__
                    {RenderShellStderr(Stderr)}
                    exit {ExitCode}
                    """;
        }

        public override string RenderBatch()
        {
            var lines = Stdout.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            AppendBatchContainsArgGuard(sb, "doctor");
            foreach (var line in lines)
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
            AppendBatchStderr(sb, Stderr);
            sb.AppendLine($"exit /b {ExitCode}");
            return sb.ToString();
        }
    }

    private sealed record StderrRepeatScript(int ByteCount, int ExitCode) : ScriptBody
    {
        public override string RenderShell() =>
            $"""
             #!/bin/sh
             if [ "$1" != "doctor" ]; then
               exit 127
             fi
             dd if=/dev/zero bs={ByteCount} count=1 2>/dev/null | LC_ALL=C tr '\000' 'x' 1>&2
             exit {ExitCode}
             """;

        public override string RenderBatch()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            AppendBatchContainsArgGuard(sb, "doctor");
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -Command \"[Console]::Error.Write(('x' * {ByteCount}))\"");
            sb.AppendLine($"exit /b {ExitCode}");
            return sb.ToString();
        }
    }

    private sealed record SleepScript(int Seconds) : ScriptBody
    {
        public override string RenderShell() =>
            $"""
             #!/bin/sh
             sleep {Seconds}
             """;

        public override string RenderBatch() =>
            // Built-in timeout /t requires interactive console handling
            // sometimes; ping localhost is the conventional sleep stand-in.
            $"""
             @echo off
             ping -n {Seconds + 1} 127.0.0.1 > nul
             """;
    }

    private sealed record SleepWithPidFileScript(string PidFile, int Seconds) : ScriptBody
    {
        public override string RenderShell() =>
            $$"""
              #!/bin/sh
              printf '%s\n' "$$" > '{{PidFile}}'
              sleep {{Seconds}}
              """;

        public override string RenderBatch() =>
            throw new PlatformNotSupportedException("POSIX pid-file sleeper is not supported on Windows.");
    }

    private sealed record DoctorOrVersionScript(string DoctorStdout, int DoctorExitCode, string VersionStdout, int VersionExitCode) : ScriptBody
    {
        public override string RenderShell()
        {
            return $"""
                    #!/bin/sh
                    if [ "$1" = "doctor" ]; then
                      cat <<'__ASPIRE_DOCTOR_EOF__'
                    {DoctorStdout}
                    __ASPIRE_DOCTOR_EOF__
                      exit {DoctorExitCode}
                    fi
                    if [ "$1" = "--version" ]; then
                      cat <<'__ASPIRE_VERSION_EOF__'
                    {VersionStdout}
                    __ASPIRE_VERSION_EOF__
                      exit {VersionExitCode}
                    fi
                    exit 127
                    """;
        }

        public override string RenderBatch()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("echo %* | findstr /C:\"doctor\" > nul");
            sb.AppendLine("if not errorlevel 1 goto :doctor");
            sb.AppendLine("echo %* | findstr /C:\"--version\" > nul");
            sb.AppendLine("if not errorlevel 1 goto :version");
            sb.AppendLine("exit /b 127");
            sb.AppendLine(":doctor");
            foreach (var line in DoctorStdout.Split('\n'))
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
            sb.AppendLine($"exit /b {DoctorExitCode}");
            sb.AppendLine(":version");
            foreach (var line in VersionStdout.Split('\n'))
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
            sb.AppendLine($"exit /b {VersionExitCode}");
            return sb.ToString();
        }
    }

    private sealed record ArgvRecorderScript(string ArgvFile) : ScriptBody
    {
        // Minimal valid doctor JSON: enough for the probe to take the primary
        // path (no fallback to --version), so the recorded argv reflects the
        // first invocation only.
        private const string DoctorJson = """{"checks":[],"summary":{"passed":0,"warnings":0,"failed":0},"installations":[{"path":"/peer/aspire","version":"1.0.0","status":"ok"}]}""";

        public override string RenderShell()
        {
            // POSIX: truncate the recorder file, then write one arg per line,
            // honoring quoted args via "$@" (not $*) so multi-word args round-trip.
            return $$"""
                    #!/bin/sh
                    : > "{{ArgvFile}}"
                    for a in "$@"; do
                      printf '%s\n' "$a" >> "{{ArgvFile}}"
                    done
                    cat <<'__ASPIRE_PEER_EOF__'
                    {{DoctorJson}}
                    __ASPIRE_PEER_EOF__
                    exit 0
                    """;
        }

        public override string RenderBatch()
        {
            // Batch: shift through %1 until empty, appending each arg on its
            // own line. type nul > <file> creates an empty file (truncate).
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"type nul > \"{ArgvFile}\"");
            sb.AppendLine(":loop");
            sb.AppendLine("if \"%~1\"==\"\" goto :emit");
            sb.AppendLine($"echo %~1>>\"{ArgvFile}\"");
            sb.AppendLine("shift");
            sb.AppendLine("goto :loop");
            sb.AppendLine(":emit");
            sb.AppendLine($"echo {DoctorJson}");
            sb.AppendLine("exit /b 0");
            return sb.ToString();
        }
    }

    private static string RenderShellStderr(string stderr)
    {
        if (stderr.Length == 0)
        {
            return string.Empty;
        }

        return $"printf '{ToShellPrintfEscaped(stderr)}' 1>&2";
    }

    private static string ToShellPrintfEscaped(string value)
    {
        var builder = new StringBuilder();
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            builder.Append('\\').Append(Convert.ToString(valueByte, 8).PadLeft(3, '0'));
        }

        return builder.ToString();
    }

    private static void AppendBatchStderr(StringBuilder sb, string stderr)
    {
        if (stderr.Length == 0)
        {
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(stderr));
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -Command \"$bytes=[Convert]::FromBase64String('{encoded}'); [Console]::Error.Write([Text.Encoding]::UTF8.GetString($bytes))\"");
    }

    private static void AppendBatchContainsArgGuard(StringBuilder sb, string arg)
    {
        sb.AppendLine($"echo %* | findstr /C:\"{arg}\" > nul");
        sb.AppendLine("if errorlevel 1 exit /b 127");
    }
}
