// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="PeerInstallProbe"/>. These tests spawn
/// a real child process — a tiny test helper binary built into the test
/// project — to exercise the timeout / stdout-cap / kill paths against
/// real process semantics.
/// </summary>
/// <remarks>
/// Joins <see cref="EnvVarMutatingTestCollection"/> because
/// <c>ProbeAsync_StripsIdentityEnvVarOverridesBeforeSpawningPeer</c> mutates
/// process-wide <c>ASPIRE_CLI_*</c> environment variables via
/// <see cref="EnvVarOverride"/>: xUnit runs test classes in parallel by
/// default, so without this collection another test's own child-process
/// spawn could transiently observe the poisoned override.
/// </remarks>
[Collection(EnvVarMutatingTestCollection.Name)]
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missing = Path.Combine(workspace.WorkspaceRoot.FullName, "does-not-exist");

        var result = await probe.ProbeAsync(missing, TestContext.Current.CancellationToken);

        Assert.IsType<PeerProbeResult.Failed>(result);
    }

    [Fact]
    public async Task ProbeAsync_InvokesPeerWithInfoSelfFormatJson()
    {
        // The peer must be asked to describe ONLY itself. Without --self,
        // `aspire --info` would run full installation discovery and the peer
        // would recursively probe back into us — and into every other peer
        // it finds — turning a single discovery invocation into a fan-out
        // bounded only by the per-level timeout. `--format json` selects the
        // machine-readable contract because the human-readable table is the
        // default when `--format` is omitted.
        //
        // The scripted peer accepts ONLY the new `--info --self --format
        // json` contract (see FakePeerScript.BuildArgvRecorder) and would
        // exit 127 for `doctor`/`--version`, so a successful Ok result here,
        // together with a one-line argv log, proves the probe both calls the
        // new contract first AND stops there without falling back.
        using var fakePeer = FakePeerScript.BuildArgvRecorder(outputHelper);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("1.0.0", ok.Info.Version);
        Assert.Equal("stable", ok.Info.Channel);
        Assert.Equal("script", ok.Info.Route);
        Assert.Equal(InstallationPathStatus.Active, ok.Info.PathStatus);
        Assert.Equal(InstallationInfoStatus.Ok, ok.Info.Status);

        Assert.NotNull(fakePeer.ArgvFile);
        Assert.True(File.Exists(fakePeer.ArgvFile), $"Expected argv recorder file at {fakePeer.ArgvFile} to exist.");
        var argv = await File.ReadAllLinesAsync(fakePeer.ArgvFile, TestContext.Current.CancellationToken);
        Assert.Equal(["--info", "--self", "--format", "json"], argv);
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

        // FakePeerScript.Build's EmitExit body only recognizes "doctor" as
        // arg[0]; --info and --version both fall through to its "exit 127"
        // branch. With no stderr anywhere, none of the three labeled
        // segments should pick up a "; stderr: " suffix.
        Assert.Equal(
            "--info: Peer exited with code 127.; doctor --self: Peer exited with code 7.; --version: Peer exited with code 127.",
            failed.Reason);
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
        // When every attempt fails, the aggregate reason must retain each
        // stage's own diagnostic instead of collapsing to a single one.
        // --info isn't scripted by BuildDoctorOrVersion, so it falls
        // through that script's "unrecognized option" branch (exit 127);
        // doctor and --version are both explicitly scripted to fail.
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: string.Empty,
            doctorExitCode: 1,
            versionStdout: string.Empty,
            versionExitCode: 1);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("--info: Peer exited with code 127.", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("doctor --self: Peer exited with code 1.", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("--version: Peer exited with code 1.", failed.Reason, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("{}",                       "object without installations")]
    [InlineData("""{"installations":42}""", "installations not an array")]
    [InlineData("42",                       "bare number")]
    [InlineData("\"oops\"",                 "bare string")]
    [InlineData("null",                     "bare null")]
    public async Task ProbeAsync_PeerEmitsWrongRootKind_FallsBackToVersion(string doctorStdout, string kind)
    {
        // Neither the bare-array new contract nor the legacy
        // {"installations": [...]} envelope matches these payloads, so
        // TryParseRichProbeResult must treat them the same as malformed/empty
        // JSON: fall through to --version rather than throwing.
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
    public async Task ProbeAsync_PeerEmitsEmptyObjectRow_FallsBackToVersion()
    {
        using var fakePeer = FakePeerScript.BuildDoctorOrVersion(
            outputHelper,
            doctorStdout: "[{}]",
            doctorExitCode: 0,
            versionStdout: "9.0.0\n",
            versionExitCode: 0);

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("9.0.0", ok.Info.Version);
    }

    [Fact]
    public async Task ProbeAsync_InfoFailsLegacyValid_InvokesExactlyInfoThenDoctorAndKeepsRichMetadata()
    {
        // The peer doesn't understand --info --self (e.g. an older build):
        // the probe must fall through to the legacy doctor --self contract
        // and still surface its channel/route/status metadata — not just a
        // bare version string. --version must never be spawned once the
        // legacy attempt already produced a usable row.
        using var fakePeer = FakePeerScript.BuildThreeStage(
            outputHelper,
            info: StageResponse.Fail(exitCode: 127),
            doctor: new StageResponse(
                Stdout: """{"checks":[],"summary":{"passed":0,"warnings":0,"failed":0},"installations":[{"path":"/peer/aspire","version":"12.5.0","channel":"staging","route":"nightly","pathStatus":"shadowed","status":"ok"}]}""",
                ExitCode: 0),
            version: new StageResponse("9.9.9\n", 0));

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("12.5.0", ok.Info.Version);
        Assert.Equal("staging", ok.Info.Channel);
        Assert.Equal("nightly", ok.Info.Route);
        Assert.Equal(InstallationPathStatus.Shadowed, ok.Info.PathStatus);
        Assert.Equal(InstallationInfoStatus.Ok, ok.Info.Status);

        Assert.NotNull(fakePeer.InvocationLog);
        var invocations = await File.ReadAllLinesAsync(fakePeer.InvocationLog, TestContext.Current.CancellationToken);
        Assert.Equal(["--info", "doctor"], invocations);
    }

    [Fact]
    public async Task ProbeAsync_BothRichStagesFailVersionValid_InvokesAllThreeStagesAndReturnsVersionOnly()
    {
        // Both self-describe contracts fail (peer predates both); the probe
        // must fall all the way through to --version and surface only a
        // version string, with the two failed rich attempts and the version
        // attempt each spawned exactly once, in order.
        using var fakePeer = FakePeerScript.BuildThreeStage(
            outputHelper,
            info: StageResponse.Fail(exitCode: 127),
            doctor: StageResponse.Fail(exitCode: 127),
            version: new StageResponse("13.4.0-pr.16817.g790d6fa3\n", 0));

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("13.4.0-pr.16817.g790d6fa3", ok.Info.Version);
        // Unknown metadata the --version floor can't report stays null/default
        // rather than being invented.
        Assert.Null(ok.Info.Channel);
        Assert.Null(ok.Info.Route);
        Assert.Null(ok.Info.CanonicalPath);

        Assert.NotNull(fakePeer.InvocationLog);
        var invocations = await File.ReadAllLinesAsync(fakePeer.InvocationLog, TestContext.Current.CancellationToken);
        Assert.Equal(["--info", "doctor", "--version"], invocations);
    }

    [Fact]
    public async Task ProbeAsync_InfoStageRowHasBothSourceAndRoute_SourceWins()
    {
        // The new --info --self contract uses "source"; the legacy doctor
        // contract uses "route". InstallationInfoParser.Parse already prefers
        // "source", but this proves it end-to-end through the probe's first
        // (new-contract) stage specifically, for a row that (unusually)
        // carries both properties.
        using var fakePeer = FakePeerScript.BuildThreeStage(
            outputHelper,
            info: new StageResponse(
                Stdout: """[{"path":"/peer/aspire","version":"1.2.3","source":"winget","route":"script","status":"ok"}]""",
                ExitCode: 0),
            doctor: StageResponse.Fail(),
            version: StageResponse.Fail());

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = AssertProbeOk(result);
        Assert.Equal("winget", ok.Info.Route);

        // A successful new-contract row must not trigger doctor/--version.
        Assert.NotNull(fakePeer.InvocationLog);
        var invocations = await File.ReadAllLinesAsync(fakePeer.InvocationLog, TestContext.Current.CancellationToken);
        Assert.Equal(["--info"], invocations);
    }

    [Fact]
    public async Task ProbeAsync_SharedBudget_LaterStageDoesNotGetAFreshTimeoutAfterAnEarlierStageStalls()
    {
        // Regression guard for the shared-budget contract: all three
        // compatibility attempts must draw from ONE total timeout, not a
        // fresh timeout each. Values below are chosen with generous margins
        // (not tight millisecond math) so the assertions distinguish a
        // correct shared budget from a per-stage-reset bug without being
        // sensitive to ordinary process-spawn jitter:
        //
        //   - probe timeout: 1500ms
        //   - --info: sleeps 1000ms, then fails -> ~500ms of shared budget
        //     remains for the doctor attempt.
        //   - doctor: sleeps 1000ms, then WOULD succeed with a valid rich
        //     row -- but only if it gets that full 1000ms. A correct shared
        //     budget gives it only the ~500ms remaining, so the timeout
        //     fires and it is killed before responding. A buggy
        //     reset-per-stage implementation would instead give doctor a
        //     fresh 1500ms slice, in which its 1000ms delay comfortably
        //     succeeds -- flipping the result from Failed to Ok.
        //   - --version: configured to succeed instantly if spawned at all,
        //     so a reset that lets this fallback run would also flip the
        //     result from Failed to Ok.
        var timeout = TimeSpan.FromMilliseconds(1500);
        using var fakePeer = FakePeerScript.BuildThreeStage(
            outputHelper,
            info: new StageResponse(string.Empty, 1, DelayMs: 1000),
            doctor: new StageResponse(
                Stdout: """[{"path":"/peer/aspire","version":"1.0.0","status":"ok"}]""",
                ExitCode: 0,
                DelayMs: 1000),
            version: new StageResponse("9.0.0\n", 0));

        var probe = new PeerInstallProbe(timeout, ProbeLogger);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        // Primary evidence: the doctor stage's would-be success was NOT
        // honored, because it didn't get a fresh budget.
        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("timed out", failed.Reason, StringComparison.OrdinalIgnoreCase);
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

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

    [Fact]
    public async Task ProbeAsync_AllStagesFailDifferently_FinalReasonPreservesEachStagesOwnDiagnostic()
    {
        // Each stage fails via a DIFFERENT mechanism with a distinctive
        // fingerprint, so this test is falsifiable: reverting the aggregate
        // failure reason to only describe the doctor stage (the prior
        // behavior this PR fixes) would make the --info and --version
        // assertions below fail, because their fingerprints would never
        // appear in the reason at all.
        //
        //   --info:        real exit code 42 + distinctive stderr (also
        //                   wrapped in ANSI color codes, to prove
        //                   sanitization still applies per-stage inside the
        //                   aggregate, not just in the single-stage case).
        //   doctor --self:  exits 0 but returns unparsable JSON, so its
        //                   segment must say WHY (malformed JSON) rather
        //                   than a generic "no usable output".
        //   --version:      a different real exit code (13) + a distinct
        //                   stderr fingerprint containing a raw control
        //                   character (BEL, \u0007), to prove control
        //                   characters are stripped from every stage's
        //                   segment, not just the primary one.
        using var fakePeer = FakePeerScript.BuildThreeStage(
            outputHelper,
            info: new StageResponse(string.Empty, ExitCode: 42, Stderr: "\u001b[31mCAFEF00D-info-exploded\u001b[0m"),
            doctor: new StageResponse("DEADBEEF-not-json", ExitCode: 0),
            version: new StageResponse(string.Empty, ExitCode: 13, Stderr: "FEEDFACE-version-exploded\u0007"));

        var probe = CreateProbeWithGenerousTimeout();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);

        // --info: its own exit code + sanitized stderr must survive into the
        // final aggregate instead of being discarded as "unimportant noise"
        // (the prior behavior this PR fixes).
        Assert.Contains("--info: Peer exited with code 42", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("CAFEF00D-info-exploded", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", failed.Reason, StringComparison.Ordinal);

        // doctor --self: exited 0, so its segment must explain the JSON
        // parse failure rather than reporting a generic "no usable output".
        Assert.Contains("doctor --self: Peer returned malformed JSON", failed.Reason, StringComparison.Ordinal);

        // --version: its own distinct exit code + sanitized stderr, with the
        // raw BEL control character stripped.
        Assert.Contains("--version: Peer exited with code 13", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("FEEDFACE-version-exploded", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", failed.Reason, StringComparison.Ordinal);

        // Bounded length and free of any remaining raw control character
        // (none of the fingerprints above use '\n', so this is a strict
        // zero-control-character check for this test's inputs).
        Assert.True(failed.Reason.Length <= 4096,
            $"Expected the aggregate reason to stay within the bounded cap; was {failed.Reason.Length} chars.");
        Assert.DoesNotContain(failed.Reason, char.IsControl);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The identity environment variable leak probe uses a POSIX shell script.")]
    public async Task ProbeAsync_StripsIdentityEnvVarOverridesBeforeSpawningPeer()
    {
        // IdentityResolver.IdentityEnvVarNames is the exact strip-list
        // PeerInstallProbe applies before spawning any of the three probe
        // stages (see the strip loop in SpawnAndCaptureAsync). Poison every
        // one of them in THIS process, then prove none of them reach the
        // spawned peer: the fake peer script dumps whatever value it
        // actually observes for each name to a file (so the test asserts
        // absence directly, not just indirectly through a success/failure
        // signal), and ALSO emits a poison marker + non-zero exit if it
        // observes ANY of them set — so a regression that stops stripping
        // even one override turns this from Ok into Failed.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dumpFile = Path.Combine(workspace.WorkspaceRoot.FullName, "identity-env-dump.txt");
        using var fakePeer = FakePeerScript.BuildIdentityEnvVarLeakProbe(outputHelper, dumpFile, IdentityResolver.IdentityEnvVarNames);

        var overrides = new List<EnvVarOverride>();
        try
        {
            foreach (var name in IdentityResolver.IdentityEnvVarNames)
            {
                overrides.Add(new EnvVarOverride(name, $"poison-{name}"));
            }

            var probe = CreateProbeWithGenerousTimeout();
            var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

            // A leaked override would make the fake peer emit its poison
            // marker (non-zero exit) instead of a valid --info row, so a
            // correct strip makes this Ok, not Failed.
            var ok = AssertProbeOk(result);
            Assert.Equal("1.0.0", ok.Info.Version);

            Assert.True(File.Exists(dumpFile), $"Expected identity env dump file at {dumpFile} to exist.");
            var dumpedLines = await File.ReadAllLinesAsync(dumpFile, TestContext.Current.CancellationToken);
            foreach (var name in IdentityResolver.IdentityEnvVarNames)
            {
                // The dump line is always written (even when the var is
                // absent, in which case printenv yields an empty value), so
                // asserting the literal poisoned value is ABSENT is a
                // direct, per-variable proof rather than an inference from
                // the overall Ok/Failed outcome above.
                Assert.DoesNotContain(dumpedLines, line => line.StartsWith($"{name}=poison-", StringComparison.Ordinal));
            }
        }
        finally
        {
            foreach (var envVarOverride in overrides)
            {
                envVarOverride.Dispose();
            }
        }
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
    /// to an in-workspace file and then emits a minimal valid <c>--info
    /// --self --format json</c> bare-array JSON so the probe completes via
    /// the new-contract primary path without falling back to <c>doctor</c>
    /// or <c>--version</c>. The recorded argv file path is exposed on the
    /// returned <see cref="FakeScriptResult.ArgvFile"/>.
    /// </summary>
    internal static FakeScriptResult BuildArgvRecorder(ITestOutputHelper outputHelper)
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

    /// <summary>
    /// Builds a script that responds independently to each of the three
    /// probe stages (<c>--info</c>, <c>doctor</c>, <c>--version</c>),
    /// dispatching on the first argument, and appends the dispatched stage
    /// token to an in-workspace invocation log file on every call — so a
    /// test can assert exactly which stages were spawned and in what order,
    /// even when a stage responds with a transport failure rather than a
    /// distinguishable stdout. The log file path is exposed on the returned
    /// <see cref="FakeScriptResult.InvocationLog"/>.
    /// </summary>
    internal static FakeScriptResult BuildThreeStage(
        ITestOutputHelper outputHelper,
        StageResponse info,
        StageResponse doctor,
        StageResponse version)
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invocationLog = Path.Combine(workspace.WorkspaceRoot.FullName, "invocations.txt");
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(workspace.WorkspaceRoot.FullName, "peer.cmd")
            : Path.Combine(workspace.WorkspaceRoot.FullName, "peer");

        var body = ScriptBody.ThreeStage(invocationLog, info, doctor, version);
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
        return new FakeScriptResult(path, workspace, InvocationLog: invocationLog);
    }

    private static FakeScriptResult BuildInternal(ITestOutputHelper outputHelper, ScriptBody body)
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

    /// <summary>
    /// Builds a script that, for every invocation regardless of arguments,
    /// dumps the observed value of each name in <paramref name="varNames"/>
    /// to <paramref name="dumpFile"/> (one <c>NAME=value</c> line per name,
    /// value empty when unset), then either emits a poison marker and exits
    /// non-zero if ANY of those names is non-empty in its environment, or a
    /// valid <c>--info --self --format json</c> row if none of them are.
    /// Used by <c>ProbeAsync_StripsIdentityEnvVarOverridesBeforeSpawningPeer</c>
    /// to prove <see cref="PeerInstallProbe"/>'s identity-env-var strip
    /// actually removes every override before the peer is spawned, rather
    /// than assuming it from code inspection alone.
    /// </summary>
    internal static FakeScriptResult BuildIdentityEnvVarLeakProbe(
        ITestOutputHelper outputHelper, string dumpFile, IReadOnlyList<string> varNames)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The identity-env-var leak probe script is POSIX shell only; PeerInstallProbe's strip loop itself is platform-agnostic and is exercised on Windows by the other PeerInstallProbeTests.");
        }

        return BuildInternal(outputHelper, body: ScriptBody.IdentityEnvVarLeakProbe(dumpFile, varNames));
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

internal sealed record FakeScriptResult(string Path, TemporaryWorkspace Workspace, string? ArgvFile = null, string? InvocationLog = null) : IDisposable
{
    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Configures one stage's response for <see cref="ScriptBody.ThreeStage"/>:
/// what stdout/exit code the peer returns for that stage, and an optional
/// delay (in milliseconds) before responding, used to model a slow peer for
/// shared-budget tests.
/// </summary>
internal readonly record struct StageResponse(string Stdout, int ExitCode, int DelayMs = 0, string Stderr = "")
{
    internal static StageResponse Fail(int exitCode = 1) => new(string.Empty, exitCode);
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
    public static ScriptBody ThreeStage(string invocationLogFile, StageResponse info, StageResponse doctor, StageResponse version)
        => new ThreeStageScript(invocationLogFile, info, doctor, version);
    public static ScriptBody IdentityEnvVarLeakProbe(string dumpFile, IReadOnlyList<string> varNames)
        => new IdentityEnvVarLeakProbeScript(dumpFile, varNames);

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
        // Minimal valid `--info --self --format json` bare-array JSON: enough
        // for the probe to take the new-contract primary path (no fallback
        // to doctor/--version), so the recorded argv reflects the first
        // invocation only.
        private const string InfoJson = """[{"path":"/peer/aspire","canonicalPath":"/peer/aspire-canonical","version":"1.0.0","channel":"stable","source":"script","pathStatus":"active","status":"ok"}]""";

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
                    {{InfoJson}}
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
            sb.AppendLine($"echo {InfoJson}");
            sb.AppendLine("exit /b 0");
            return sb.ToString();
        }
    }

    /// <summary>
    /// For every invocation regardless of arguments: dumps the observed
    /// value of each name in <see cref="VarNames"/> to <see cref="DumpFile"/>
    /// (one <c>NAME=value</c> line per name, always written even when the
    /// value is empty, so a test can assert absence directly rather than
    /// only inferring it from the poison/Ok outcome below), then either
    /// emits a poison marker and exits non-zero if ANY of those names is
    /// non-empty in its environment, or a valid <c>--info --self --format
    /// json</c> row if none of them are.
    /// </summary>
    private sealed record IdentityEnvVarLeakProbeScript(string DumpFile, IReadOnlyList<string> VarNames) : ScriptBody
    {
        private const string InfoJson = """[{"path":"/peer/aspire","version":"1.0.0","channel":"stable","source":"script","status":"ok"}]""";

        public override string RenderShell()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine($": > \"{DumpFile}\"");
            sb.AppendLine("LEAKED=0");
            foreach (var name in VarNames)
            {
                sb.AppendLine($"printf '{name}=%s\\n' \"${name}\" >> \"{DumpFile}\"");
                sb.AppendLine($"if [ -n \"${name}\" ]; then LEAKED=1; fi");
            }

            sb.AppendLine("if [ \"$LEAKED\" = \"1\" ]; then");
            // Written to stderr rather than stdout: a leaked override must
            // make the probe treat this stage as an unusable rich-probe
            // result (non-zero exit), not as a valid-but-wrong JSON row.
            sb.AppendLine("  echo 'IDENTITY_ENV_LEAKED' 1>&2");
            sb.AppendLine("  exit 66");
            sb.AppendLine("fi");
            sb.AppendLine("cat <<'__ASPIRE_ENV_PROBE_EOF__'");
            sb.AppendLine(InfoJson);
            sb.AppendLine("__ASPIRE_ENV_PROBE_EOF__");
            sb.AppendLine("exit 0");
            return sb.ToString();
        }

        public override string RenderBatch()
            => throw new PlatformNotSupportedException(
                "The identity-env-var leak probe script is POSIX shell only; see FakePeerScript.BuildIdentityEnvVarLeakProbe.");
    }

    private sealed record ThreeStageScript(
        string InvocationLogFile,
        StageResponse Info,
        StageResponse Doctor,
        StageResponse Version) : ScriptBody
    {
        public override string RenderShell()
        {
            return $$"""
                    #!/bin/sh
                    printf '%s\n' "$1" >> "{{InvocationLogFile}}"
                    if [ "$1" = "--info" ]; then
                    {{RenderShellStage(Info)}}
                    fi
                    if [ "$1" = "doctor" ]; then
                    {{RenderShellStage(Doctor)}}
                    fi
                    if [ "$1" = "--version" ]; then
                    {{RenderShellStage(Version)}}
                    fi
                    exit 127
                    """;
        }

        public override string RenderBatch()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"echo %~1>>\"{InvocationLogFile}\"");
            sb.AppendLine("if \"%~1\"==\"--info\" goto :info");
            sb.AppendLine("if \"%~1\"==\"doctor\" goto :doctor");
            sb.AppendLine("if \"%~1\"==\"--version\" goto :version");
            sb.AppendLine("exit /b 127");
            sb.AppendLine(":info");
            AppendBatchStage(sb, Info);
            sb.AppendLine(":doctor");
            AppendBatchStage(sb, Doctor);
            sb.AppendLine(":version");
            AppendBatchStage(sb, Version);
            return sb.ToString();
        }

        // Renders one stage's body inside a shell `if` block: an optional
        // fractional-second sleep (for shared-budget tests that need a peer
        // to take a controlled amount of time before responding), then the
        // scripted stdout, an optional stderr fingerprint, and the exit code.
        private static string RenderShellStage(StageResponse stage)
        {
            var sb = new StringBuilder();
            if (stage.DelayMs > 0)
            {
                sb.AppendLine($"  sleep {FormatSleepSeconds(stage.DelayMs)}");
            }

            sb.AppendLine("  cat <<'__ASPIRE_STAGE_EOF__'");
            sb.AppendLine(stage.Stdout);
            sb.AppendLine("__ASPIRE_STAGE_EOF__");
            if (stage.Stderr.Length > 0)
            {
                sb.AppendLine($"  {RenderShellStderr(stage.Stderr)}");
            }

            sb.Append("  exit ").Append(stage.ExitCode.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void AppendBatchStage(StringBuilder sb, StageResponse stage)
        {
            if (stage.DelayMs > 0)
            {
                // `ping` only resolves whole-second delays; round up so a
                // configured sub-second delay never becomes a no-op on
                // Windows.
                var seconds = Math.Max(1, (int)Math.Ceiling(stage.DelayMs / 1000.0));
                sb.AppendLine($"ping -n {seconds + 1} 127.0.0.1 > nul");
            }

            foreach (var line in stage.Stdout.Split('\n'))
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }

            AppendBatchStderr(sb, stage.Stderr);

            sb.AppendLine($"exit /b {stage.ExitCode}");
        }

        private static string FormatSleepSeconds(int delayMs)
            => (delayMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
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
