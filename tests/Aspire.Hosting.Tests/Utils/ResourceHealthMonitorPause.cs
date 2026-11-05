// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Health;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class ResourceHealthMonitorPause : IAsyncDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private Task _pauseTask = Task.CompletedTask;
    private bool _disposed;

    public static async Task<ResourceHealthMonitorPause> CreateAsync(ResourceHealthCheckService service)
    {
        var pause = new ResourceHealthMonitorPause();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Use the real consumer's existing lock, without a production-only test hook. Publishers
        // can queue terminal, prepared Starting and Running snapshots while consumption is paused.
        var syncRoot = typeof(ResourceHealthCheckService)
            .GetField("_resourceMonitoringStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        pause._pauseTask = Task.Run(() =>
        {
            lock (syncRoot)
            {
                entered.TrySetResult();
                if (!pause._release.Wait(TestConstants.LongTimeoutTimeSpan))
                {
                    throw new TimeoutException("Health notification consumer was not released.");
                }
            }
        });
        try
        {
            await entered.Task.DefaultTimeout();
            return pause;
        }
        catch
        {
            await pause.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _release.Set();
        await _pauseTask.DefaultTimeout();
        _release.Dispose();
    }
}
