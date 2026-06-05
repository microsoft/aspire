// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

public sealed class ConsoleLogsManager
{
    private readonly object _lock = new object();
    private readonly List<ModelSubscription> _subscriptions = new List<ModelSubscription>();
    private readonly ISessionStorage _sessionStorage;
    private bool _hasInitialized;
    private ConsoleLogsFilters? _filters;

    public ConsoleLogsManager(ISessionStorage sessionStorage)
    {
        _sessionStorage = sessionStorage;
    }

    public ConsoleLogsFilters Filters
    {
        get
        {
            AssertInitialized();
            return _filters;
        }
    }

    [MemberNotNull(nameof(_filters))]
    private void AssertInitialized()
    {
        if (!_hasInitialized)
        {
            throw new InvalidOperationException($"{nameof(ConsoleLogsManager)} not initialized.");
        }

        Debug.Assert(_filters != null, "There should be filters if manager has been initialized.");
    }

    public async Task EnsureInitializedAsync()
    {
        if (!_hasInitialized)
        {
            var filtersResult = await _sessionStorage.GetAsync<ConsoleLogsFilters>(BrowserStorageKeys.ConsoleLogFilters).ConfigureAwait(false);
            _filters = filtersResult.Success ? filtersResult.Value : ConsoleLogsFilters.Default;

            _hasInitialized = true;
        }
    }

    public IDisposable OnFiltersChanged(Func<Task> callback)
    {
        lock (_lock)
        {
            var subscription = new ModelSubscription(callback, RemoveSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void RemoveSubscription(ModelSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    // Internal for tests.
    internal List<ModelSubscription> GetSubscriptions()
    {
        lock (_lock)
        {
            return _subscriptions.ToList();
        }
    }

    public async Task UpdateFiltersAsync(ConsoleLogsFilters filters)
    {
        _filters = filters;
        _hasInitialized = true;

        await _sessionStorage.SetAsync(BrowserStorageKeys.ConsoleLogFilters, filters).ConfigureAwait(false);

        ModelSubscription[] subscriptions;
        lock (_lock)
        {
            if (_subscriptions.Count == 0)
            {
                return;
            }

            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.ExecuteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the filter date for a resource based on the current filters.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The filter date, or null if no filter applies.</returns>
    public DateTime? GetFilterDate(string resourceName)
    {
        if (!_hasInitialized || _filters is null)
        {
            return null;
        }

        // Check for resource-specific filter first
        if (_filters.TryGetResourceFilterDate(resourceName, out var resourceFilterDate))
        {
            return resourceFilterDate;
        }

        // Fall back to global filter
        return _filters.FilterAllLogsDate;
    }
}
