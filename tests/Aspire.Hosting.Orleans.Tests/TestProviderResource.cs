// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans.Tests;

public sealed class TestProviderResource(string name) : Resource(name), IResourceWithConnectionString
{
    public string? ConnectionString { get; init; }

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{ConnectionString}");
}
