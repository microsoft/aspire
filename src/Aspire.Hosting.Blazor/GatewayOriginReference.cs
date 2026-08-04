// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Wraps a gateway <see cref="EndpointReference"/> so publishers can emit a deployer-configurable
/// placeholder (e.g., <c>${GATEWAY_BINDINGS_HTTPS_URL}</c>) while dev mode resolves the actual URL.
/// </summary>
internal sealed class GatewayOriginReference(EndpointReference endpoint) : IValueProvider, IManifestExpressionProvider
{
    public string ValueExpression => $"{{{endpoint.Resource.Name}.bindings.{endpoint.EndpointName}.url}}";

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var url = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return url?.TrimEnd('/');
    }
}
