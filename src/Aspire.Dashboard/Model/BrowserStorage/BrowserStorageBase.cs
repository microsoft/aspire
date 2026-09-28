// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model.BrowserStorage;

public abstract class BrowserStorageBase : IBrowserStorage
{
    private readonly ProtectedBrowserStorage _protectedBrowserStorage;

    protected BrowserStorageBase(ProtectedBrowserStorage protectedBrowserStorage, ILogger logger)
    {
        _protectedBrowserStorage = protectedBrowserStorage;
        Logger = logger;
    }

    public ILogger Logger { get; }

    public Task<StorageResult<TValue>> GetAsync<TValue>(string key)
    {
        return ReadAsync(key, async () =>
        {
            var result = await _protectedBrowserStorage.GetAsync<TValue>(key).ConfigureAwait(false);
            return new StorageResult<TValue>(result.Success, result.Value);
        });
    }

    protected async Task<StorageResult<TValue>> ReadAsync<TValue>(string key, Func<Task<StorageResult<TValue>>> getValue)
    {
        // Possible errors here:
        // - Client is disconnected from the server.
        // - Saved value in storage can't be deserialized to TValue.
        // - Saved value has a different data protection key than the current one.
        //   This could happen with values saved to persistent browser and the user upgrades Aspire version, which has a different
        //   install location and so a different data protection key.
        //   It could also be caused by standalone dashboard, which creates a new key each run. Leaving the dashboard browser open
        //   while restarting the container will cause a new data protection key, even with session storage.
        try
        {
            return await getValue().ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // A disconnected circuit cannot read browser storage; avoid recording it as a storage error.
            return new StorageResult<TValue>(false, default);
        }
        catch (Exception ex)
        {
            Logger.LogInformation(ex, "Error when reading '{Key}' as {ValueType}.", key, typeof(TValue).Name);

            return new StorageResult<TValue>(false, default);
        }
    }

    public async Task SetAsync<TValue>(string key, TValue value)
    {
        await _protectedBrowserStorage.SetAsync(key, value!).ConfigureAwait(false);
    }
}
