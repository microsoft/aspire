// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model.BrowserStorage;

public class LocalBrowserStorage : BrowserStorageBase, ILocalStorage
{
    private readonly IJSRuntime _jsRuntime;
    private readonly JsonSerializerOptions _serializerOptions;

    public LocalBrowserStorage(
        IJSRuntime jsRuntime,
        ProtectedLocalStorage protectedLocalStorage,
        ILogger<LocalBrowserStorage> logger,
        IOptions<CircuitOptions> circuitOptions) : base(protectedLocalStorage, logger)
    {
        _jsRuntime = jsRuntime;
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

    private Task<StorageResult<TValue>> GetJsonAsync<TValue>(string key)
        => ReadAsync(key, async () =>
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key).ConfigureAwait(false);
            if (json is null)
            {
                return new StorageResult<TValue>(false, default);
            }

            var typeInfo = (JsonTypeInfo<TValue>)_serializerOptions.GetTypeInfo(typeof(TValue));
            return new StorageResult<TValue>(true, JsonSerializer.Deserialize(json, typeInfo));
        }, LogLevel.Warning);

    public async Task SetUnprotectedAsync<TValue>(string key, TValue value)
    {
        var typeInfo = (JsonTypeInfo<TValue>)_serializerOptions.GetTypeInfo(typeof(TValue));
        var json = JsonSerializer.Serialize(value, typeInfo);

        await SetJsonAsync(key, json).ConfigureAwait(false);
    }

    private ValueTask SetJsonAsync(string key, string json)
        => _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, json);
}
