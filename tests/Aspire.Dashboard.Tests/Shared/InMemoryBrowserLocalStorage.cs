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
        Storage = new LocalBrowserStorage(
            jsRuntime,
            new ProtectedLocalStorage(jsRuntime, new EphemeralDataProtectionProvider()),
            NullLogger<LocalBrowserStorage>.Instance,
            Options.Create(circuitOptions));
    }

    /// <summary>
    /// Gets the items in local storage, as JSON keyed by storage key.
    /// </summary>
    public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether reading local storage fails, as when the circuit is disconnected.
    /// </summary>
    public bool FailReads { get; set; }

    public ILocalStorage Storage { get; }

    /// <summary>
    /// Waits until a key has a value, for saves that are retried after a delay.
    /// </summary>
    public async Task WaitForItemAsync(string key, string expectedJson)
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        while (true)
        {
            lock (Items)
            {
                if (Items.TryGetValue(key, out var json) && json == expectedJson)
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
        }
    }

    private object? HandleLocalStorageCall((string Identifier, object?[]? Args) call)
    {
        var key = (string)call.Args![0]!;
        lock (Items)
        {
            switch (call.Identifier)
            {
                case "localStorage.getItem":
                    if (FailReads)
                    {
                        throw new JSException("Local storage is unavailable.");
                    }
                    return Items.GetValueOrDefault(key);
                case "localStorage.setItem":
                    Items[key] = (string)call.Args[1]!;
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected JavaScript call '{call.Identifier}'.");
            }
        }
    }
}
