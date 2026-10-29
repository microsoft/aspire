// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

// Select the writable run at the batch boundary, serialized with run rollover.
internal sealed class CurrentResourceWriter(DashboardDataSourcePool pool) : IResourceRepositoryWriter
{
    public async Task ReplaceResourcesAsync(IReadOnlyList<Resource> resources)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((IResourceRepositoryWriter)pool.Current.ResourceRepository).ReplaceResourcesAsync(resources).ConfigureAwait(false);
        }
    }

    public async Task ApplyChangesAsync(IReadOnlyList<WatchResourcesChange> changes)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((IResourceRepositoryWriter)pool.Current.ResourceRepository).ApplyChangesAsync(changes).ConfigureAwait(false);
        }
    }

    public async Task MarkConsoleLogsLoadedAsync(string resourceName)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((IResourceRepositoryWriter)pool.Current.ResourceRepository).MarkConsoleLogsLoadedAsync(resourceName).ConfigureAwait(false);
        }
    }

    public async Task AddConsoleLogsAsync(string resourceName, IReadOnlyList<ConsoleLogLine> logLines)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((IResourceRepositoryWriter)pool.Current.ResourceRepository).AddConsoleLogsAsync(resourceName, logLines).ConfigureAwait(false);
        }
    }

    public async Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((IResourceRepositoryWriter)pool.Current.ResourceRepository).ClearConsoleLogsAsync(resourceNames, clearDate).ConfigureAwait(false);
        }
    }

}
