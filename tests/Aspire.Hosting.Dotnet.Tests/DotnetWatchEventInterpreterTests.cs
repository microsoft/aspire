// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.Watch;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Tests;

// Resource-watch notices are derived from structured status, never console diagnostics.
public class DotnetWatchEventInterpreterTests
{
    private static readonly string s_root = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static readonly string s_api = Path.Combine(s_root, "Api", "Api.csproj");
    private static readonly string s_worker = Path.Combine(s_root, "Worker", "Worker.csproj");

    private static DotnetWatchEventInterpreter CreateReady(string watchResourceName = "dotnet-watch")
    {
        var interpreter = new DotnetWatchEventInterpreter([(s_api, "api"), (s_worker, "worker")], watchResourceName);
        Assert.Empty(interpreter.OnStatus("building", [s_api, s_worker], null, null));
        var notice = Assert.Single(interpreter.OnStatus("build_complete", [s_api, s_worker], true, null));
        Assert.Equal(WatchNoticeKind.Watching, notice.Kind);
        Assert.StartsWith("👀 Watching api, worker for changes.", notice.Message);
        Assert.Equal([s_api, s_worker], notice.ProjectPaths);
        return interpreter;
    }

    [Fact]
    public void NoticeEventIdsAreStablePerKind()
    {
        Assert.Equal(new EventId(7101, "WatchNotice.HotReloadApplied"), DotnetWatchCoordinator.NoticeEventId(WatchNoticeKind.HotReloadApplied));
        var ids = Enum.GetValues<WatchNoticeKind>().Select(k => DotnetWatchCoordinator.NoticeEventId(k).Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void HotReloadIsReportedForTheProjectsTheEventNames()
    {
        var interpreter = CreateReady("dotnet-watch-api");

        var notice = Assert.Single(interpreter.OnStatus("hot_reload_applied", [s_api], null, null));

        // The event follows every apply attempt (applied, blocked by compile errors, no changes), so the notice is outcome-neutral.
        Assert.Equal(WatchNoticeKind.HotReloadApplied, notice.Kind);
        Assert.Equal("⌚ The watcher processed the change for api. Watcher 10.0.403 does not report whether hot reload applied. Open 'dotnet-watch-api' Console logs in the dashboard, or run 'aspire logs dotnet-watch-api --tail 50'.", notice.Message);
        Assert.Equal([s_api], notice.ProjectPaths);
    }

    [Fact]
    public void HotReloadWithoutProjectsIsIgnored()
    {
        // The watcher reports an empty update during a rebuild/restart cycle.
        Assert.Empty(CreateReady().OnStatus("hot_reload_applied", [], null, null));
    }

    [Fact]
    public void InitialBuildFailureThenRecovery()
    {
        var interpreter = new DotnetWatchEventInterpreter([(s_api, "api"), (s_worker, "worker")], "dotnet-watch");
        Assert.Empty(interpreter.OnStatus("building", [s_api, s_worker], null, null));

        // 10.0.x leaves `error` empty: the notice points at the watcher's logs.
        var failed = Assert.Single(interpreter.OnStatus("build_complete", [s_api, s_worker], false, null));
        Assert.Equal(WatchNoticeKind.BuildFailed, failed.Kind);
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.Equal("❌ Initial build failed: open 'dotnet-watch' Console logs in the dashboard, or run 'aspire logs dotnet-watch --tail 50'. Fix it and save; api, worker start once the build succeeds.", failed.Message);

        interpreter.OnStatus("building", [s_api, s_worker], null, null);
        var ready = Assert.Single(interpreter.OnStatus("build_complete", [s_api, s_worker], true, null));
        Assert.Equal(WatchNoticeKind.Watching, ready.Kind);
        Assert.StartsWith("✅ Build succeeded. 👀 Watching api, worker", ready.Message);
    }

    [Fact]
    public void BuildFailureKeepsRawProtocolDiagnosticsOutOfTheNotice()
    {
        var interpreter = CreateReady();
        Assert.Equal(WatchNoticeKind.Rebuilding, Assert.Single(interpreter.OnStatus("building", [s_api], null, null)).Kind);

        var failed = Assert.Single(interpreter.OnStatus("build_complete", [s_api], false, null,
            "Program.cs(8,43): error CS0117\n[red]RAW WATCHER DIAGNOSTIC[/]\u001b[31m"));
        Assert.Equal("❌ Build failed for api: open 'dotnet-watch' Console logs in the dashboard, or run 'aspire logs dotnet-watch --tail 50'. Fix it and save.", failed.Message);

        var fixedNotice = Assert.Single(interpreter.OnStatus("build_complete", [s_api], true, null));
        Assert.Equal(WatchNoticeKind.BuildSucceeded, fixedNotice.Kind);
        Assert.Equal("✅ Build succeeded for api.", fixedNotice.Message);
    }

    [Fact]
    public void RudeEditReportsRebuildAndRestart()
    {
        var interpreter = CreateReady();

        Assert.Equal("⌚ Rebuilding api…", Assert.Single(interpreter.OnStatus("building", [s_api], null, null)).Message);
        Assert.Empty(interpreter.OnStatus("build_complete", [s_api], true, null));
        Assert.Empty(interpreter.OnStatus("hot_reload_applied", [], null, null));

        // `rebuild` (the Restart command) lists the project once per process.
        var restarting = Assert.Single(interpreter.OnStatus("restarting", [s_api, s_api], null, null));
        Assert.Equal(WatchNoticeKind.Restarting, restarting.Kind);
        Assert.Equal("🔄 Restarting api…", restarting.Message);
        Assert.Equal([s_api], restarting.ProjectPaths);
        Assert.Empty(interpreter.OnStatus("process_started", [s_api], null, null));

        // Nothing running to restart.
        Assert.Empty(interpreter.OnStatus("restarting", [], null, null));
    }

    [Fact]
    public void CrashAndRelaunch()
    {
        var interpreter = CreateReady();

        var crashed = Assert.Single(interpreter.OnStatus("process_exited", [s_api], null, 3));
        Assert.Equal(WatchNoticeKind.Crashed, crashed.Kind);
        Assert.Equal(LogLevel.Error, crashed.Level);
        Assert.Equal("💥 api exited with code 3. Save a source change or restart the complete Aspire run to relaunch it; Restart requires a running application.", crashed.Message);

        // The crashed project is relaunched rather than patched; the other one is still hot reloaded.
        var updated = Assert.Single(interpreter.OnStatus("hot_reload_applied", [s_api, s_worker], null, null));
        Assert.Equal([s_worker], updated.ProjectPaths);

        var relaunched = Assert.Single(interpreter.OnStatus("process_started", [s_api], null, null));
        Assert.Equal(WatchNoticeKind.Relaunched, relaunched.Kind);
        Assert.Equal("▶️ api is running again.", relaunched.Message);
        Assert.Empty(interpreter.OnStatus("process_started", [s_api], null, null));
        Assert.Equal(WatchNoticeKind.HotReloadApplied, Assert.Single(interpreter.OnStatus("hot_reload_applied", [s_api], null, null)).Kind);
    }

    [Fact]
    public void UnknownProjectIsNamedByItsFile()
    {
        var other = Path.Combine(s_root, "Other", "Other.csproj");
        var notice = Assert.Single(CreateReady().OnStatus("hot_reload_applied", [other], null, null));
        Assert.Contains("for Other.", notice.Message);
    }

    [Fact]
    public void FailureDetailPointsToTheExactOwningLog()
    {
        Assert.Equal("open 'dotnet-watch-api' Console logs in the dashboard, or run 'aspire logs dotnet-watch-api --tail 50'",
            DotnetWatchEventInterpreter.FailureDetail("dotnet-watch-api"));
    }
}
