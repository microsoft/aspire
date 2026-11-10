// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestManifestValue(
    string expression,
    Func<ValueProviderContext, CancellationToken, ValueTask<string?>> resolve,
    params object[] references) : IExpressionValue, IValueWithReferences
{
    public string ValueExpression => expression;

    public IEnumerable<object> References => references;

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
        GetValueAsync(new ValueProviderContext(), cancellationToken);

    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
        resolve(context, cancellationToken);
}
