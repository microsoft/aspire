// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace RemoteDependencies;

// Use the same notification/log services as the dashboard when running without its UI.
internal sealed class HeadlessObserver(DistributedApplicationModel model,
    ResourceNotificationService notifications, ResourceLoggerService logs) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        WatchStatesAsync(stoppingToken),
        Task.WhenAll(model.Resources.Where(r => r is RemoteRedisResource or RemoteMachineResource)
            .Select(r => WatchLogsAsync(r, stoppingToken))));

    private async Task WatchStatesAsync(CancellationToken token)
    {
        await foreach (var update in notifications.WatchAsync(token))
        {
            if (update.Resource is RemoteRedisResource or RemoteMachineResource)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "state",
                    resource = update.Resource.Name,
                    state = update.Snapshot.State?.Text,
                    urls = update.Snapshot.Urls.Select(u => new { u.Url, u.IsInactive })
                }));
            }
        }
    }

    private async Task WatchLogsAsync(IResource resource, CancellationToken token)
    {
        await foreach (var batch in logs.WatchAsync(resource).WithCancellation(token))
        {
            foreach (var line in batch)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "log", resource = resource.Name, content = line.Content }));
            }
        }
    }
}
