// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Foundry.Tests;

public class FoundryLocalLifecycleTests
{
    [Fact]
    public async Task OverlappingReadyEventsDownloadAndLoadModelOnce()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var localModelService = new TestFoundryLocalModelService();
        builder.Services.AddSingleton<IFoundryLocalModelService>(localModelService);

        var foundry = builder.AddFoundry("foundry");
        var deployment = foundry.AddDeployment("chat", "model", "1", "OpenAI");
        foundry.RunAsFoundryLocal();

        await using var app = builder.Build();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(foundry.Resource, app.Services));
        await localModelService.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(foundry.Resource, app.Services));

        localModelService.AllowDownload.TrySetResult();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => localModelService.IsModelLoadedCallCount == 2,
            "Expected both overlapping ready events to finish checking the model state.");

        Assert.Equal(1, localModelService.DownloadCallCount);
        Assert.Equal(1, localModelService.LoadCallCount);
        Assert.Equal("model-id", deployment.Resource.ModelId);
    }

    [Fact]
    public async Task ReadyEventAfterRestartReloadsWithoutDownloadingModelAgain()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var localModelService = new TestFoundryLocalModelService();
        builder.Services.AddSingleton<IFoundryLocalModelService>(localModelService);

        var foundry = builder.AddFoundry("foundry");
        var deployment = foundry.AddDeployment("chat", "model", "1", "OpenAI");
        foundry.RunAsFoundryLocal();

        await using var app = builder.Build();
        localModelService.AllowDownload.TrySetResult();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(foundry.Resource, app.Services));
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => localModelService.LoadCallCount == 1,
            "Expected the model to load for the first ready event.");

        localModelService.Unload();
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(foundry.Resource, app.Services));
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => localModelService.LoadCallCount == 2,
            "Expected the downloaded model to reload after the parent restarted.");

        Assert.Equal(1, localModelService.DownloadCallCount);
        Assert.Equal("model-id", deployment.Resource.ModelId);
    }
}
