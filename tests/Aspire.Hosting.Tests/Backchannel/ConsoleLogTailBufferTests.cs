// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.ConsoleLogs;

namespace Aspire.Hosting.Tests.Backchannel;

public class ConsoleLogTailBufferTests
{
    [Fact]
    public void Items_KeepsSystemLineThatArrivesBeforeOlderApplicationOutput()
    {
        // On the app host, process output and [sys] lines are read from separate streams, so the
        // exit event (t3) can arrive before older application output (t2).
        var buffer = CreateServerBuffer(tail: 1);
        buffer.Add("2026-01-01T00:00:01.0000000Z app line 1");
        buffer.Add("2026-01-01T00:00:03.0000000Z [sys] Process exited");
        buffer.Add("2026-01-01T00:00:02.0000000Z app line 2");

        Assert.Equal(
            [
                "2026-01-01T00:00:02.0000000Z app line 2",
                "2026-01-01T00:00:03.0000000Z [sys] Process exited"
            ],
            buffer.Items);
    }

    [Fact]
    public void Items_TrimsSystemLineThatArrivesLateButIsOlderThanTheWindow()
    {
        var buffer = CreateServerBuffer(tail: 1);
        buffer.Add("2026-01-01T00:00:02.0000000Z app line 1");
        buffer.Add("2026-01-01T00:00:03.0000000Z app line 2");
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");

        Assert.Equal(["2026-01-01T00:00:03.0000000Z app line 2"], buffer.Items);
    }

    [Fact]
    public void Items_ReturnsLastApplicationLinesInTimestampOrder()
    {
        var buffer = CreateServerBuffer(tail: 2);
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");
        buffer.Add("2026-01-01T00:00:04.0000000Z app line 4");
        buffer.Add("2026-01-01T00:00:02.0000000Z app line 2");
        buffer.Add("2026-01-01T00:00:03.0000000Z app line 3");

        Assert.Equal(
            [
                "2026-01-01T00:00:03.0000000Z app line 3",
                "2026-01-01T00:00:04.0000000Z app line 4"
            ],
            buffer.Items);
    }

    [Fact]
    public void Items_ReturnsEveryLineWhenThereAreFewerApplicationLinesThanTheTail()
    {
        var buffer = CreateServerBuffer(tail: 5);
        buffer.Add("2026-01-01T00:00:02.0000000Z app line 1");
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");
        buffer.Add("2026-01-01T00:00:03.0000000Z [sys] Process exited");

        Assert.Equal(
            [
                "2026-01-01T00:00:01.0000000Z [sys] Starting process...",
                "2026-01-01T00:00:02.0000000Z app line 1",
                "2026-01-01T00:00:03.0000000Z [sys] Process exited"
            ],
            buffer.Items);
    }

    [Fact]
    public void Items_UsesArrivalOrderForLinesWithoutTimestamps()
    {
        var buffer = new ConsoleLogTailBuffer<string>(2, ConsoleLogTail.IsSystemLog);
        buffer.Add("[sys] Starting process...");
        buffer.Add("app line 1");
        buffer.Add("app line 2");
        buffer.Add("[sys] Process exited");
        buffer.Add("app line 3");

        Assert.Equal(["app line 2", "[sys] Process exited", "app line 3"], buffer.Items);
    }

    [Fact]
    public void Items_KeepsUntimestampedLineAfterOlderSystemLine()
    {
        // A late [sys] line with an older timestamp mustn't move an untimestamped line that arrives after it
        // back in time. The CLI keeps untimestamped lines at the end, so this line is the latest.
        var buffer = CreateServerBuffer(tail: 1);
        buffer.Add("2026-01-01T00:00:05.0000000Z app line 1");
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");
        buffer.Add("app line 2");

        Assert.Equal(["app line 2"], buffer.Items);
    }

    [Fact]
    public void Items_PrunesOldSystemLinesWithoutDroppingOnesInTheWindow()
    {
        var buffer = CreateServerBuffer(tail: 1);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        buffer.Add($"{start.AddSeconds(5000):o} app line");
        for (var i = 0; i < 3000; i++)
        {
            buffer.Add($"{start.AddSeconds(i):o} [sys] restart {i}");
        }

        buffer.Add($"{start.AddSeconds(5001):o} [sys] Process exited");

        Assert.Equal(
            [
                $"{start.AddSeconds(5000):o} app line",
                $"{start.AddSeconds(5001):o} [sys] Process exited"
            ],
            buffer.Items);
    }

    [Fact]
    public void Add_AcceptsMaximumTailWithoutPreallocating()
    {
        var buffer = CreateServerBuffer(tail: int.MaxValue);
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");
        buffer.Add("2026-01-01T00:00:02.0000000Z app line 1");

        Assert.Equal(
            [
                "2026-01-01T00:00:01.0000000Z [sys] Starting process...",
                "2026-01-01T00:00:02.0000000Z app line 1"
            ],
            buffer.Items);
    }

    [Fact]
    public void Add_DiscardsSystemLinesOlderThanAFullWindow()
    {
        var buffer = CreateServerBuffer(tail: 1);
        buffer.Add("2026-01-01T00:00:05.0000000Z app line");
        buffer.Add("2026-01-01T00:00:01.0000000Z [sys] Starting process...");

        Assert.Equal(0, buffer.RetainedSystemLineCount);
        Assert.Equal(["2026-01-01T00:00:05.0000000Z app line"], buffer.Items);
    }

    [Fact]
    public void Add_PrunesSystemLinesWhenTheWindowAdvances()
    {
        var buffer = CreateServerBuffer(tail: 1);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        buffer.Add($"{start:o} app line 0");

        // Many restarts after the last application line are all inside the window and must be kept.
        const int restarts = 50_000;
        for (var i = 1; i <= restarts; i++)
        {
            buffer.Add($"{start.AddMilliseconds(i):o} [sys] restart {i}");
        }

        Assert.Equal(restarts, buffer.RetainedSystemLineCount);
        Assert.Equal(restarts + 1, buffer.Items.Count());

        // A newer application line moves the window past every restart, so they're pruned.
        buffer.Add($"{start.AddMilliseconds(restarts + 1):o} app line 1");

        Assert.Equal(0, buffer.RetainedSystemLineCount);
        Assert.Equal([$"{start.AddMilliseconds(restarts + 1):o} app line 1"], buffer.Items);
    }

    private static ConsoleLogTailBuffer<string> CreateServerBuffer(int tail) =>
        new(tail, ConsoleLogTail.IsSystemLogWithOptionalTimestamp, ConsoleLogTail.GetTimestamp);
}
