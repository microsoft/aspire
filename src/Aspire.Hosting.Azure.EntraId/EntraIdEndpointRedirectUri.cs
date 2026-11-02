// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource.GetHostAddressExpression is experimental.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A redirect URI whose value is computed from other model values when the registration is provisioned.
/// </summary>
internal interface IEntraIdDerivedRedirectUri
{
    ReferenceExpression CreateExpression();
}

/// <summary>
/// A redirect URI built from a host name supplied by a parameter and a caller-supplied callback path.
/// </summary>
internal sealed class EntraIdHostRedirectUri(ParameterResource host, string path) : IEntraIdDerivedRedirectUri
{
    // The value is a host name (for example, app.contoso.com), so the scheme is always https.
    public ReferenceExpression CreateExpression() => ReferenceExpression.Create($"https://{host}{path}");
}

/// <summary>
/// A redirect URI built from a resource endpoint and a caller-supplied callback path.
/// </summary>
internal sealed class EntraIdEndpointRedirectUri : IEntraIdDerivedRedirectUri
{
    private readonly bool _isPublishMode;
    private readonly RunModeOrigin? _runModeOrigin;

    public EntraIdEndpointRedirectUri(EndpointReference endpoint, string path, bool isPublishMode)
    {
        Endpoint = endpoint;
        Path = path;
        _isPublishMode = isPublishMode;

        if (!isPublishMode)
        {
            _runModeOrigin = new RunModeOrigin(endpoint);
            endpoint.Resource.Annotations.Add(new ResourceUrlsCallbackAnnotation(_runModeOrigin.CaptureUrl));
        }
    }

    public EndpointReference Endpoint { get; }

    public string Path { get; }

    public ReferenceExpression CreateExpression()
    {
        // The callback path is configured by the consuming application (for example, Microsoft.Identity.Web's
        // CallbackPath), so it can't be discovered from the endpoint and is supplied by the AppHost.
        if (!_isPublishMode)
        {
            return ReferenceExpression.Create($"{_runModeOrigin!}{Path}");
        }

        // Publish/deploy: ask the compute environment that hosts the resource for its public host name, the same way
        // Azure Front Door resolves origin host names. Deployed endpoints are always served over HTTPS.
        if (!ComputeEnvironmentEndpointResolver.TryGetEffectiveComputeEnvironment(Endpoint.Resource, out var computeEnvironment))
        {
            throw new InvalidOperationException(
                $"Resource '{Endpoint.Resource.Name}' does not have a compute environment. " +
                "Ensure a compute environment (for example, Azure Container Apps or Azure App Service) is configured in the application model " +
                "or register the redirect URI with an explicit URL.");
        }

        var host = computeEnvironment.GetHostAddressExpression(Endpoint);
        return ReferenceExpression.Create($"https://{host}{Path}");
    }

    /// <summary>
    /// Resolves the scheme, host, and port that users browse to when running locally.
    /// </summary>
    /// <remarks>
    /// The allocated endpoint is always <c>localhost</c>, but when the endpoint targets a <c>*.localhost</c> host or the
    /// dashboard uses a <c>*.dev.localhost</c> name, the orchestrator makes a friendly URL such as
    /// <c>https://web-myapp.dev.localhost:7081</c> the resource's primary URL. Microsoft Entra ID matches the host
    /// exactly for such names (only plain <c>localhost</c> ignores the port), so the redirect URI must use the same URL
    /// the user navigates to. The orchestrator computes that URL when it runs the resource's URL callbacks, which
    /// happens after the endpoint is allocated, so this waits for the callback.
    /// </remarks>
    private sealed class RunModeOrigin(EndpointReference endpoint) : IValueProvider, IManifestExpressionProvider
    {
        private readonly TaskCompletionSource<string> _origin = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ValueExpression => endpoint.Property(EndpointProperty.Url).ValueExpression;

        public void CaptureUrl(ResourceUrlsCallbackContext context)
        {
            // The primary URL is the one shown in the summary; a *.localhost URL replaces the plain localhost one there.
            // Endpoint names are only unique within a resource, and WithUrlForEndpoint can add another resource's
            // endpoint link to this list, so match the owning resource as well.
            var urls = context.Urls
                .Where(u => u.Endpoint is { } e
                    && string.Equals(e.Resource.Name, endpoint.Resource.Name, StringComparison.Ordinal)
                    && string.Equals(e.EndpointName, endpoint.EndpointName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var primary = urls.FirstOrDefault(u => u.DisplayLocation != UrlDisplayLocation.DetailsOnly) ?? urls.FirstOrDefault();

            // The orchestrator turns relative URLs (for example, "/swagger" set by WithUrlForEndpoint) into absolute
            // ones against the allocated endpoint only after every callback has run, so mirror that here. Parsing a
            // relative URL directly would produce a file URI on some platforms.
            var allocatedUrl = endpoint.EndpointAnnotation.AllocatedEndpoint?.UriString;
            var url = primary?.Url;
            if (url is not null && url.StartsWith('/') && allocatedUrl is not null)
            {
                url = allocatedUrl.TrimEnd('/') + url;
            }

            // A callback may have removed every displayed URL for this endpoint. The endpoint is still allocated, so
            // complete with its allocated URL instead of leaving each resolution to wait for the fallback timeout.
            if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                _origin.TrySetResult(uri.GetLeftPart(UriPartial.Authority));
            }
            else if (allocatedUrl is not null && Uri.TryCreate(allocatedUrl, UriKind.Absolute, out var allocatedUri))
            {
                _origin.TrySetResult(allocatedUri.GetLeftPart(UriPartial.Authority));
            }
        }

        public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            // Resolving the endpoint waits for allocation. Use it as the fallback when no URL callback runs for the
            // endpoint (for example, a non-HTTP endpoint), so provisioning never waits indefinitely.
            var allocated = await endpoint.Property(EndpointProperty.Url).GetValueAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await _origin.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return allocated;
            }
        }
    }
}
