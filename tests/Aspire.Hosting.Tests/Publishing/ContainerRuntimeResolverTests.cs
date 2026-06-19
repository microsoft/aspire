// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Publishing;

public class ContainerRuntimeResolverTests
{
    private static ContainerRuntimeResolver CreateResolver(
        string? configuredRuntime = null,
        IServiceProvider? serviceProvider = null)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IContainerRuntime, FakeContainerRuntime>("docker");
        services.AddKeyedSingleton<IContainerRuntime, FakeContainerRuntime>("podman");
        var sp = serviceProvider ?? services.BuildServiceProvider();

        var dcpOptions = Options.Create(new DcpOptions { ContainerRuntime = configuredRuntime });
        return new ContainerRuntimeResolver(sp, dcpOptions, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsSameInstance_OnSubsequentCalls()
    {
        var resolver = CreateResolver(configuredRuntime: "docker");

        var first = await resolver.ResolveAsync();
        var second = await resolver.ResolveAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsSameTask_WhenCached()
    {
        var resolver = CreateResolver(configuredRuntime: "docker");

        var task1 = resolver.ResolveAsync();
        var task2 = resolver.ResolveAsync();

        Assert.Same(task1, task2);
        await task1;
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredRuntime_ReturnsKeyedService()
    {
        var resolver = CreateResolver(configuredRuntime: "podman");

        var runtime = await resolver.ResolveAsync();

        Assert.NotNull(runtime);
    }

    [Fact]
    public async Task ResolveAsync_AfterCancellation_RetriesWithNewToken()
    {
        var resolver = CreateResolver(configuredRuntime: null);

        // First call with an already-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The first call may or may not throw depending on timing —
        // if detection hasn't started yet, the token cancels it immediately.
        Task<IContainerRuntime>? firstTask = null;
        try
        {
            firstTask = resolver.ResolveAsync(cts.Token);
            await firstTask;
        }
        catch (OperationCanceledException)
        {
            // Expected — first attempt was cancelled
        }

        // Second call with a valid token should work (not return cached cancellation)
        if (firstTask is { IsCanceled: true })
        {
            var runtime = await resolver.ResolveAsync(CancellationToken.None);
            Assert.NotNull(runtime);
        }
    }
}
