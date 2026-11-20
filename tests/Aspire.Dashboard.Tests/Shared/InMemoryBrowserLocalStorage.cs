// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Serialization;
using Aspire.Tests.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Tests.Shared;

/// <summary>
/// The dashboard's <see cref="LocalBrowserStorage"/> over an in-memory browser <c>localStorage</c>, so tests cover the
/// JSON the dashboard stores, including its source-generated serialization, like a browser does.
/// </summary>
internal sealed class InMemoryBrowserLocalStorage
{
    public InMemoryBrowserLocalStorage()
    {
        var jsRuntime = new TestJSRuntime { OnInvoke = HandleLocalStorageCall };
        var circuitOptions = new CircuitOptions();
#pragma warning disable ASPNETCORE9004 // Native AOT resolver composition is experimental in .NET 11.
        // Matches the resolvers the dashboard registers, so a stored type missing from the context fails here too.
        circuitOptions.JsonTypeInfoResolvers.Add(DashboardJsonSerializerContext.Default);
#pragma warning restore ASPNETCORE9004
        Storage = new DelayedReadLocalStorage(new LocalBrowserStorage(
            jsRuntime,
            new ProtectedLocalStorage(jsRuntime, new EphemeralDataProtectionProvider()),
            NullLogger<LocalBrowserStorage>.Instance,
            Options.Create(circuitOptions)), this);
    }

    /// <summary>
    /// Gets the raw items in local storage, keyed by storage key.
    /// </summary>
    public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether reading local storage fails, as when the circuit is disconnected.
    /// </summary>
    public bool FailReads { get; set; }

    public ILocalStorage Storage { get; }
    public int SnapshotReadCount { get; private set; }

    /// <summary>
    /// Gets or sets a function awaited after a read of local storage and before its result is returned, to simulate
    /// a response that arrives after later calls have completed.
    /// </summary>
    public Func<Task>? OnAfterReadAsync { get; set; }

    /// <summary>
    /// Waits until a protected value matches, for saves that are retried after a delay.
    /// </summary>
    public async Task WaitForProtectedItemAsync<TValue>(string key, Func<TValue, bool> matches) where TValue : class
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        while (true)
        {
            var result = await Storage.GetAsync<TValue>(key);
            if (result.Success && result.Value is { } value && matches(value))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
        }
    }

    private object? HandleLocalStorageCall((string Identifier, object?[]? Args) call)
    {
        lock (Items)
        {
            switch (call.Identifier)
            {
                case "localStorage.getItem":
                    if (FailReads)
                    {
                        throw new JSException("Local storage is unavailable.");
                    }
                    return Items.GetValueOrDefault((string)call.Args![0]!);
                case "getLocalStorageItems":
                    SnapshotReadCount++;
                    if (FailReads)
                    {
                        throw new JSException("Local storage is unavailable.");
                    }
                    return ((string[])call.Args![0]!).Select(key => Items.GetValueOrDefault(key)).ToArray();
                case "localStorage.setItem":
                    Items[(string)call.Args![0]!] = (string)call.Args[1]!;
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected JavaScript call '{call.Identifier}'.");
            }
        }
    }

    private sealed class DelayedReadLocalStorage(ILocalStorage inner, InMemoryBrowserLocalStorage owner) : ILocalStorage
    {
        public Task<StorageResult<TValue>> GetUnprotectedAsync<TValue>(string key) => inner.GetUnprotectedAsync<TValue>(key);

        public Task SetUnprotectedAsync<TValue>(string key, TValue value) => inner.SetUnprotectedAsync(key, value);

        public async Task<StorageResult<TValue>> GetAsync<TValue>(string key)
        {
            var result = await inner.GetAsync<TValue>(key);
            if (owner.OnAfterReadAsync is { } afterRead)
            {
                await afterRead();
            }
            return result;
        }

        public Task<IUnprotectedLocalStorageSnapshot> ReadUnprotectedSnapshotAsync(string[] keys) => inner.ReadUnprotectedSnapshotAsync(keys);

        public Task SetAsync<TValue>(string key, TValue value) => inner.SetAsync(key, value);
    }
}
