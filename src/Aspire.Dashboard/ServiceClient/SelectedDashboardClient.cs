// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

internal sealed class SelectedDashboardClient(DashboardClient currentClient, DashboardDataSource dataSource) : IDashboardClient
{
    public Task WhenConnected => IsReadOnly ? Task.CompletedTask : currentClient.WhenConnected;
    public Task WhenResourcesReady => IsReadOnly ? Task.CompletedTask : currentClient.WhenResourcesReady;
    public string AppHostRunId => currentClient.AppHostRunId;
    public bool IsEnabled => IsReadOnly || currentClient.IsEnabled;
    public bool IsReadOnly => dataSource.IsReadOnly;
    public DashboardConnectionState ConnectionState => IsReadOnly ? DashboardConnectionState.Connected : currentClient.ConnectionState;
    // Current runs must retain the resource service's name rather than the startup configuration stored in metadata.
    public string ApplicationName => IsReadOnly
        ? DashboardOptions.GetApplicationNameOrDefault(dataSource.SelectedRun.ApplicationName)
        : currentClient.ApplicationName;
    public string? MinRequiredVersion => IsReadOnly ? null : currentClient.MinRequiredVersion;

    public event Action<DashboardConnectionState>? ConnectionStateChanged
    {
        add
        {
            if (!IsReadOnly)
            {
                currentClient.ConnectionStateChanged += value;
            }
        }
        remove
        {
            currentClient.ConnectionStateChanged -= value;
        }
    }

    public Task ReconnectAsync() => IsReadOnly ? Task.CompletedTask : currentClient.ReconnectAsync();
    public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken) => dataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
    public ResourceViewModel? GetResource(string resourceName) => dataSource.ResourceRepository.GetResource(resourceName);
    public IReadOnlyList<ResourceViewModel> GetResources() => dataSource.ResourceRepository.GetResources();
    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var capture = IsReadOnly ? Task.CompletedTask : CaptureAsync();
        try
        {
            // Read cached and live data through one subscription. This keeps an offline page usable and
            // lets capture resume after reconnect without replaying duplicate RPC batches into the viewer.
            await foreach (var batch in dataSource.ResourceRepository.SubscribeConsoleLogs(resourceName, cts.Token).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            cts.Cancel();
            try
            {
                await capture.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }

        async Task CaptureAsync()
        {
            try
            {
                await currentClient.CaptureConsoleLogsAsync(resourceName, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Wake a waiting SQLite reader so an unexpected capture failure is observed by the caller.
                cts.Cancel();
                throw;
            }
        }
    }

    public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) =>
        IsReadOnly || ConnectionState != DashboardConnectionState.Connected
            ? dataSource.ResourceRepository.GetConsoleLogs(resourceName, cancellationToken)
            : currentClient.GetConsoleLogs(resourceName, cancellationToken);

    public Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate)
    {
        EnsureWritable();
        return dataSource.ResourceRepository.ClearConsoleLogsAsync(resourceNames, clearDate);
    }

    public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken) =>
        IsReadOnly ? EmptyInteractionsAsync() : currentClient.SubscribeInteractionsAsync(cancellationToken);

    public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        EnsureLiveConnection();
        return currentClient.SendInteractionRequestAsync(request, cancellationToken);
    }

    public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken)
    {
        EnsureWritable();
        if (currentClient.ConnectionState != DashboardConnectionState.Connected)
        {
            return Task.FromResult(new ResourceCommandResponseViewModel { Kind = Aspire.Dashboard.Model.ResourceCommandResponseKind.Cancelled });
        }
        return currentClient.ExecuteResourceCommandAsync(resourceName, resourceType, command, options, cancellationToken);
    }

    public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, string appHostRunId, CancellationToken cancellationToken)
    {
        EnsureLiveConnection();
        return currentClient.UploadFileAsync(fileStream, fileName, expectedSize, interactionId, inputName, appHostRunId, cancellationToken);
    }

    public Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureLiveConnection();
        return currentClient.AttachTerminalAsync(terminalId, cancellationToken);
    }

    public IAsyncEnumerable<WatchTerminalsUpdate> SubscribeTerminalsAsync(CancellationToken cancellationToken) =>
        IsReadOnly ? EmptyTerminalsAsync() : currentClient.SubscribeTerminalsAsync(cancellationToken);

    public Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureLiveConnection();
        return currentClient.CloseTerminalAsync(terminalId, cancellationToken);
    }

    private static async IAsyncEnumerable<WatchTerminalsUpdate> EmptyTerminalsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private void EnsureLiveConnection()
    {
        EnsureWritable();
        if (currentClient.ConnectionState != DashboardConnectionState.Connected)
        {
            throw new InvalidOperationException("The AppHost is disconnected.");
        }
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Historical dashboard data is read-only.");
        }
    }

    private static async IAsyncEnumerable<WatchInteractionsResponseUpdate> EmptyInteractionsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}