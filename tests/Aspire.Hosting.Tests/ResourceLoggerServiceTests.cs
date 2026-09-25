// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Hosting.Tests.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class ResourceLoggerServiceTests
{
    [Fact]
    public async Task AddingResourceLoggerAnnotationAllowsLogging()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        var subsLoop = WatchForSubscribers(service);

        var logsEnumerator1 = service.WatchAsync(testResource).GetAsyncEnumerator();
        var logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator1, 2);

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        // Log
        logger.LogInformation("Hello, world!");
        logger.LogError("Hello, error!");

        // Wait for logs to be read
        var allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", allLogs[0].Content);
        Assert.False(allLogs[0].IsErrorMessage);

        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", allLogs[1].Content);
        Assert.True(allLogs[1].IsErrorMessage);

        // New sub should get the previous logs
        subsLoop = WatchForSubscribers(service);
        var logsEnumerator2 = service.WatchAsync(testResource).GetAsyncEnumerator();
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator2, 2);
        await subsLoop.DefaultTimeout();
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal(2, allLogs.Count);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", allLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", allLogs[1].Content);

        await logsEnumerator1.DisposeAsync().DefaultTimeout();
        await logsEnumerator2.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task StreamingLogsCancelledAfterComplete()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        var subsLoop = WatchForSubscribers(service);
        var logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(service, 2, testResource);

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        logger.LogInformation("Hello, world!");
        logger.LogError("Hello, error!");

        // Complete the log stream & log afterwards
        service.Complete(testResource);
        logger.LogInformation("The third log");

        // Wait for logs to be read
        var allLogs = await logsLoop.DefaultTimeout();

        Assert.Collection(allLogs,
            l => Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", l.Content),
            l => Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", l.Content));

        // The backlog should be cleared once there are no subscribers.
        Assert.Empty(service.GetResourceLoggerState(testResource.Name).GetBacklogSnapshot());

        // New sub should replay logs again.
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(service, 100, testResource);
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Collection(allLogs,
            l => Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", l.Content),
            l => Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", l.Content));
    }

    [Fact]
    public async Task SecondSubscriberGetsBacklog()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        var subsLoop = WatchForSubscribers(service);
        var logsEnumerator1 = service.WatchAsync(testResource).GetAsyncEnumerator();
        var logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator1, 2);

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        // Log
        logger.LogInformation("Hello, world!");
        logger.LogError("Hello, error!");

        // Wait for logs to be read
        var allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", allLogs[0].Content);
        Assert.False(allLogs[0].IsErrorMessage);

        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", allLogs[1].Content);
        Assert.True(allLogs[1].IsErrorMessage);

        // New sub should get the previous logs (backlog)
        subsLoop = WatchForSubscribers(service);
        var logsEnumerator2 = service.WatchAsync(testResource).GetAsyncEnumerator();
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator2, 2);
        await subsLoop.DefaultTimeout();
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal(2, allLogs.Count);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", allLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", allLogs[1].Content);

        // Clear the backlog and ensure new subs only get new logs
        service.ClearBacklog(testResource.Name);

        subsLoop = WatchForSubscribers(service);
        var logsEnumerator3 = service.WatchAsync(testResource).GetAsyncEnumerator();
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator3, 1);
        await subsLoop.DefaultTimeout();
        logger.LogInformation("The third log");
        allLogs = await logsLoop.DefaultTimeout();

        // The backlog should be cleared so only new logs are received
        Assert.Single(allLogs);
        Assert.Equal("2000-12-29T20:59:59.0000000Z The third log", allLogs[0].Content);
    }

    [Fact]
    public async Task InMemoryLogsPreservedBetweenWatches()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        // Log before watching
        logger.LogInformation("Before watching!");

        var subsLoop = WatchForSubscribers(service);
        var logsEnumerator1 = service.WatchAsync(testResource).GetAsyncEnumerator();
        var logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator1, 1);

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        // Read before watching log
        var allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal("2000-12-29T20:59:59.0000000Z Before watching!", allLogs[0].Content);
        Assert.False(allLogs[0].IsErrorMessage);

        // Log while watching
        logger.LogInformation("While watching!");

        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator1, 1);
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal("2000-12-29T20:59:59.0000000Z While watching!", allLogs[0].Content);
        Assert.False(allLogs[0].IsErrorMessage);

        // New sub should get the previous logs (backlog)
        subsLoop = WatchForSubscribers(service);
        var logsEnumerator2 = service.WatchAsync(testResource).GetAsyncEnumerator();
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator2, 2);
        await subsLoop.DefaultTimeout();
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal(2, allLogs.Count);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Before watching!", allLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z While watching!", allLogs[1].Content);

        await logsEnumerator1.DisposeAsync().DefaultTimeout();
        await logsEnumerator2.DisposeAsync().DefaultTimeout();

        logger.LogInformation("After watching!");

        // The backlog should be cleared once there are no subscribers.
        Assert.Empty(service.GetResourceLoggerState(testResource.Name).GetBacklogSnapshot());

        subsLoop = WatchForSubscribers(service);
        var logsEnumerator3 = service.WatchAsync(testResource).GetAsyncEnumerator();
        logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator3, 4);
        await subsLoop.DefaultTimeout();
        logger.LogInformation("While watching again!");
        allLogs = await logsLoop.DefaultTimeout();

        Assert.Equal(4, allLogs.Count);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Before watching!", allLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z While watching!", allLogs[1].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z After watching!", allLogs[2].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z While watching again!", allLogs[3].Content);
    }

    [Fact]
    public async Task MultipleInstancesLogsToAll()
    {
        var testResource = new TestResource("myResource");
        testResource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("instance0", "0", 0), new DcpInstance("instance1", "1", 1)]));

        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        var subsLoop = WatchForSubscribers(service);

        var logsEnumerator = service.WatchAsync(testResource).GetAsyncEnumerator();
        var logsLoop = ConsoleLoggingTestHelpers.WatchForLogsAsync(logsEnumerator, 4);

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        // Log
        logger.LogInformation("Hello, world!");
        logger.LogError("Hello, error!");

        Assert.True(service.Loggers.ContainsKey("instance0"));
        Assert.True(service.Loggers.ContainsKey("instance1"));

        // Wait for logs to be read
        var allLogs = await logsLoop.DefaultTimeout();

        var sortedLogs = allLogs.OrderBy(l => l.LineNumber).ToList();

        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", sortedLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", sortedLogs[1].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", sortedLogs[2].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", sortedLogs[3].Content);

        service.Complete(testResource);

        Assert.False(await logsEnumerator.MoveNextAsync().DefaultTimeout());

        await logsEnumerator.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task MultipleInstancesGetLogForAll()
    {
        var testResource = new TestResource("myResource");
        testResource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("instance0", "0", 0), new DcpInstance("instance1", "1", 1)]));

        var consoleLogsChannel0 = Channel.CreateUnbounded<IReadOnlyList<LogEntry>>();
        consoleLogsChannel0.Writer.TryWrite([LogEntry.Create(timestamp: null, logMessage: "instance0!", isErrorMessage: false)]);
        consoleLogsChannel0.Writer.Complete();

        var consoleLogsChannel1 = Channel.CreateUnbounded<IReadOnlyList<LogEntry>>();
        consoleLogsChannel1.Writer.TryWrite([LogEntry.Create(timestamp: null, logMessage: "instance1!", isErrorMessage: false)]);
        consoleLogsChannel1.Writer.Complete();

        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

        // Get logger before SetConsoleLogsService is called so that we test there is no bad state stored on the resource logger instance.
        var logger = service.GetLogger(testResource);

        service.SetConsoleLogsService(new TestConsoleLogsService(name => name switch
            {
                "instance0" => consoleLogsChannel0,
                "instance1" => consoleLogsChannel1,
                string n => throw new InvalidOperationException($"Unexpected {n}")
            }));

        // Log
        logger.LogInformation("Hello, world!");
        logger.LogError("Hello, error!");

        Assert.True(service.Loggers.ContainsKey("instance0"));
        Assert.True(service.Loggers.ContainsKey("instance1"));

        // Wait for logs to be read
        var allLogs = new List<LogLine>();
        await foreach (var logs in service.GetAllAsync(testResource).DefaultTimeout())
        {
            allLogs.AddRange(logs);
        }

        var sortedLogs = allLogs.OrderBy(l => l.LineNumber).ToList();

        Assert.Equal(6, sortedLogs.Count);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", sortedLogs[0].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", sortedLogs[1].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", sortedLogs[2].Content);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, error!", sortedLogs[3].Content);

        var consoleLogsSourceLogs = sortedLogs.Slice(4, 2).ToList();
        Assert.Contains(consoleLogsSourceLogs, l => l.Content == "instance0!");
        Assert.Contains(consoleLogsSourceLogs, l => l.Content == "instance1!");
    }

    [Fact]
    public async Task AddLogEntries_OverlappingSnapshotAndFollow_DedupesByOccurrence()
    {
        await Task.Run(() =>
        {
            const string resourceName = "myResource";
            var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
            var logLines = new List<LogLine>();

            using var subscription = service.Subscribe(resourceName, logLines.AddRange);

            service.AddLogEntries(resourceName,
                [
                    CreateLogEntry("snapshot-before-overlap"),
                    CreateLogEntry("overlap"),
                    CreateLogEntry("snapshot-after-overlap")
                ],
                inMemorySource: false,
                skipExisting: true);
            service.AddLogEntries(resourceName,
                [
                    CreateLogEntry("overlap"),
                    CreateLogEntry("follow-only")
                ],
                inMemorySource: false,
                skipExisting: true);

            Assert.Collection(logLines,
                l => { Assert.Equal(1, l.LineNumber); Assert.Equal("snapshot-before-overlap", l.Content); },
                l => { Assert.Equal(2, l.LineNumber); Assert.Equal("overlap", l.Content); },
                l => { Assert.Equal(3, l.LineNumber); Assert.Equal("snapshot-after-overlap", l.Content); },
                l => { Assert.Equal(4, l.LineNumber); Assert.Equal("follow-only", l.Content); });
        }).DefaultTimeout();
    }

    [Fact]
    public async Task AddLogEntries_RepeatedIdenticalLines_ArePreservedWhenDedupingOverlap()
    {
        await Task.Run(() =>
        {
            const string resourceName = "myResource";
            var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
            var logLines = new List<LogLine>();

            using var subscription = service.Subscribe(resourceName, logLines.AddRange);

            service.AddLogEntries(resourceName,
                [
                    CreateLogEntry("same"),
                    CreateLogEntry("same")
                ],
                inMemorySource: false,
                skipExisting: true);
            service.AddLogEntries(resourceName,
                [
                    CreateLogEntry("same"),
                    CreateLogEntry("same"),
                    CreateLogEntry("same")
                ],
                inMemorySource: false,
                skipExisting: true);

            Assert.Collection(logLines,
                l => { Assert.Equal(1, l.LineNumber); Assert.Equal("same", l.Content); },
                l => { Assert.Equal(2, l.LineNumber); Assert.Equal("same", l.Content); },
                l => { Assert.Equal(3, l.LineNumber); Assert.Equal("same", l.Content); });
        }).DefaultTimeout();
    }

    [Fact]
    public async Task Subscribe_DeliversBacklogBeforeLiveLogs_WithContinuousLineNumbers()
    {
        const string resourceName = "myResource";
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logLines = new List<LogLine>();
        var logLinesLock = new object();
        var subscribeRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBacklogDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        service.AddLogEntries(resourceName, [CreateLogEntry("backlog")], inMemorySource: true, skipExisting: false);
        var loggerState = service.GetResourceLoggerState(resourceName);
        loggerState.SynchronousSubscribeRegistered = () =>
        {
            subscribeRegistered.TrySetResult();
            continueBacklogDelivery.Task.DefaultTimeout().GetAwaiter().GetResult();
        };

        IDisposable? subscription = null;
        var subscribeTask = Task.Run(() =>
        {
            subscription = service.Subscribe(resourceName, batch =>
            {
                lock (logLinesLock)
                {
                    logLines.AddRange(batch);
                }
            });
        });

        try
        {
            await subscribeRegistered.Task.DefaultTimeout();

            service.AddLogEntries(resourceName, [CreateLogEntry("live-during-backlog-delivery")], inMemorySource: false, skipExisting: false);

            continueBacklogDelivery.SetResult();
            await subscribeTask.DefaultTimeout();

            service.AddLogEntries(resourceName, [CreateLogEntry("live-after-subscribe")], inMemorySource: false, skipExisting: false);

            lock (logLinesLock)
            {
                Assert.Collection(logLines,
                    l => { Assert.Equal(1, l.LineNumber); Assert.Equal("backlog", l.Content); },
                    l => { Assert.Equal(2, l.LineNumber); Assert.Equal("live-during-backlog-delivery", l.Content); },
                    l => { Assert.Equal(3, l.LineNumber); Assert.Equal("live-after-subscribe", l.Content); });
            }
        }
        finally
        {
            loggerState.SynchronousSubscribeRegistered = null;
            continueBacklogDelivery.TrySetResult();
            subscription?.Dispose();
        }
    }

    [Fact]
    public async Task WaitForCompletionAsync_CompletesWhenResourceLogStreamCompletes()
    {
        const string resourceName = "myResource";
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

        var completionTask = service.WaitForCompletionAsync(resourceName, CancellationToken.None);
        Assert.False(completionTask.IsCompleted);

        service.Complete(resourceName);

        await completionTask.DefaultTimeout();
        await service.WaitForCompletionAsync(resourceName, CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task HasActiveSubscribers_ReturnsFalseAfterCompletion()
    {
        await Task.Run(() =>
        {
            const string resourceName = "myResource";
            var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

            Assert.False(service.HasActiveSubscribers(resourceName));

            using var subscription = service.Subscribe(resourceName, _ => { });

            Assert.True(service.HasActiveSubscribers(resourceName));

            service.Complete(resourceName);

            Assert.False(service.HasActiveSubscribers(resourceName));
        }).DefaultTimeout();
    }

    [Fact]
    public async Task GetAllAsync_DoesNotReplayNonInMemoryEntries()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

        using (service.Subscribe(testResource.Name, _ => { }))
        {
            service.AddLogEntries(testResource.Name, [CreateLogEntry("dcp-snapshot-log")], inMemorySource: false, skipExisting: false);
        }

        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<LogEntry>>();
        consoleLogsChannel.Writer.TryWrite([CreateLogEntry("dcp-snapshot-log")]);
        consoleLogsChannel.Writer.Complete();

        service.SetConsoleLogsService(new TestConsoleLogsService(name => name == testResource.Name
            ? consoleLogsChannel
            : throw new InvalidOperationException($"Unexpected {name}")));

        var allLogs = new List<LogLine>();
        await foreach (var logs in service.GetAllAsync(testResource).DefaultTimeout())
        {
            allLogs.AddRange(logs);
        }

        var logLine = Assert.Single(allLogs);
        Assert.Equal("dcp-snapshot-log", logLine.Content);
    }

    [Fact]
    public async Task Subscribe_ConcurrentAddLogEntries_DeliversEachEntryOnceWithUniqueLineNumbers()
    {
        const string resourceName = "myResource";
        const int producerCount = 8;
        const int entriesPerProducer = 100;
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logLines = new List<LogLine>();
        var logLinesLock = new object();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = service.Subscribe(resourceName, batch =>
        {
            lock (logLinesLock)
            {
                logLines.AddRange(batch);
            }
        });

        var producerTasks = Enumerable.Range(0, producerCount).Select(async producerIndex =>
        {
            await start.Task.DefaultTimeout();

            for (var entryIndex = 0; entryIndex < entriesPerProducer; entryIndex++)
            {
                service.AddLogEntries(
                    resourceName,
                    [CreateConcurrentLogEntry(producerIndex, entryIndex)],
                    inMemorySource: false,
                    skipExisting: false);
            }
        });

        start.SetResult();
        await Task.WhenAll(producerTasks).DefaultTimeout();

        lock (logLinesLock)
        {
            Assert.Equal(producerCount * entriesPerProducer, logLines.Count);
            Assert.Equal(logLines.Count, logLines.Select(l => l.Content).Distinct().Count());
            Assert.Equal(Enumerable.Range(1, logLines.Count), logLines.Select(l => l.LineNumber).Order());
        }
    }

    [Fact]
    public async Task AddLogEntries_ConcurrentOverlappingBatches_DedupesByOccurrence()
    {
        const string resourceName = "myResource";
        const int producerCount = 16;
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logLines = new List<LogLine>();
        var logLinesLock = new object();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = service.Subscribe(resourceName, batch =>
        {
            lock (logLinesLock)
            {
                logLines.AddRange(batch);
            }
        });

        var producerTasks = Enumerable.Range(0, producerCount).Select(async _ =>
        {
            await start.Task.DefaultTimeout();

            service.AddLogEntries(
                resourceName,
                [
                    CreateLogEntry("same"),
                    CreateLogEntry("same"),
                    CreateLogEntry("unique")
                ],
                inMemorySource: false,
                skipExisting: true);
        });

        start.SetResult();
        await Task.WhenAll(producerTasks).DefaultTimeout();

        lock (logLinesLock)
        {
            Assert.Equal(3, logLines.Count);
            Assert.Equal(2, logLines.Count(l => l.Content == "same"));
            Assert.Single(logLines, l => l.Content == "unique");
            Assert.Equal([1, 2, 3], logLines.Select(l => l.LineNumber).Order());
        }
    }

    [Fact]
    public async Task SubscribeAndDispose_ConcurrentWithLogging_DoesNotThrowOrDeadlock()
    {
        const string resourceName = "myResource";
        const int subscriberCount = 8;
        const int subscribeIterations = 50;
        const int producerCount = 4;
        const int entriesPerProducer = 100;
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var exceptions = new ConcurrentQueue<Exception>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedByStableSubscription = 0;

        using var stableSubscription = service.Subscribe(resourceName, batch =>
        {
            foreach (var line in batch)
            {
                if (line.LineNumber <= 0)
                {
                    throw new InvalidOperationException("Expected positive line numbers.");
                }
            }

            Interlocked.Add(ref observedByStableSubscription, batch.Count);
        });

        var subscriberTasks = Enumerable.Range(0, subscriberCount).Select(async subscriberIndex =>
        {
            await start.Task.DefaultTimeout();

            try
            {
                for (var i = 0; i < subscribeIterations; i++)
                {
                    using var subscription = service.Subscribe(resourceName, batch =>
                    {
                        foreach (var line in batch)
                        {
                            if (line.LineNumber <= 0)
                            {
                                throw new InvalidOperationException($"Subscriber {subscriberIndex} observed non-positive line number.");
                            }
                        }
                    });

                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        var producerTasks = Enumerable.Range(0, producerCount).Select(async producerIndex =>
        {
            await start.Task.DefaultTimeout();

            try
            {
                for (var entryIndex = 0; entryIndex < entriesPerProducer; entryIndex++)
                {
                    service.AddLogEntries(
                        resourceName,
                        [CreateConcurrentLogEntry(producerIndex, entryIndex)],
                        inMemorySource: false,
                        skipExisting: false);
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        start.SetResult();
        await Task.WhenAll(subscriberTasks.Concat(producerTasks)).DefaultTimeout();

        Assert.Empty(exceptions);
        Assert.Equal(producerCount * entriesPerProducer, observedByStableSubscription);
    }

    [Fact]
    public async Task WatchAsyncCompletesOnDispose()
    {
        var testResource = new TestResource("myResource");
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var logger = service.GetLogger(testResource);

        var subsLoop = WatchForSubscribers(service);

        // Start watching logs in a background task
        var watchTask = Task.Run(async () =>
        {
            var logs = new List<LogLine>();
            await foreach (var batch in service.WatchAsync(testResource))
            {
                logs.AddRange(batch);
            }
            return (IReadOnlyList<LogLine>)logs;
        });

        // Wait for subscriber to be added
        await subsLoop.DefaultTimeout();

        // Log a message
        logger.LogInformation("Hello, world!");

        // Dispose the service - this should cause WatchAsync to complete
        service.Dispose();

        // The watch task should complete without waiting for more logs
        var allLogs = await watchTask.DefaultTimeout();

        Assert.Single(allLogs);
        Assert.Equal("2000-12-29T20:59:59.0000000Z Hello, world!", allLogs[0].Content);
    }

    [Fact]
    public async Task WatchAsyncCompletesOnDisposeForNonexistentResource()
    {
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

        var subsLoop = WatchForSubscribers(service);

        // Start watching logs for a resource that doesn't exist yet
        var watchTask = Task.Run(async () =>
        {
            var logs = new List<LogLine>();
            await foreach (var batch in service.WatchAsync("nonexistent"))
            {
                logs.AddRange(batch);
            }
            return (IReadOnlyList<LogLine>)logs;
        });

        // Wait for subscriber to be added - this proves the watch is running
        await subsLoop.DefaultTimeout();

        // Dispose the service - this should cause WatchAsync to complete
        service.Dispose();

        // The watch task should complete without waiting for more logs
        var allLogs = await watchTask.DefaultTimeout();

        Assert.Empty(allLogs);
    }

    [Fact]
    public async Task WatchAnySubscribersAsyncCompletesOnDispose()
    {
        var service = ConsoleLoggingTestHelpers.GetResourceLoggerService();

        // Create a TaskCompletionSource to signal when the watch has started
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start watching for subscribers in a background task
        var watchTask = Task.Run(async () =>
        {
            var subscribers = new List<LogSubscriber>();
            var isFirst = true;
            try
            {
                await foreach (var sub in service.WatchAnySubscribersAsync())
                {
                    if (isFirst)
                    {
                        watchStarted.TrySetResult();
                        isFirst = false;
                    }
                    subscribers.Add(sub);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the service is disposed
            }
            return subscribers;
        });

        // Trigger a subscriber event by starting a watch on a resource
        var logWatchEnumerator = service.WatchAsync("testResource").GetAsyncEnumerator();
        var moveNextTask = logWatchEnumerator.MoveNextAsync();

        // Wait for the first subscriber event to be received - this proves WatchAnySubscribersAsync is running
        await watchStarted.Task.DefaultTimeout();

        // Dispose the service - this should cause WatchAnySubscribersAsync to complete
        service.Dispose();

        // The watch task should complete
        var allSubscribers = await watchTask.DefaultTimeout();

        // Should have received at least one subscriber event before dispose
        // (may receive both subscribe and unsubscribe events depending on timing)
        Assert.NotEmpty(allSubscribers);
        Assert.True(allSubscribers[0].AnySubscribers);

        // Cleanup - the enumerator's MoveNextAsync should complete after dispose
        await moveNextTask.DefaultTimeout();

        await logWatchEnumerator.DisposeAsync().DefaultTimeout();
    }

    private sealed class TestResource(string name) : Resource(name)
    {

    }

    private static LogEntry CreateLogEntry(string content)
    {
        return LogEntry.Create(timestamp: null, content, isErrorMessage: false);
    }

    private static LogEntry CreateConcurrentLogEntry(int producerIndex, int entryIndex)
    {
        return CreateLogEntry($"concurrent log from producer {producerIndex}, entry {entryIndex}");
    }

    private static Task WatchForSubscribers(ResourceLoggerService service)
    {
        return Task.Run(async () =>
        {
            await foreach (var sub in service.WatchAnySubscribersAsync())
            {
                if (sub.AnySubscribers)
                {
                    break;
                }
            }
        });
    }
}
