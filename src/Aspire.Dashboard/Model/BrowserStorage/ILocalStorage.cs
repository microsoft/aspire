// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.BrowserStorage;

public interface IUnprotectedLocalStorageSnapshot
{
    /// <summary>
    /// Reads a value from the snapshot without accessing the browser.
    /// </summary>
    /// <exception cref="ArgumentException">The key was not requested when the snapshot was loaded.</exception>
    StorageResult<TValue> Get<TValue>(string key);
}

public interface ILocalStorage : IBrowserStorage
{
    /// <summary>
    /// Get unprotected data from local storage. This must only be used with non-sensitive data.
    /// </summary>
    Task<StorageResult<TValue>> GetUnprotectedAsync<TValue>(string key);
    /// <summary>
    /// Loads the requested keys from local storage in one browser call. Use only with non-sensitive data.
    /// </summary>
    Task<IUnprotectedLocalStorageSnapshot> ReadUnprotectedSnapshotAsync(string[] keys);

    /// <summary>
    /// Set unprotected data to local storage. This must only be used with non-sensitive data.
    /// </summary>
    Task SetUnprotectedAsync<TValue>(string key, TValue value);
}
