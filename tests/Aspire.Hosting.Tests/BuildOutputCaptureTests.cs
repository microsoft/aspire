// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

public class BuildOutputCaptureTests
{
    [Fact]
    public void LateAndConcurrentSubscribersReceiveCurrentAttemptOnce()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var firstLines = new List<LogLine>();
        var secondLines = new List<LogLine>();
        using var firstLogs = logs.Subscribe("project-first", firstLines.AddRange);
        using var secondLogs = logs.Subscribe("project-second", secondLines.AddRange);
        using var first = capture.Subscribe("builder", ["project-first"], includeCompleted: false);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid-1");
        var attempt = capture.GetAttempt("builder", "uid-1");
        Assert.NotNull(attempt);
        BuildOutputCapture.AddFollow(attempt, [Entry("first")]);

        using var duplicate = capture.Subscribe("builder", ["project-first"], includeCompleted: false);
        using var second = capture.Subscribe("builder", ["project-second"], includeCompleted: false);
        BuildOutputCapture.AddFollow(attempt, [Entry("second")]);

        Assert.Collection(firstLines,
            line => Assert.EndsWith("[build] first", line.Content),
            line => Assert.EndsWith("[build] second", line.Content));
        Assert.Collection(secondLines,
            line => Assert.EndsWith("[build] first", line.Content),
            line => Assert.EndsWith("[build] second", line.Content));
    }

    [Fact]
    public void RetryRejectsOldIncarnationButKeepsIdenticalNewLines()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid-1");
        var firstAttempt = capture.GetAttempt("builder", "uid-1");
        Assert.NotNull(firstAttempt);
        BuildOutputCapture.AddFollow(firstAttempt, [Entry("same")]);
        BuildOutputCapture.Complete(firstAttempt);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid-1");
        Assert.Null(capture.GetAttempt("builder", "uid-1"));
        capture.Observe("builder", "uid-2");
        var secondAttempt = capture.GetAttempt("builder", "uid-2");
        Assert.NotNull(secondAttempt);
        BuildOutputCapture.AddFollow(firstAttempt, [Entry("late old output")]);
        BuildOutputCapture.AddFollow(secondAttempt, [Entry("same")]);

        Assert.Collection(lines,
            line => Assert.EndsWith("[build] same", line.Content),
            line => Assert.EndsWith("[build] same", line.Content));
    }

    [Fact]
    public void TerminalSnapshotAndFollowOverlapAreDeduplicatedByOccurrence()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid");
        var attempt = capture.GetAttempt("builder", "uid");
        Assert.NotNull(attempt);
        BuildOutputCapture.AddFollow(attempt, [Entry("repeat"), Entry("repeat"), Entry("first")]);
        BuildOutputCapture.AddTerminal(attempt, [Entry("repeat"), Entry("repeat"), Entry("first"), Entry("last")]);
        BuildOutputCapture.AddFollow(attempt, [Entry("repeat"), Entry("repeat"), Entry("first"), Entry("last")]);

        Assert.Collection(lines,
            line => Assert.EndsWith("[build] repeat", line.Content),
            line => Assert.EndsWith("[build] repeat", line.Content),
            line => Assert.EndsWith("[build] first", line.Content),
            line => Assert.EndsWith("[build] last", line.Content));
    }

    [Fact]
    public void TerminalOverlapDoesNotSuppressLaterIdenticalLines()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid");
        var attempt = capture.GetAttempt("builder", "uid");
        Assert.NotNull(attempt);
        BuildOutputCapture.AddTerminal(attempt, [Entry("snapshot")]);
        BuildOutputCapture.AddFollow(attempt, [Entry("new output")]);
        BuildOutputCapture.AddFollow(attempt, [Entry("snapshot")]);

        Assert.Collection(lines,
            line => Assert.EndsWith("[build] snapshot", line.Content),
            line => Assert.EndsWith("[build] new output", line.Content),
            line => Assert.EndsWith("[build] snapshot", line.Content));
    }

    [Fact]
    public void TerminalSnapshotDoesNotReplayOutputOutsideTheBoundedBuffer()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);
        var entries = Enumerable.Range(0, 10_001).Select(i => Entry($"line {i}")).ToArray();

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid");
        var attempt = capture.GetAttempt("builder", "uid");
        Assert.NotNull(attempt);
        BuildOutputCapture.AddFollow(attempt, entries);
        BuildOutputCapture.AddTerminal(attempt, entries);

        Assert.Equal(10_001, lines.Count);
        Assert.Equal(10_000, attempt.Buffer.Count);
    }

    [Fact]
    public void DisposingOneSubscriberDoesNotDisconnectAnother()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        var first = capture.Subscribe("builder", ["project"], includeCompleted: false);
        var second = capture.Subscribe("builder", ["project"], includeCompleted: false);
        first.Dispose();

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid");
        var attempt = capture.GetAttempt("builder", "uid");
        Assert.NotNull(attempt);
        BuildOutputCapture.AddFollow(attempt, [Entry("visible")]);
        second.Dispose();
        BuildOutputCapture.AddFollow(attempt, [Entry("not forwarded")]);

        Assert.Collection(lines, line => Assert.EndsWith("[build] visible", line.Content));
    }

    [Fact]
    public void InProcessFailureOutputBelongsOnlyToTheActiveAttempt()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        using var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);
        var builderLogger = logs.GetLogger("builder");

        builderLogger.LogError("before build");
        capture.BeginAttempt("builder");
        builderLogger.LogError("configuration failed");
        capture.CompleteCurrent("builder");
        builderLogger.LogError("after build");
        capture.BeginAttempt("builder");
        builderLogger.LogError("retry failed");

        Assert.Collection(lines,
            line => Assert.EndsWith("[build] configuration failed", line.Content),
            line => Assert.EndsWith("[build] retry failed", line.Content));
    }

    [Fact]
    public void DisposingWriterRejectsLateOutputFromItsAttempt()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        var registration = capture.Register("builder");
        var lines = new List<LogLine>();
        using var targetLogs = logs.Subscribe("project", lines.AddRange);
        using var subscription = capture.Subscribe("builder", ["project"], includeCompleted: false);

        capture.BeginAttempt("builder");
        capture.Observe("builder", "uid");
        var attempt = capture.GetAttempt("builder", "uid");
        Assert.NotNull(attempt);

        registration.Dispose();
        BuildOutputCapture.AddFollow(attempt, [Entry("late output")]);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task DisposingWriterRejectsRacingSubscription()
    {
        using var logs = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var capture = logs.BuildOutputCapture;
        var registration = capture.Register("builder");
        var enumeratingTargets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeEnumeration = new ManualResetEventSlim();
        var targets = new[] { "project" }.Select(target =>
        {
            enumeratingTargets.TrySetResult();
            resumeEnumeration.Wait();
            return target;
        });

        var subscribe = Task.Run(() => capture.Subscribe("builder", targets, includeCompleted: false));
        try
        {
            await enumeratingTargets.Task.WaitAsync(TestContext.Current.CancellationToken);
            registration.Dispose();
        }
        finally
        {
            resumeEnumeration.Set();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscribe);
    }

    private static LogEntry Entry(string content) => LogEntry.Create(timestamp: null, logMessage: content, isErrorMessage: false);
}
