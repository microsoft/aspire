// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Utils;

public static class PersistentContainerTestHelpers
{
    private const string ContainerIdPropertyName = "container.id";
    private const string ContainerLifetimePropertyName = "container.lifetime";

    /// <summary>
    /// Verifies that a resource configured with a persistent lifetime uses the same Docker container across AppHost runs.
    /// </summary>
    /// <param name="testOutputHelper">The xUnit output helper used for test and resource logging.</param>
    /// <param name="configureResource">Configures the persistent resource on each AppHost run.</param>
    /// <param name="resourceName">The resource name whose persistent Docker container identity should be compared.</param>
    /// <param name="useTestContainerRegistry">Whether to apply the test container registry override for integrations that require CI-mirrored images.</param>
    /// <param name="randomizePorts">Whether to force DCP to randomize ports for the AppHost runs.</param>
    /// <param name="timeout">The timeout for starting, stopping, and observing the resource. Defaults to 10 minutes because some container integrations have slow cold starts.</param>
    public static async Task AssertResourceReusesContainerAsync(
        ITestOutputHelper testOutputHelper,
        Action<IDistributedApplicationTestingBuilder> configureResource,
        string resourceName,
        bool useTestContainerRegistry = false,
        bool randomizePorts = false,
        TimeSpan? timeout = null)
    {
        await AssertResourcesReuseContainersAsync(
            testOutputHelper,
            configureResource,
            [resourceName],
            useTestContainerRegistry,
            randomizePorts,
            compareUrls: false,
            timeout);
    }

    /// <summary>
    /// Verifies that resources configured with persistent lifetimes use the same Docker containers across AppHost runs.
    /// </summary>
    /// <param name="testOutputHelper">The xUnit output helper used for test and resource logging.</param>
    /// <param name="configureResources">Configures the persistent resources on each AppHost run.</param>
    /// <param name="resourceNames">The resource names whose persistent Docker container identities should be compared.</param>
    /// <param name="useTestContainerRegistry">Whether to apply the test container registry override for integrations that require CI-mirrored images.</param>
    /// <param name="randomizePorts">Whether to force DCP to randomize ports for the AppHost runs.</param>
    /// <param name="compareUrls">Whether to compare the resource URLs across runs. This also verifies stable public ports.</param>
    /// <param name="timeout">The timeout for starting, stopping, and observing the resources. Defaults to 10 minutes because some container integrations have slow cold starts.</param>
    public static async Task AssertResourcesReuseContainersAsync(
        ITestOutputHelper testOutputHelper,
        Action<IDistributedApplicationTestingBuilder> configureResources,
        string[] resourceNames,
        bool useTestContainerRegistry = false,
        bool randomizePorts = false,
        bool compareUrls = false,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var aspireStore = new TestTempDirectory();
        var userSecretsId = Guid.NewGuid().ToString("N");

        try
        {
            var before = await RunContainerAsync();
            var after = await RunContainerAsync();

            Assert.Equal(before, after);
        }
        finally
        {
            var userSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
            if (Path.GetDirectoryName(userSecretsPath) is { } userSecretsDirectory && Directory.Exists(userSecretsDirectory))
            {
                try
                {
                    Directory.Delete(userSecretsDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only. A locked secrets file should not fail an otherwise successful test.
                }
            }
        }

        async Task<ResourceRunSnapshot[]> RunContainerAsync()
        {
            var args = new[]
            {
                "--environment=Development",
                $"{KnownConfigNames.AspireUserSecretsId}={userSecretsId}",
                // Persistent-container reuse tests default to normal run behavior. Individual
                // tests opt into randomization when the integration doesn't publish the public
                // proxy port into container configuration that participates in the lifecycle key.
                $"DcpPublisher:RandomizePorts={(randomizePorts ? "true" : "false")}"
            };

            using var builder = (useTestContainerRegistry
                    ? TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper, args)
                    : TestDistributedApplicationBuilder.Create(testOutputHelper, args))
                .WithTempAspireStore(aspireStore.Path)
                .WithResourceCleanUp(false);

            Assert.True(builder.UserSecretsManager.IsAvailable);

            configureResources(builder);

            using var app = builder.Build();
            await app.StartAsync(cts.Token);

            var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
            var resourceSnapshots = await Task.WhenAll(
                resourceNames.Select(resourceName => GetContainerIdentityAsync(resourceNotificationService, resourceName, compareUrls, cts.Token)));

            await app.StopAsync(cts.Token).WaitAsync(cts.Token);

            return resourceSnapshots.OrderBy(snapshot => snapshot.ResourceName, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Gets the Docker container identity for a persistent resource after it becomes healthy.
    /// </summary>
    private static async Task<ResourceRunSnapshot> GetContainerIdentityAsync(ResourceNotificationService resourceNotificationService, string resourceName, bool includeUrls, CancellationToken cancellationToken)
    {
        await resourceNotificationService.WaitForResourceHealthyAsync(resourceName, cancellationToken);
        var resourceEvent = await resourceNotificationService.WaitForResourceAsync(resourceName, evt =>
        {
            return GetPropertyValue(evt, ContainerLifetimePropertyName) is ContainerLifetime.Persistent &&
                GetPropertyValue(evt, ContainerIdPropertyName) is string { Length: > 0 } &&
                (!includeUrls || evt.Snapshot.Urls.Length > 0);
        }, cancellationToken);

        var containerLifetime = GetPropertyValue(resourceEvent, ContainerLifetimePropertyName);
        Assert.Equal(ContainerLifetime.Persistent, containerLifetime);

        var containerId = Assert.IsType<string>(GetPropertyValue(resourceEvent, ContainerIdPropertyName));
        Assert.NotEmpty(containerId);

        var urls = includeUrls
            ? string.Join(Environment.NewLine, resourceEvent.Snapshot.Urls
                .OrderBy(url => url.Name, StringComparer.Ordinal)
                .ThenBy(url => url.Url, StringComparer.Ordinal)
                .Select(url => $"{url.Name}:{url.Url}"))
            : string.Empty;

        if (includeUrls)
        {
            Assert.NotEmpty(urls);
        }

        return new(resourceName, containerId, urls);
    }

    private static object? GetPropertyValue(ResourceEvent resourceEvent, string propertyName) =>
        resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == propertyName)?.Value;

    private sealed record ResourceRunSnapshot(string ResourceName, string ContainerId, string Urls);
}
