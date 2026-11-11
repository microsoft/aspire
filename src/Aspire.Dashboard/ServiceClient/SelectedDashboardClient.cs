// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

internal sealed class SelectedDashboardClient(DashboardClient currentClient, DashboardDataSource dataSource) : IDashboardClient
{
    public Task WhenConnected => IsReadOnly ? Task.CompletedTask : currentClient.WhenConnected;
    public Task WhenResourcesReady => IsReadOnly ? Task.CompletedTask : currentClient.WhenResourcesReady;
    public bool IsEnabled => IsReadOnly || currentClient.IsEnabled;
    public bool IsReadOnly => dataSource.IsReadOnly;
    public DashboardConnectionState ConnectionState => IsReadOnly ? DashboardConnectionState.Connected : currentClient.ConnectionState;
    // Current runs must retain the resource service's name rather than the startup configuration stored in metadata.
    public string ApplicationName => IsReadOnly
        ? DashboardOptions.GetApplicationNameOrDefault(dataSource.SelectedRun.ApplicationName)
        : currentClient.ApplicationName;
    public string? MinRequiredVersion => IsReadOnly ? null : currentClient.MinRequiredVersion;
    public LaunchGroupInformationViewModel? LaunchGroups => IsReadOnly ? null : currentClient.LaunchGroups;

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
    public async Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        var repository = dataSource.ResourceRepository;
        var isCurrent = dataSource.IsCurrentResourceRepository(repository);
        var subscription = await repository.SubscribeResourcesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var resource in subscription.InitialState)
        {
            ProjectLaunchGroup(resource, isCurrent);
        }

        return subscription with
        {
            Subscription = ProjectLaunchGroupsAsync(subscription.Subscription, isCurrent)
        };
    }

    public ResourceViewModel? GetResource(string resourceName)
    {
        var repository = dataSource.ResourceRepository;
        var resource = repository.GetResource(resourceName);
        if (resource is not null)
        {
            ProjectLaunchGroup(resource, dataSource.IsCurrentResourceRepository(repository));
        }

        return resource;
    }

    public IReadOnlyList<ResourceViewModel> GetResources()
    {
        var repository = dataSource.ResourceRepository;
        var resources = repository.GetResources();
        var isCurrent = dataSource.IsCurrentResourceRepository(repository);
        foreach (var resource in resources)
        {
            ProjectLaunchGroup(resource, isCurrent);
        }

        return resources;
    }

    public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken) =>
        IsReadOnly
            ? dataSource.ResourceRepository.SubscribeConsoleLogs(resourceName, cancellationToken)
            : currentClient.SubscribeConsoleLogs(resourceName, cancellationToken);
    public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) =>
        IsReadOnly
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
        EnsureWritable();
        return currentClient.SendInteractionRequestAsync(request, cancellationToken);
    }

    public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.ExecuteResourceCommandAsync(resourceName, resourceType, command, options, cancellationToken);
    }

    public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.UploadFileAsync(fileStream, fileName, expectedSize, interactionId, inputName, cancellationToken);
    }

    public Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.AttachTerminalAsync(terminalId, cancellationToken);
    }

    public IAsyncEnumerable<WatchTerminalsUpdate> SubscribeTerminalsAsync(CancellationToken cancellationToken) =>
        IsReadOnly ? EmptyTerminalsAsync() : currentClient.SubscribeTerminalsAsync(cancellationToken);

    public Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return currentClient.CloseTerminalAsync(terminalId, cancellationToken);
    }

    private static async IAsyncEnumerable<WatchTerminalsUpdate> EmptyTerminalsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
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

    private async IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> ProjectLaunchGroupsAsync(
        IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> updates,
        bool isCurrent)
    {
        await foreach (var changes in updates.ConfigureAwait(false))
        {
            foreach (var change in changes)
            {
                ProjectLaunchGroup(change.Resource, isCurrent);
            }

            yield return changes;
        }
    }

    private void ProjectLaunchGroup(ResourceViewModel resource, bool isCurrent)
    {
        // A subscription can outlive a run switch, and its view models are shared by other circuits.
        // Project using the subscribed repository, not the scope's newly selected run.
        resource.LaunchGroup = isCurrent ? currentClient.LaunchGroups?.FindResource(resource.DisplayName) : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}