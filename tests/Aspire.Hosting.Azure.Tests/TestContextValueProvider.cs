// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Tests;

internal sealed class TestContextValueProvider(Func<ValueProviderContext, CancellationToken, ValueTask<string?>> callback) : IValueProvider, IManifestExpressionProvider
{
    public string ValueExpression => "{test.value}";

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A value provider context is required.");

    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
        callback(context, cancellationToken);
}
