// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model.BrowserStorage;

public class LocalBrowserStorage : BrowserStorageBase, ILocalStorage
{
    // ProtectedLocalStorage stores IDataProtector.Protect(json) under the key, protected with a purpose derived from its
    // type, store name and key:
    //   Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedLocalStorage:localStorage:<key>
    // Snapshots unprotect with the same purpose so they read values written by SetAsync. See
    // https://github.com/dotnet/aspnetcore/blob/main/src/Components/Server/src/ProtectedBrowserStorage/ProtectedBrowserStorage.cs
    private static readonly string s_protectedPurposePrefix = $"{typeof(ProtectedLocalStorage).FullName}:localStorage:";

    private readonly IJSRuntime _jsRuntime;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ConcurrentDictionary<string, IDataProtector> _protectorsByKey = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _serializerOptions;

    public LocalBrowserStorage(
        IJSRuntime jsRuntime,
        ProtectedLocalStorage protectedLocalStorage,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<LocalBrowserStorage> logger,
        IOptions<CircuitOptions> circuitOptions) : base(protectedLocalStorage, logger)
    {
        _jsRuntime = jsRuntime;
        _dataProtectionProvider = dataProtectionProvider;
        // Match the options ProtectedLocalStorage serializes with, so protected values decoded from a snapshot
        // deserialize the same way as with GetAsync.
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

#pragma warning disable ASPNETCORE9004 // Native AOT resolver composition is experimental in .NET 11.
        foreach (var resolver in circuitOptions.Value.JsonTypeInfoResolvers)
        {
            _serializerOptions.TypeInfoResolverChain.Add(resolver);
        }
#pragma warning restore ASPNETCORE9004
    }

    public Task<StorageResult<TValue>> GetUnprotectedAsync<TValue>(string key) => GetJsonAsync<TValue>(key);

    public async Task<ILocalStorageSnapshot> ReadSnapshotAsync(string[] keys)
    {
        keys = keys.ToArray();
        var result = await ReadAsync("local storage snapshot", async () =>
        {
            var values = await _jsRuntime.InvokeAsync<string?[]>("getLocalStorageItems", (object)keys).ConfigureAwait(false);
            return new StorageResult<string?[]>(true, values);
        }).ConfigureAwait(false);

        return new Snapshot(this, keys, result);
    }

    private Task<StorageResult<TValue>> GetJsonAsync<TValue>(string key)
        => ReadAsync(key, async () =>
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key).ConfigureAwait(false);
            return Deserialize<TValue>(json);
        });

    private StorageResult<TValue> Deserialize<TValue>(string? json)
    {
        if (json is null)
        {
            return new StorageResult<TValue>(false, default);
        }

        var typeInfo = (JsonTypeInfo<TValue>)_serializerOptions.GetTypeInfo(typeof(TValue));
        return new StorageResult<TValue>(true, JsonSerializer.Deserialize(json, typeInfo));
    }

    private StorageResult<TValue> Unprotect<TValue>(string key, string? protectedJson)
    {
        if (protectedJson is null)
        {
            return new StorageResult<TValue>(false, default);
        }

        var protector = _protectorsByKey.GetOrAdd(
            key,
            static (key, provider) => provider.CreateProtector(s_protectedPurposePrefix + key),
            _dataProtectionProvider);
        return Deserialize<TValue>(protector.Unprotect(protectedJson));
    }

    public async Task SetUnprotectedAsync<TValue>(string key, TValue value)
    {
        var typeInfo = (JsonTypeInfo<TValue>)_serializerOptions.GetTypeInfo(typeof(TValue));
        var json = JsonSerializer.Serialize(value, typeInfo);

        await SetJsonAsync(key, json).ConfigureAwait(false);
    }

    private ValueTask SetJsonAsync(string key, string json)
        => _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, json);

    private sealed class Snapshot(LocalBrowserStorage storage, string[] keys, StorageResult<string?[]> result)
        : ILocalStorageSnapshot
    {
        public StorageResult<TValue> Get<TValue>(string key) => Read(key, storage.Unprotect<TValue>);

        public StorageResult<TValue> GetUnprotected<TValue>(string key) => Read(key, (_, json) => storage.Deserialize<TValue>(json));

        private StorageResult<TValue> Read<TValue>(string key, Func<string, string?, StorageResult<TValue>> decode)
        {
            var index = Array.IndexOf(keys, key);
            if (index < 0)
            {
                throw new ArgumentException($"Key '{key}' was not requested when loading the snapshot.", nameof(key));
            }

            if (!result.Success)
            {
                return new StorageResult<TValue>(false, default) { ReadFailed = result.ReadFailed, InvalidValue = result.InvalidValue };
            }

            var value = result.Value![index];
            return storage.Read(key, () => decode(key, value));
        }
    }
}
