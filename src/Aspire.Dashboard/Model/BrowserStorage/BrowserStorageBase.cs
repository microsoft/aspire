// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
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
            return new StorageResult<TValue>(false, default) { ReadFailed = true };
        }
        catch (Exception ex) when (ex is JSException or OperationCanceledException)
        {
            // The browser failed or didn't answer in time. Reading again later can succeed.
            Logger.LogInformation(ex, "Error when reading '{Key}' as {ValueType}.", key, typeof(TValue).Name);

            return new StorageResult<TValue>(false, default) { ReadFailed = true };
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            // The stored value can't be deserialized, or was protected with another data protection key.
            Logger.LogInformation(ex, "Error when reading '{Key}' as {ValueType}.", key, typeof(TValue).Name);

            return new StorageResult<TValue>(false, default) { InvalidValue = true };
        }
        catch (Exception ex)
        {
            // Anything else, such as a type missing from the JSON serializer context, fails the same way on every read.
            // It's reported as an invalid value rather than a transient failure so it isn't retried, and logged as a
            // warning because it's a bug rather than something about the stored value.
            Logger.LogWarning(ex, "Error when reading '{Key}' as {ValueType}.", key, typeof(TValue).Name);

            return new StorageResult<TValue>(false, default) { InvalidValue = true };
        }
    }

    public async Task SetAsync<TValue>(string key, TValue value)
    {
        await _protectedBrowserStorage.SetAsync(key, value!).ConfigureAwait(false);
    }
}
