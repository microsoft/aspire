// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task ProbeAsync_PeerExitsNonZero_ReturnsFailed()
    {
        var fakePeer = FakePeerScript.Build(outputHelper, stdout: "{}", exitCode: 7);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("code 7", failed.Reason);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsInvalidJson_ReturnsFailed()
    {
        var fakePeer = FakePeerScript.Build(outputHelper, stdout: "not json at all", exitCode: 0);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("malformed", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_PeerEmitsEmptyArray_ReturnsFailed()
    {
        var fakePeer = FakePeerScript.Build(outputHelper, stdout: "[]", exitCode: 0);

        var probe = new PeerInstallProbe(NullLogger<PeerInstallProbe>.Instance);
        var result = await probe.ProbeAsync(fakePeer.Path, TestContext.Current.CancellationToken);

        var failed = Assert.IsType<PeerProbeResult.Failed>(result);
        Assert.Contains("non-empty array", failed.Reason);
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

    internal static FakeScriptResult BuildSleeper(ITestOutputHelper outputHelper, int sleepSeconds)
    {
        return BuildInternal(outputHelper, body: ScriptBody.Sleep(sleepSeconds));
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

    private sealed record EmitExit(string Stdout, int ExitCode) : ScriptBody
    {
        public override string RenderShell()
        {
            // Heredoc preserves the exact bytes (incl. newlines) we want.
            return $"""
                    #!/bin/sh
                    cat <<'__ASPIRE_PEER_EOF__'
                    {Stdout}
                    __ASPIRE_PEER_EOF__
                    exit {ExitCode}
                    """;
        }

        public override string RenderBatch()
        {
            // Echo each line verbatim. cmd.exe doesn't have a heredoc, so
            // we split-and-echo. Special characters (^ & | < >) would need
            // escaping; the JSON shapes used by these tests don't contain
            // any. Tests that need richer payloads should add escaping.
            var lines = Stdout.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
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
}
