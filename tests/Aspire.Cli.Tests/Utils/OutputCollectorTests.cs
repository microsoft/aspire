// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class OutputCollectorTests
{
    [Fact]
    public async Task OutputCollector_ThreadSafety_MultipleThreadsAddingLines()
    {
        // Arrange
        var collector = new OutputCollector();
        const int threadCount = 10;
        const int linesPerThread = 100;
        var tasks = new Task[threadCount];

        // Act - Start multiple threads that add lines concurrently
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < linesPerThread; j++)
                {
                    if (j % 2 == 0)
                    {
                        collector.AppendOutput($"stdout-thread{threadId}-line{j}");
                    }
                    else
                    {
                        collector.AppendError($"stderr-thread{threadId}-line{j}");
                    }
                }
            });
        }

        await Task.WhenAll(tasks).DefaultTimeout();

        // Assert - Should have all lines without any exceptions
        var lines = collector.GetLines().ToList();
        Assert.Equal(threadCount * linesPerThread, lines.Count);

        // Check that we have both stdout and stderr entries
        var stdoutLines = lines.Where(l => l.Stream == OutputLineStream.StdOut).ToList();
        var stderrLines = lines.Where(l => l.Stream == OutputLineStream.StdErr).ToList();

        Assert.Equal(threadCount * linesPerThread / 2, stdoutLines.Count);
        Assert.Equal(threadCount * linesPerThread / 2, stderrLines.Count);
    }

    [Fact]
    public void OutputCollector_GetLines_ReturnsSnapshotNotLiveReference()
    {
        // Arrange
        var collector = new OutputCollector();
        collector.AppendOutput("initial line");

        // Act
        var snapshot = collector.GetLines().ToList();
        collector.AppendOutput("added after snapshot");

        // Assert - Snapshot should not be affected by subsequent additions
        Assert.Single(snapshot);
        Assert.Equal("initial line", snapshot[0].Line);
        Assert.Equal(OutputLineStream.StdOut, snapshot[0].Stream);

        // New call should include the additional line
        var newSnapshot = collector.GetLines().ToList();
        Assert.Equal(2, newSnapshot.Count);
    }

    [Fact]
    public async Task OutputCollector_ConcurrentReadWrite_ShouldNotCrash()
    {
        // Arrange
        var collector = new OutputCollector();
        var readerTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                _ = collector.GetLines().ToList();
                await Task.Delay(1);
            }
        });

        var writerTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                collector.AppendOutput($"line {i}");
                await Task.Delay(1);
            }
        });

        // Act & Assert - Should complete without exceptions
        await Task.WhenAll(readerTask, writerTask).DefaultTimeout();

        // Verify final state
        var finalLines = collector.GetLines().ToList();
        Assert.Equal(100, finalLines.Count);
    }
}
