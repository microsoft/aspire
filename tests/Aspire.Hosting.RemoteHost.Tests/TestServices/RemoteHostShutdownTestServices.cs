// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.RemoteHost.Tests;

internal static class RemoteHostShutdownTestServices
{
    public static DistributedApplication CreateApplication(BlockingHostedService service)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["AppHost:Operation=inspect"],
            DisableDashboard = true
        });
        // Exercise the real application's StopAsync/DisposeAsync boundary without starting DCP.
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton<IHostedService>(_ => service);

        return builder.Build();
    }
}

internal sealed class BlockingHostedService : IHostedLifecycleService, IAsyncDisposable
{
    public ConcurrentQueue<string> Events { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseStopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? StoppingException { get; init; }
    public Exception? DisposalException { get; init; }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        Events.Enqueue("started");
        Started.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        Events.Enqueue("stopping");
        Stopping.TrySetResult();
        await ReleaseStopping.Task.WaitAsync(cancellationToken);
        if (StoppingException is { } exception)
        {
            throw exception;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Events.Enqueue("stopped");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Events.Enqueue("disposed");
        Disposed.TrySetResult();
        if (DisposalException is { } exception)
        {
            throw exception;
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingAsyncDisposable : IAsyncDisposable
{
    public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? DisposalException { get; init; }

    public async ValueTask DisposeAsync()
    {
        Disposing.TrySetResult();
        await ReleaseDisposal.Task;
        Disposed.TrySetResult();
        if (DisposalException is { } exception)
        {
            throw exception;
        }
    }
}
