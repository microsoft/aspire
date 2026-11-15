// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class ConnectionStringParameterResource : ParameterResource, IResourceWithConnectionString
{
    private readonly string? _environmentVariableName;

    public ConnectionStringParameterResource(string name, Func<ParameterDefault?, string> callback, string? environmentVariableName) : base(name, callback, secret: true)
    {
        _environmentVariableName = environmentVariableName;

        IsConnectionString = true;
    }

    string IManifestExpressionProvider.ValueExpression => $"{{{Name}.connectionString}}";

    public string? ConnectionStringEnvironmentVariable => _environmentVariableName;

    ValueTask<string?> IResourceWithConnectionString.GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
        GetValueAsync(context, cancellationToken);

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{this}");
}
