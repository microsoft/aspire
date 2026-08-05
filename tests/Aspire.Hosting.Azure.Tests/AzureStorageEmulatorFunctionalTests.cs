// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Azure.Storage.Queues;

namespace Aspire.Hosting.Azure.Tests;

public class AzureStorageEmulatorFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnAzureStorageEmulatorForBlobsBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var storage = builder.AddAzureStorage("resource")
                              .RunAsEmulator()
                              .WithHealthCheck("blocking_check");

        var blobs = storage.AddBlobs("blobs");
        var queues = storage.AddQueues("queues");
        var tables = storage.AddTables("tables");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(blobs)
                                       .WaitFor(queues)
                                       .WaitFor(tables);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceAsync(storage.Resource.Name, KnownResourceStates.Running, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await rns.WaitForResourceHealthyAsync(blobs.Resource.Name, cts.Token);
        await rns.WaitForResourceHealthyAsync(queues.Resource.Name, cts.Token);
        await rns.WaitForResourceHealthyAsync(tables.Resource.Name, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnAzureStorageEmulatorForBlobContainersBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var storage = builder.AddAzureStorage("resource")
                              .RunAsEmulator()
                              .WithHealthCheck("blocking_check");

        var blobContainer = storage.AddBlobContainer("testblobcontainer");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(blobContainer);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceAsync(storage.Resource.Name, KnownResourceStates.Running, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await rns.WaitForResourceHealthyAsync(blobContainer.Resource.Name, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnAzureStorageEmulatorForQueueBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var storage = builder.AddAzureStorage("resource")
                              .RunAsEmulator()
                              .WithHealthCheck("blocking_check");

        var testQueue = storage.AddQueue("testqueue");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(testQueue);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceAsync(storage.Resource.Name, KnownResourceStates.Running, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await rns.WaitForResourceHealthyAsync(testQueue.Resource.Name, cts.Token);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureStorageEmulatorResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var blobsResourceName = "BlobConnection";
        var blobContainerName = "my-container";
        var queuesResourceName = "QueuesConnection";
        var queueName = "my-queue";

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var blobs = storage.AddBlobs(blobsResourceName);
        var container = storage.AddBlobContainer(blobContainerName);

        var queues = storage.AddQueues(queuesResourceName);
        var queue = storage.AddQueue(queueName);

        using var app = builder.Build();
        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration[$"ConnectionStrings:{blobsResourceName}"] = await blobs.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.Configuration[$"ConnectionStrings:{blobContainerName}"] = await container.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.Configuration[$"ConnectionStrings:{queuesResourceName}"] = await queues.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.Configuration[$"ConnectionStrings:{queueName}"] = await queue.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);

        hb.AddAzureBlobServiceClient(blobsResourceName);
        hb.AddAzureBlobContainerClient(blobContainerName);
        hb.AddAzureQueueServiceClient(queuesResourceName);
        hb.AddAzureQueue(queueName);

        using var host = hb.Build();
        await host.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceHealthyAsync(storage.Resource.Name, cts.Token);

        var blobContainerClient = host.Services.GetRequiredService<BlobContainerClient>();
        var blobClient = blobContainerClient.GetBlobClient("testKey");
        var queueClient = host.Services.GetRequiredService<QueueClient>();

        await blobClient.UploadAsync(BinaryData.FromString("testValue"));

        var downloadResult = (await blobClient.DownloadContentAsync()).Value;
        Assert.Equal("testValue", downloadResult.Content.ToString());

        await queueClient.SendMessageAsync("Hello, World!");
        var peekedMessages = await queueClient.PeekMessagesAsync(1);
        Assert.Single(peekedMessages.Value);
        Assert.Equal("Hello, World!", peekedMessages.Value[0].MessageText);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureStorageEmulator_blobcontainer_auto_created()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var blobs = storage.AddBlobs("BlobConnection");
        var blobContainer = storage.AddBlobContainer("testblobcontainer");

        using var app = builder.Build();
        await app.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(blobContainer.Resource.Name, cancellationToken: cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration["ConnectionStrings:BlobConnection"] = await blobs.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.AddAzureBlobServiceClient("BlobConnection");

        using var host = hb.Build();
        await host.StartAsync();

        var serviceClient = host.Services.GetRequiredService<BlobServiceClient>();
        var blobContainerClient = serviceClient.GetBlobContainerClient("testblobcontainer");

        var exists = await blobContainerClient.ExistsAsync();
        Assert.True(exists, "Blob container should exist after starting the application.");

        var blobNameAndContent = Guid.NewGuid().ToString();
        var response = await blobContainerClient.UploadBlobAsync(blobNameAndContent, new BinaryData(blobNameAndContent));

        var blobClient = blobContainerClient.GetBlobClient(blobNameAndContent);

        var downloadResult = (await blobClient.DownloadContentAsync()).Value;
        Assert.Equal(blobNameAndContent, downloadResult.Content.ToString());
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureStorageEmulator_queue_auto_created()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var queues = storage.AddQueues("QueueConnection");
        var queue = storage.AddQueue("testqueue");

        using var app = builder.Build();
        await app.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(queue.Resource.Name, cancellationToken: cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration["ConnectionStrings:QueueConnection"] = await queues.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.AddAzureQueueServiceClient("QueueConnection");

        using var host = hb.Build();
        await host.StartAsync();

        var serviceClient = host.Services.GetRequiredService<QueueServiceClient>();
        var queueClient = serviceClient.GetQueueClient("testqueue");

        var exists = await queueClient.ExistsAsync();
        Assert.True(exists, "Queue should exist after starting the application.");

        var blobNameAndContent = Guid.NewGuid().ToString();
        var response = await queueClient.SendMessageAsync(blobNameAndContent);

        var peekMessage = await queueClient.PeekMessageAsync();

        Assert.Equal(blobNameAndContent, peekMessage.Value.Body.ToString());
    }
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public Task AzureStorageEmulator_WithPersistentLifetime_ReusesContainersAndPorts()
    {
        return PersistentContainerTestHelpers.AssertResourcesReuseContainersAsync(
            testOutputHelper,
            builder =>
            {
                builder.AddAzureStorage("storage1").RunAsEmulator(container => container.WithPersistentLifetime());
                builder.AddAzureStorage("storage2").RunAsEmulator(container => container.WithPersistentLifetime());
            },
            ["storage1", "storage2"],
            compareUrls: true);
    }

}
