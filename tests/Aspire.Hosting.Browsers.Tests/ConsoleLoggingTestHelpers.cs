// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tests.Utils;

internal static class ConsoleLoggingTestHelpers
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService service, string resourceName, int targetLogCount, Action writeLogs)
    {
        var subscribedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchTask = WatchForLogsAsync(service.WatchAsync(resourceName), targetLogCount);

        _ = Task.Run(async () =>
        {
            await foreach (var subscriber in service.WatchAnySubscribersAsync())
            {
                if (subscriber.Name == resourceName && subscriber.AnySubscribers)
                {
                    subscribedTcs.TrySetResult();
                    return;
                }
            }
        });

        await subscribedTcs.Task.WaitAsync(s_defaultTimeout);
        writeLogs();

        return await watchTask.WaitAsync(s_defaultTimeout);
    }

    public static Task<IReadOnlyList<LogLine>> WatchForLogsAsync(IAsyncEnumerable<IReadOnlyList<LogLine>> watchEnumerable, int targetLogCount)
    {
        return Task.Run(async () =>
        {
            var logs = new List<LogLine>();
            await foreach (var log in watchEnumerable)
            {
                logs.AddRange(log);
                if (logs.Count >= targetLogCount)
                {
                    break;
                }
            }

            return (IReadOnlyList<LogLine>)logs;
        });
    }

    public static ResourceLoggerService GetResourceLoggerService()
    {
        var service = new ResourceLoggerService();
        var timeProviderProperty = typeof(ResourceLoggerService).GetProperty("TimeProvider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResourceLoggerService.TimeProvider was not found.");

        timeProviderProperty.SetValue(service, new TestTimeProvider());

        return service;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);
        }
    }
}
