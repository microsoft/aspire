// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests.Watch;

/// <summary>
/// SPIKE: replays output recorded from the REAL watch server (by <see cref="DotnetWatchContractTests"/>, see
/// Watch/Recordings) through <see cref="DotnetWatchEventInterpreter"/>, so every console literal the notices depend on
/// (<see cref="DotnetWatchOutputPatterns"/>) is covered by real output in regular CI.
/// </summary>
public class DotnetWatchRecordedOutputTests
{
    private static readonly string s_root = OperatingSystem.IsWindows() ? @"C:\awc" : "/tmp/awc";
    private static readonly string s_project = Path.Combine(s_root, "ContractApp", "ContractApp.csproj");

    [Fact]
    public void EditLifecycleRecordingProducesTheExpectedNotices()
    {
        var observations = WatchRecording.Load("edit-lifecycle", s_root);

        var notices = WatchRecording.Interpret(observations, s_project, "contract", s_root);

        DotnetWatchContractTests.AssertLifecycleNotices(notices);
        Assert.All(notices, n => Assert.Equal([s_project], n.ProjectPaths));
    }

    [Fact]
    public void RelativeChangedFilePathsAreAttributedToTheOwningProject()
    {
        // The watcher logs files under its working directory relative to it ("File updated: ./ContractApp/Greeting.cs").
        var other = Path.Combine(s_root, "Other", "Other.csproj");
        var interpreter = new DotnetWatchEventInterpreter([(s_project, "contract"), (other, "other")], s_root);
        var notices = new List<WatchNotice>();
        foreach (var observation in WatchRecording.Load("edit-lifecycle", s_root).Where(o => o.Kind == WatchObservationKind.Output))
        {
            notices.AddRange(interpreter.OnLogLine(observation.Text));
        }

        var applied = notices.Where(n => n.Kind == WatchNoticeKind.HotReloadApplied).ToArray();
        Assert.NotEmpty(applied);
        Assert.All(applied, n => Assert.Equal([s_project], n.ProjectPaths));
        Assert.Contains(applied, n => n.Message.Contains("(Greeting.cs)", StringComparison.Ordinal));
    }

    [Fact]
    public void EditLifecycleRecordingCoversEveryStatusEventType()
    {
        var types = WatchRecording.Load("edit-lifecycle", s_root).Select(o => o.StatusType).OfType<string>().ToHashSet();

        Assert.Superset(new HashSet<string> { "building", "build_complete", "hot_reload_applied", "restarting", "process_exited", "process_started" }, types);
    }

    [Fact]
    public void EditLifecycleRecordingMatchesEveryConsolePattern()
    {
        var lines = WatchRecording.Load("edit-lifecycle", s_root).Where(o => o.Kind == WatchObservationKind.Output).Select(o => o.Text).ToArray();

        foreach (var (name, matches) in DotnetWatchOutputPatterns.All)
        {
            Assert.True(lines.Any(matches), $"No line in the recorded watcher output matches {name}; the watcher's console text may have changed.");
        }
    }

    [Fact]
    public void LaunchSpecRecordingStartsTheProcessOnce()
    {
        var observations = WatchRecording.Load("launch-spec", s_root);

        Assert.Single(observations, o => o.StatusType == "process_started");
        Assert.DoesNotContain(WatchRecording.Interpret(observations, s_project, "contract", s_root), n => n.Kind is WatchNoticeKind.BuildFailed or WatchNoticeKind.CompileError or WatchNoticeKind.Crashed);
    }
}
