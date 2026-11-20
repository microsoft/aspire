// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.BrowserStorage;

public interface ILocalStorage : IBrowserStorage
{
    /// <summary>
    /// Get unprotected data from local storage. This must only be used with non-sensitive data.
    /// </summary>
    Task<StorageResult<TValue>> GetUnprotectedAsync<TValue>(string key);

    /// <summary>
    /// Set unprotected data to local storage. This must only be used with non-sensitive data.
    /// </summary>
    Task SetUnprotectedAsync<TValue>(string key, TValue value);
}
