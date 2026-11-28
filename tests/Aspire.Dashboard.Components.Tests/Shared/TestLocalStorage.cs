// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.BrowserStorage;

namespace Aspire.Dashboard.Components.Tests.Shared;

public sealed class TestLocalStorage : ILocalStorage
{
    private readonly Dictionary<string, object?> _protectedValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _unprotectedValues = new(StringComparer.Ordinal);

    public Func<string, Task>? OnBeforeGetAsync { get; set; }
    public Func<string, Task>? OnBeforeGetUnprotectedAsync { get; set; }
    public Func<string, (bool Success, object? Value)>? OnGetUnprotectedAsync { get; set; }
    public Action<string, object?>? OnSetUnprotectedAsync { get; set; }
    public Func<string, (bool Success, object? Value)>? OnGetAsync { get; set; }
    public Action<string, object?>? OnSetAsync { get; set; }

    /// <summary>
    /// Gets or sets a function that returns whether reading a protected key fails, as when the circuit is disconnected.
    /// </summary>
    public Func<string, bool>? IsReadFailed { get; set; }

    /// <summary>
    /// Gets or sets a function that returns whether reading an unprotected key fails, as when the circuit is disconnected.
    /// </summary>
    public Func<string, bool>? IsUnprotectedReadFailed { get; set; }

    public int SnapshotReadCount => _snapshotReadCount;
    private int _snapshotReadCount;

    public async Task<ILocalStorageSnapshot> ReadSnapshotAsync(string[] keys)
    {
        Interlocked.Increment(ref _snapshotReadCount);

        // The browser reads local storage when it's called and the read hooks simulate a slow response, so capture what's
        // stored before running them. Later writes don't change the snapshot, like a browser. Both protections are
        // captured because the protection a key is read with is only known when it's read from the snapshot. The
        // OnGet* overrides are scripted responses for one protection, so they run when the key is read that way.
        var items = new Dictionary<string, (StoredItem Protected, StoredItem Unprotected)>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            items[key] = (CaptureStored(key, IsReadFailed, _protectedValues), CaptureStored(key, IsUnprotectedReadFailed, _unprotectedValues));
        }

        // Run the read hooks once for the whole snapshot, like the single browser call: every key is requested
        // before any of those reads completes.
        if (OnBeforeGetUnprotectedAsync is { } beforeCallback)
        {
            await Task.WhenAll(keys.Select(beforeCallback));
        }

        return new Snapshot(this, items);
    }

    public async Task<StorageResult<T>> GetAsync<T>(string key)
    {
        if (OnBeforeGetAsync is { } beforeCallback)
        {
            await beforeCallback(key);
        }

        return Read<T>(key, CaptureStored(key, IsReadFailed, _protectedValues), OnGetAsync);
    }

    public async Task<StorageResult<T>> GetUnprotectedAsync<T>(string key)
    {
        if (OnBeforeGetUnprotectedAsync is { } beforeCallback)
        {
            await beforeCallback(key);
        }

        return Read<T>(key, CaptureStored(key, IsUnprotectedReadFailed, _unprotectedValues), OnGetUnprotectedAsync);
    }

    private static StoredItem CaptureStored(string key, Func<string, bool>? isReadFailed, Dictionary<string, object?> savedValues)
    {
        if (isReadFailed?.Invoke(key) == true)
        {
            return new StoredItem(ReadFailed: true, HasValue: false, Value: null);
        }

        // Like a browser, return what was saved. Settings stores read the stored value before every save, so a
        // storage that forgot saved values would make them drop their own changes.
        lock (savedValues)
        {
            return savedValues.TryGetValue(key, out var savedValue)
                ? new StoredItem(ReadFailed: false, HasValue: true, Value: savedValue)
                : default;
        }
    }

    private static StorageResult<T> Read<T>(string key, StoredItem stored, Func<string, (bool Success, object? Value)>? callback)
    {
        if (stored.ReadFailed)
        {
            return new StorageResult<T>(success: false, value: default) { ReadFailed = true };
        }

        // Callbacks return (false, null) for keys they don't provide, so those keys fall back to saved values. Use
        // the read-failed hooks to simulate a failed read.
        if (callback is not null)
        {
            var (success, value) = callback(key);
            if (success)
            {
                return new StorageResult<T>(success: true, value: (T)(value ?? default(T))!);
            }
        }

        if (stored.HasValue && stored.Value is T typedValue)
        {
            return new StorageResult<T>(success: true, value: typedValue);
        }

        return new StorageResult<T>(success: false, value: default);
    }

    public Task SetAsync<T>(string key, T value)
    {
        lock (_protectedValues)
        {
            _protectedValues[key] = value;
        }

        OnSetAsync?.Invoke(key, value);
        return Task.CompletedTask;
    }

    public Task SetUnprotectedAsync<T>(string key, T value)
    {
        lock (_unprotectedValues)
        {
            _unprotectedValues[key] = value;
        }

        if (OnSetUnprotectedAsync is { } callback)
        {
            callback(key, value);
        }
        return Task.CompletedTask;
    }

    private readonly record struct StoredItem(bool ReadFailed, bool HasValue, object? Value);

    private sealed class Snapshot(TestLocalStorage storage, Dictionary<string, (StoredItem Protected, StoredItem Unprotected)> items)
        : ILocalStorageSnapshot
    {
        public StorageResult<T> Get<T>(string key) => Read<T>(key, GetItem(key).Protected, storage.OnGetAsync);

        public StorageResult<T> GetUnprotected<T>(string key) => Read<T>(key, GetItem(key).Unprotected, storage.OnGetUnprotectedAsync);

        private (StoredItem Protected, StoredItem Unprotected) GetItem(string key)
        {
            if (!items.TryGetValue(key, out var item))
            {
                throw new ArgumentException($"Key '{key}' was not requested when loading the snapshot.", nameof(key));
            }

            return item;
        }
    }
}
