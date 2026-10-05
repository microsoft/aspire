// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Tests;

internal sealed class TestValueProvider(
    string value,
    string valueExpression = "{test-value}") : IValueProvider, IManifestExpressionProvider
{
    public string ValueExpression { get; } = valueExpression;

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        => new(value);

    public ValueTask<string?> GetValueAsync(
        ValueProviderContext context,
        CancellationToken cancellationToken = default)
        => new(value);
}
