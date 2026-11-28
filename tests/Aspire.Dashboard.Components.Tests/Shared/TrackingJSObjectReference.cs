// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TrackingJSObjectReference : IJSObjectReference
{
    private readonly TaskCompletionSource _disposedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsDisposed => _disposedTcs.Task.IsCompleted;

    public Task DisposedTask => _disposedTcs.Task;

    public List<string> InvokedIdentifiers { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        InvokedIdentifiers.Add(identifier);
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        InvokedIdentifiers.Add(identifier);
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask DisposeAsync()
    {
        _disposedTcs.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal sealed class InterceptingJSRuntime(
    IJSRuntime inner,
    Func<string, object?[]?, ValueTask<IJSObjectReference>?> interceptObjectReference) : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        if (typeof(TValue) == typeof(IJSObjectReference) &&
            interceptObjectReference(identifier, args) is { } intercepted)
        {
            return (ValueTask<TValue>)(object)intercepted;
        }

        return inner.InvokeAsync<TValue>(identifier, args);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (typeof(TValue) == typeof(IJSObjectReference) &&
            interceptObjectReference(identifier, args) is { } intercepted)
        {
            return (ValueTask<TValue>)(object)intercepted;
        }

        return inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
    }
}
