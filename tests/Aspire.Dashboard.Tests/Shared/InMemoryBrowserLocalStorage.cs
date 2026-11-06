// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Serialization;
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
        var jsRuntime = new LocalStorageJSRuntime(Items);
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

    public ILocalStorage Storage { get; }

    // This runtime is the fake browser, so its calls don't cross a circuit and can't be disconnected.
#pragma warning disable BL0016 // JS interop call is not guarded with a try/catch block.
    private sealed class LocalStorageJSRuntime(Dictionary<string, string> items) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args)
            => HandleLocalStorageCall<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => HandleLocalStorageCall<TValue>(identifier, args);
#pragma warning restore BL0016

        private ValueTask<TValue> HandleLocalStorageCall<TValue>(string identifier, object?[]? args)
        {
            var key = (string)args![0]!;
            lock (items)
            {
                switch (identifier)
                {
                    case "localStorage.getItem":
                        return ValueTask.FromResult(items.TryGetValue(key, out var json) ? (TValue)(object)json : default!);
                    case "localStorage.setItem":
                        items[key] = (string)args[1]!;
                        return default;
                    default:
                        throw new NotSupportedException($"Unexpected JavaScript call '{identifier}'.");
                }
            }
        }
    }
}
