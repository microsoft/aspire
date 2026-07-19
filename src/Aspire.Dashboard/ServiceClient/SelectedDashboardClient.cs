// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

internal sealed class SelectedDashboardClient(DashboardClient currentClient, DashboardDataSource dataSource) : IDashboardClient
{
    public Task WhenConnected => IsReadOnly ? Task.CompletedTask : currentClient.WhenConnected;
    public bool IsEnabled => IsReadOnly || currentClient.IsEnabled;
    public bool IsReadOnly => dataSource.IsReadOnly;
    public DashboardConnectionState ConnectionState => IsReadOnly ? DashboardConnectionState.Connected : currentClient.ConnectionState;
    public string ApplicationName => dataSource.SelectedRun.ApplicationName ?? currentClient.ApplicationName;
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
    public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken) =>
        IsReadOnly
            ? dataSource.ResourceRepository.SubscribeConsoleLogs(resourceName, cancellationToken)
            : currentClient.SubscribeConsoleLogs(resourceName, cancellationToken);
    public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) =>
        IsReadOnly
            ? dataSource.ResourceRepository.GetConsoleLogs(resourceName, cancellationToken)
            : currentClient.GetConsoleLogs(resourceName, cancellationToken);
    public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken) =>
        IsReadOnly ? EmptyInteractionsAsync() : currentClient.SubscribeInteractionsAsync(cancellationToken);

    public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.SendInteractionRequestAsync(request, cancellationToken);
    }

    public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.ExecuteResourceCommandAsync(resourceName, resourceType, command, options, cancellationToken);
    }

    public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.UploadFileAsync(fileStream, fileName, expectedSize, cancellationToken);
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