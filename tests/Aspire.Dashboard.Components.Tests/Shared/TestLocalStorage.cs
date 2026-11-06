// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.BrowserStorage;

namespace Aspire.Dashboard.Components.Tests.Shared;

public sealed class TestLocalStorage : ILocalStorage
{
    private readonly Dictionary<string, object?> _unprotectedValues = new(StringComparer.Ordinal);

    public Func<string, Task>? OnBeforeGetUnprotectedAsync { get; set; }
    public Func<string, (bool Success, object? Value)>? OnGetUnprotectedAsync { get; set; }
    public Action<string, object?>? OnSetUnprotectedAsync { get; set; }
    public Func<string, (bool Success, object? Value)>? OnGetAsync { get; set; }

    /// <summary>
    /// Gets or sets a function that returns whether reading a key fails, as when the circuit is disconnected.
    /// </summary>
    public Func<string, bool>? IsUnprotectedReadFailed { get; set; }

    public Task<StorageResult<T>> GetAsync<T>(string key)
    {
        if (OnGetAsync is { } callback)
        {
            var (success, value) = callback(key);
            return Task.FromResult(new StorageResult<T>(success: success, value: (T)(value ?? default(T))!));
        }
        return Task.FromResult(new StorageResult<T>(success: false, value: default));
    }

    public async Task<StorageResult<T>> GetUnprotectedAsync<T>(string key)
    {
        if (OnBeforeGetUnprotectedAsync is { } beforeCallback)
        {
            await beforeCallback(key);
        }

        if (IsUnprotectedReadFailed?.Invoke(key) == true)
        {
            return new StorageResult<T>(success: false, value: default) { ReadFailed = true };
        }

        // Callbacks return (false, null) for keys they don't provide, so those keys fall back to saved values. Use
        // IsUnprotectedReadFailed to simulate a failed read.
        if (OnGetUnprotectedAsync is { } callback)
        {
            var (success, value) = callback(key);
            if (success)
            {
                return new StorageResult<T>(success: success, value: (T)(value ?? default(T))!);
            }
        }

        // Like a browser, return what was saved. Settings stores read the stored value before every save, so a
        // storage that forgot saved values would make them drop their own changes.
        lock (_unprotectedValues)
        {
            if (_unprotectedValues.TryGetValue(key, out var savedValue) && savedValue is T typedValue)
            {
                return new StorageResult<T>(success: true, value: typedValue);
            }
        }

        return new StorageResult<T>(success: false, value: default);
    }

    public Task<StorageResult<string>> GetRawUnprotectedAsync(string key)
    {
        if (IsUnprotectedReadFailed?.Invoke(key) == true)
        {
            return Task.FromResult(new StorageResult<string>(success: false, value: null) { ReadFailed = true });
        }

        lock (_unprotectedValues)
        {
            return Task.FromResult(_unprotectedValues.TryGetValue(key, out var savedValue) && savedValue is string text
                ? new StorageResult<string>(success: true, value: text)
                : new StorageResult<string>(success: false, value: null));
        }
    }

    public Task SetRawUnprotectedAsync(string key, string value)
    {
        lock (_unprotectedValues)
        {
            _unprotectedValues[key] = value;
        }
        return Task.CompletedTask;
    }

    public Task SetAsync<T>(string key, T value)
    {
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
}
