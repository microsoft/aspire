// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Bunit;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TestJSObjectReference : IJSObjectReference
{
    private int _disposeCount;

    public ConcurrentQueue<Invocation> Invocations { get; } = new();
    // Calls made after disposal, which throw ObjectDisposedException like Microsoft.JSInterop's JSObjectReference.
    public ConcurrentQueue<string> RejectedInvocations { get; } = new();
    public Func<string, Task>? BeforeInvokeAsync { get; init; }
    // The value a call returns, for example another reference for a call that creates a JavaScript object. Calls return
    // default when it's not set or gives a value of another type.
    public Func<string, object?>? GetResult { get; init; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public static JSRuntimeInvocationHandler<IJSObjectReference> SetupImport(BunitContext context, string modulePath)
    {
        // A custom handler lets tests delay import and count disposal of the returned reference.
        var handler = new ImportHandler(modulePath);
        context.JSInterop.AddInvocationHandler(handler);
        return handler;
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (DisposeCount != 0)
        {
            RejectedInvocations.Enqueue(identifier);
            throw new ObjectDisposedException(GetType().FullName);
        }
        Invocations.Enqueue(new(identifier, args ?? []));
        if (BeforeInvokeAsync is { } beforeInvoke)
        {
            await beforeInvoke(identifier);
        }
        return GetResult?.Invoke(identifier) is TValue result ? result : default!;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }

    public sealed record Invocation(string Identifier, object?[] Arguments);

    private sealed class ImportHandler(string modulePath) : JSRuntimeInvocationHandler<IJSObjectReference>(
        invocation => invocation.Identifier == "import" && Equals(invocation.Arguments[0], modulePath),
        isCatchAllHandler: false);
}
