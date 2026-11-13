// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Provides dashboard resource snapshots, updates, and console logs.
/// </summary>
public interface IResourceRepository
{
    /// <summary>
    /// Gets the current set of resources and a stream of updates.
    /// </summary>
    Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a resource matching the specified name.
    /// </summary>
    ResourceViewModel? GetResource(string resourceName);

    /// <summary>
    /// Gets the current resources.
    /// </summary>
    IReadOnlyList<ResourceViewModel> GetResources();

    /// <summary>
    /// Gets existing and incoming console log messages for a resource.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Gets existing console log messages for a resource.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes persisted console log messages for the specified resources.
    /// </summary>
    /// <param name="resourceNames">The names of the resources whose console logs should be deleted.</param>
    /// <param name="clearDate">The timestamp through which replayed console logs should remain deleted.</param>
    /// <returns>A task that represents the asynchronous delete operation.</returns>
    Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate);
}