// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Utils;

public static class PersistentContainerTestHelpers
{
    /// <summary>
    /// Verifies that a resource configured with a persistent lifetime reuses the same container across AppHost runs.
    /// </summary>
    /// <param name="testOutputHelper">The xUnit output helper used for test and resource logging.</param>
    /// <param name="configureResource">Configures the persistent resource on each AppHost run.</param>
    /// <param name="resourceName">The resource name whose container ID should be compared.</param>
    /// <param name="timeout">The timeout for starting, stopping, and observing the resource. Defaults to 10 minutes because some container integrations have slow cold starts.</param>
    public static async Task AssertResourceReusesContainerAsync(
        ITestOutputHelper testOutputHelper,
        Action<IDistributedApplicationTestingBuilder> configureResource,
        string resourceName,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var aspireStore = new TestTempDirectory();

        var before = await RunContainerAsync();
        var after = await RunContainerAsync();

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before, after);

        async Task<string?> RunContainerAsync()
        {
            using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper)
                .WithTempAspireStore(aspireStore.Path)
                .WithResourceCleanUp(false);

            configureResource(builder);

            using var app = builder.Build();
            await app.StartAsync(cts.Token);

            var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
            var containerId = await GetContainerIdAsync(resourceNotificationService, resourceName, cts.Token);

            await app.StopAsync(cts.Token).WaitAsync(cts.Token);

            return containerId;
        }
    }

    /// <summary>
    /// Gets the container ID for a resource after it becomes healthy.
    /// </summary>
    private static async Task<string?> GetContainerIdAsync(ResourceNotificationService resourceNotificationService, string resourceName, CancellationToken cancellationToken)
    {
        await resourceNotificationService.WaitForResourceHealthyAsync(resourceName, cancellationToken);
        var resourceEvent = await resourceNotificationService.WaitForResourceAsync(resourceName, evt =>
        {
            return evt.Snapshot.Properties.FirstOrDefault(x => x.Name == "container.id")?.Value != null;
        }, cancellationToken);

        return resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == "container.id")?.Value?.ToString();
    }
}
