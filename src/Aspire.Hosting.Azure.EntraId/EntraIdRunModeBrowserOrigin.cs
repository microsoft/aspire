// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Bounds the wait for browser URLs when provisioning an Entra redirect URI.
/// </summary>
internal sealed class EntraIdRunModeBrowserOrigin(
    EndpointReference endpoint,
    DistributedApplicationExecutionContext executionContext) : IExpressionValue, IValueWithReferences
{
    public string ValueExpression => endpoint.BrowserOrigins.First.ValueExpression;

    public IEnumerable<object> References => [endpoint];

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        // Preserve the existing Entra wait boundary: allocation (which can include a slow
        // build) is not part of the 30-second budget for URL customization.
        var allocatedUrl = await endpoint.Property(EndpointProperty.Url).GetValueAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await endpoint.BrowserOrigins.First.GetValueAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Slow or indirectly dependent callbacks previously used the allocated URL.
            // Keep that compatibility fallback local to Entra, not general BrowserOrigins.
            var origin = new Uri(allocatedUrl!, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
            executionContext.Services.GetRequiredService<ResourceLoggerService>().GetLogger(endpoint.Resource).LogWarning(
                "Browser URL finalization for endpoint '{EndpointName}' on resource '{ResourceName}' did not complete within 30 seconds. " +
                "Using allocated origin '{Origin}' for the Entra redirect URI. This may differ from a custom browser URL; configure an explicit redirect URI if necessary.",
                endpoint.EndpointName, endpoint.Resource.Name, origin);
            return origin;
        }
    }
}
