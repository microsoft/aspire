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
    public Func<string, Task>? BeforeInvokeAsync { get; init; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public static JSRuntimeInvocationHandler<IJSObjectReference> SetupImport(TestContext context, string modulePath)
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
        ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
        Invocations.Enqueue(new(identifier, args ?? []));
        if (BeforeInvokeAsync is { } beforeInvoke)
        {
            await beforeInvoke(identifier);
        }
        return default!;
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
