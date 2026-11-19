// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestManifestExpression(string expression, params object[] references) : IManifestExpressionProvider, IValueWithReferences
{
    public string ValueExpression => expression;

    public IEnumerable<object> References => references;
}
