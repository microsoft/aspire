// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Bounds the wait for browser URLs when provisioning an Entra redirect URI.
/// </summary>
internal sealed class EntraIdRunModeBrowserOrigin(EndpointReference endpoint) : IExpressionValue, IValueWithReferences
{
    public string ValueExpression => endpoint.BrowserOrigins.First.ValueExpression;

    public IEnumerable<object> References => [endpoint];

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        // Endpoint allocation can include a slow build. Bound URL finalization separately,
        // without using the allocated address as a fallback redirect origin.
        await endpoint.EndpointAnnotation.AllAllocatedEndpoints.GetAllocatedEndpointAsync(
            KnownNetworkIdentifiers.LocalhostNetwork, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await endpoint.BrowserOrigins.First.GetValueAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A URL callback can wait for this registration's ClientId output while provisioning
            // needs its finalized URL. Fail that indirect cycle instead of registering a different
            // allocated origin or leaving every workload blocked on endpoint finalization.
            throw new InvalidOperationException(
                $"Timed out resolving the browser origin for endpoint '{endpoint.EndpointName}' on resource '{endpoint.Resource.Name}' for an Entra redirect URI. " +
                "URL callbacks must not wait for provisioning that depends on their finalized URLs. Register an explicit redirect URI to break the dependency cycle.", ex);
        }
    }
}
