// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Tests;

internal sealed class TestValueProvider(
    string value,
    string valueExpression = "{test-value}") : IValueProvider, IManifestExpressionProvider
{
    private int _getValueCallCount;

    public string ValueExpression { get; } = valueExpression;

    public int GetValueCallCount => Volatile.Read(ref _getValueCallCount);

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getValueCallCount);
        return new(value);
    }

    public ValueTask<string?> GetValueAsync(
        ValueProviderContext context,
        CancellationToken cancellationToken = default)
        => GetValueAsync(cancellationToken);
}
