// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="PeerInstallProbe"/>. These tests spawn
/// a real child process — a tiny test helper binary built into the test
/// project — to exercise the timeout / stdout-cap / kill paths against
/// real process semantics.
/// </summary>
public class PeerInstallProbeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ProbeAsync_BinaryNotFound_ReturnsFailed()
    {
        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missing = Path.Combine(workspace.WorkspaceRoot.FullName, "does-not-exist");

        var result = await probe.ProbeAsync(missing, TestContext.Current.CancellationToken);

        Assert.IsType<PeerProbeResult.Failed>(result);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsValidJsonArray_ReturnsOk()
    {
        var fakePeer = FakePeerScript.Build(
            outputHelper,
            stdout: """
                    [
                      {
                        "path": "/peer/aspire",
                        "version": "12.5.0",
                        "channel": "stable",
                        "route": "script",
                        "isOnPath": false,
                        "status": "ok"
                      }
                    ]
                    """,
            exitCode: 0);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<PeerProbeResult.Ok>(result);
        Assert.Equal("12.5.0", ok.Info.Version);
        Assert.Equal("stable", ok.Info.Channel);
        Assert.Equal("script", ok.Info.Route);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_ReturnsFailedWhenVersionAlsoFails()
    {
        // info path scripted to exit 7; --version not supported by this
        // script (the default EmitExit body) → fallback path also fails
        // and the user sees the failure.
        var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", exitCode: 7);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("code 7", failed.Reason);
    }

    [Fact]
    public async Task ProbeAsync_PeerExitsNonZero_FallsBackToVersionAndReturnsPartialOk()
    {
        // Older peers (predating the `info` command) exit non-zero for
        // `info` but support `--version`. The probe must fall back so we
        // still surface a version string for those installs.
        var fakePeer = FakePeerScript.BuildInfoOrVersion(
            outputHelper,
            infoStdout: string.Empty,
            infoExitCode: 1,
            versionStdout: "13.4.0-pr.16817.g790d6fa3\n",
            versionExitCode: 0);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<PeerProbeResult.Ok>(result);
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
        var fakePeer = FakePeerScript.BuildInfoOrVersion(
            outputHelper,
            infoStdout: string.Empty,
            infoExitCode: 1,
            versionStdout: string.Empty,
            versionExitCode: 1);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("--version fallback", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsEmptyArray_FallsBackToVersion()
    {
        // Empty array is treated as "info didn't tell us anything useful"
        // and triggers the --version fallback. With no version response
        // scripted either, the overall probe fails.
        var fakePeer = FakePeerScript.BuildInfoOrVersion(
            outputHelper,
            infoStdout: "[]",
            infoExitCode: 0,
            versionStdout: string.Empty,
            versionExitCode: 1);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        Assert.IsType<PeerProbeResult.Failed>(result);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsInvalidJson_FallsBackToVersion()
    {
        // Invalid JSON on the info path is treated as the older peer's
        // common failure mode (output before the `info` command existed
        // was a help / error string), and triggers the --version fallback.
        var fakePeer = FakePeerScript.BuildInfoOrVersion(
            outputHelper,
            infoStdout: "not json at all",
            infoExitCode: 0,
            versionStdout: "9.0.0\n",
            versionExitCode: 0);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<PeerProbeResult.Ok>(result);
        Assert.Equal("9.0.0", ok.Info.Version);
    }

    [Fact]
    public async Task ProbeAsync_PeerHangs_TimesOutAndKills()
    {
        // Sleep significantly longer than the probe timeout we configure so
        // the timeout path is the one that completes the await.
        var fakePeer = FakePeerScript.BuildSleeper(outputHelper, sleepSeconds: 30);

        // Construct a probe with a deliberately tight timeout so the test
        // doesn't have to wait the production 5s budget.
        var probe = new PeerInstallProbe(TimeSpan.FromMilliseconds(300), NullLogger<PeerInstallProbe>.Instance);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);
        sw.Stop();

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("timed out", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected probe to return within a few seconds after timing out; took {sw.Elapsed}.");
    }

    [Fact]
    public async Task ProbeAsync_CallerCancels_KillsSpawnedProcess()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "This regression test records the shell process id using POSIX $$; Windows process-tree cancellation is covered by production code.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var pidFile = Path.Combine(workspace.WorkspaceRoot.FullName, "peer.pid");
        using var fakePeer = FakePeerScript.BuildSleeperWithPidFile(outputHelper, pidFile, sleepSeconds: 30);

        var probe = new PeerInstallProbe(TimeSpan.FromSeconds(30), NullLogger<PeerInstallProbe>.Instance);
        using var cts = new CancellationTokenSource();
        var probeTask = probe.ProbeAsync(fakePeer.Path, cts.Token);

        using var process = await WaitForProcessIdAsync(pidFile, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => probeTask);
        await WaitForExitAsync(process, TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
    }

    private static async Task<Process> WaitForProcessIdAsync(string pidFile, CancellationToken cancellationToken)
    {
        while (!File.Exists(pidFile))
        {
            await Task.Delay(20, cancellationToken);
        }

        var pidText = await File.ReadAllTextAsync(pidFile, cancellationToken);
        var pid = int.Parse(pidText.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        return Process.GetProcessById(pid);
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
    /// and exits with <paramref name="exitCode"/>. The script ignores its
    /// arguments so we can pass it the probe's <c>info --format json</c>
    /// invocation without modification.
    /// </summary>
    internal static FakeScriptResult Build(ITestOutputHelper outputHelper, string stdout, int exitCode)
    {
        return BuildInternal(outputHelper, body: ScriptBody.EmitAndExit(stdout, exitCode));
    }

    /// <summary>
    /// Builds a script that responds differently to <c>info</c> vs
    /// <c>--version</c> arguments so PeerInstallProbeTests can exercise
    /// the info → version fallback path.
    /// </summary>
    internal static FakeScriptResult BuildInfoOrVersion(
        ITestOutputHelper outputHelper,
        string infoStdout,
        int infoExitCode,
        string versionStdout,
        int versionExitCode)
    {
        return BuildInternal(outputHelper, body: ScriptBody.InfoOrVersion(
            infoStdout, infoExitCode, versionStdout, versionExitCode));
    }

    internal static FakeScriptResult BuildSleeper(ITestOutputHelper outputHelper, int sleepSeconds)
    {
        return BuildInternal(outputHelper, body: ScriptBody.Sleep(sleepSeconds));
    }

    internal static FakeScriptResult BuildSleeperWithPidFile(ITestOutputHelper outputHelper, string pidFile, int sleepSeconds)
    {
        return BuildInternal(outputHelper, body: ScriptBody.SleepWithPidFile(pidFile, sleepSeconds));
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

        return new FakeScriptResult(path, workspace);
    }
}

internal sealed record FakeScriptResult(string Path, TemporaryWorkspace Workspace) : IDisposable
{
    public void Dispose() => Workspace.Dispose();
}

internal abstract record ScriptBody
{
    public abstract string RenderShell();
    public abstract string RenderBatch();

    public static ScriptBody EmitAndExit(string stdout, int exitCode) => new EmitExit(stdout, exitCode);
    public static ScriptBody Sleep(int seconds) => new SleepScript(seconds);
    public static ScriptBody SleepWithPidFile(string pidFile, int seconds) => new SleepWithPidFileScript(pidFile, seconds);
    public static ScriptBody InfoOrVersion(string infoStdout, int infoExitCode, string versionStdout, int versionExitCode)
        => new InfoOrVersionScript(infoStdout, infoExitCode, versionStdout, versionExitCode);

    private sealed record EmitExit(string Stdout, int ExitCode) : ScriptBody
    {
        public override string RenderShell()
        {
            // The script behaves differently based on its first arg:
            // - "info" → emit the scripted stdout and exit with the scripted code
            // - anything else (e.g. "--version") → emit nothing and exit 127
            // This lets PeerInstallProbeTests isolate the "info path failed"
            // case without the fallback `--version` accidentally succeeding
            // by virtue of the script ignoring its args.
            return $"""
                    #!/bin/sh
                    if [ "$1" != "info" ]; then
                      exit 127
                    fi
                    cat <<'__ASPIRE_PEER_EOF__'
                    {Stdout}
                    __ASPIRE_PEER_EOF__
                    exit {ExitCode}
                    """;
        }

        public override string RenderBatch()
        {
            var lines = Stdout.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("if not \"%~1\" == \"info\" exit /b 127");
            foreach (var line in lines)
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
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

    private sealed record InfoOrVersionScript(string InfoStdout, int InfoExitCode, string VersionStdout, int VersionExitCode) : ScriptBody
    {
        public override string RenderShell()
        {
            return $"""
                    #!/bin/sh
                    if [ "$1" = "info" ]; then
                      cat <<'__ASPIRE_INFO_EOF__'
                    {InfoStdout}
                    __ASPIRE_INFO_EOF__
                      exit {InfoExitCode}
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
            sb.AppendLine("if \"%~1\" == \"info\" goto :info");
            sb.AppendLine("if \"%~1\" == \"--version\" goto :version");
            sb.AppendLine("exit /b 127");
            sb.AppendLine(":info");
            foreach (var line in InfoStdout.Split('\n'))
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
            sb.AppendLine($"exit /b {InfoExitCode}");
            sb.AppendLine(":version");
            foreach (var line in VersionStdout.Split('\n'))
            {
                sb.Append("echo ").AppendLine(line.TrimEnd('\r'));
            }
            sb.AppendLine($"exit /b {VersionExitCode}");
            return sb.ToString();
        }
    }
}
