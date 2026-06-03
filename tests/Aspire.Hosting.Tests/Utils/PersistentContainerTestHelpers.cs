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

        async Task<string> RunContainerAsync()
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

            configureResource(builder);

            using var app = builder.Build();
            await app.StartAsync(cts.Token);

            var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
            var containerIdentity = await GetContainerIdentityAsync(resourceNotificationService, resourceName, cts.Token);

            await app.StopAsync(cts.Token).WaitAsync(cts.Token);

            return containerIdentity;
        }
    }

    /// <summary>
    /// Gets the Docker container identity for a persistent resource after it becomes healthy.
    /// </summary>
    private static async Task<string> GetContainerIdentityAsync(ResourceNotificationService resourceNotificationService, string resourceName, CancellationToken cancellationToken)
    {
        await resourceNotificationService.WaitForResourceHealthyAsync(resourceName, cancellationToken);
        var resourceEvent = await resourceNotificationService.WaitForResourceAsync(resourceName, evt =>
        {
            return GetPropertyValue(evt, ContainerLifetimePropertyName) is ContainerLifetime.Persistent &&
                GetPropertyValue(evt, ContainerIdPropertyName) is string { Length: > 0 };
        }, cancellationToken);

        var containerLifetime = GetPropertyValue(resourceEvent, ContainerLifetimePropertyName);
        Assert.Equal(ContainerLifetime.Persistent, containerLifetime);

        var containerId = Assert.IsType<string>(GetPropertyValue(resourceEvent, ContainerIdPropertyName));
        Assert.NotEmpty(containerId);

        return containerId;
    }

    private static object? GetPropertyValue(ResourceEvent resourceEvent, string propertyName) =>
        resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == propertyName)?.Value;
}
