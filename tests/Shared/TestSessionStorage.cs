// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.BrowserStorage;

namespace Aspire.Dashboard.Tests.Shared;

public sealed class TestSessionStorage : ISessionStorage
{
    public Func<string, (bool Success, object? Value)>? OnGetAsync { get; set; }
    public Action<string, object?>? OnSetAsync { get; set; }

    public Task<StorageResult<T>> GetAsync<T>(string key)
    {
        if (OnGetAsync is { } callback)
        {
            var (success, value) = callback(key);
            return Task.FromResult(new StorageResult<T>(success: success, value: (T)(value ?? default(T))!));
        }

        return Task.FromResult<StorageResult<T>>(new StorageResult<T>(success: false, value: default));
    }

    public Task SetAsync<T>(string key, T value)
    {
        if (OnSetAsync is { } callback)
        {
            callback(key, value);
        }

        return Task.CompletedTask;
    }
}
