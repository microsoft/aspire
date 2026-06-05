// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
