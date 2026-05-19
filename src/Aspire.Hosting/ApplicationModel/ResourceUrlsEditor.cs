// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides an ATS-first editor for resource URLs within polyglot callbacks.
/// </summary>
[AspireExport]
internal sealed class ResourceUrlsEditor(DistributedApplicationExecutionContext executionContext, List<ResourceUrlAnnotation> urls, CancellationToken cancellationToken = default)
{
    private readonly DistributedApplicationExecutionContext _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
    private readonly List<ResourceUrlAnnotation> _urls = urls ?? throw new ArgumentNullException(nameof(urls));
    private readonly CancellationToken _cancellationToken = cancellationToken;

    /// <summary>
    /// Adds a displayed URL.
    /// </summary>
    /// <param name="url">The URL to add, specified as a string or reference expression.</param>
    /// <param name="displayText">The optional display text to show for the URL.</param>
    [AspireExport("ResourceUrlsEditor.add", MethodName = "add", Description = "Adds a displayed URL")]
    public async Task Add([AspireUnion(typeof(string), typeof(ReferenceExpression))] object url, string? displayText = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        switch (url)
        {
            case string stringUrl:
                AddUrlAnnotation(stringUrl, displayText);
                break;
            case ReferenceExpression referenceExpression:
                var endpoint = referenceExpression.ValueProviders.OfType<EndpointReference>().FirstOrDefault();
                var urlValue = await referenceExpression.GetValueAsync(_cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(urlValue))
                {
                    AddUrlAnnotation(urlValue, displayText, endpoint);
                }
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported URL type '{url.GetType().Name}'. Expected string or ReferenceExpression.",
                    nameof(url));
        }
    }

    /// <summary>
    /// Adds a displayed URL for a specific endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint the URL is associated with.</param>
    /// <param name="url">The URL to add, specified as a string or reference expression.</param>
    /// <param name="displayText">The optional display text to show for the URL.</param>
    [AspireExport("ResourceUrlsEditor.addForEndpoint", MethodName = "addForEndpoint", Description = "Adds a displayed URL for a specific endpoint")]
    public async Task AddForEndpoint(EndpointReference endpoint, [AspireUnion(typeof(string), typeof(ReferenceExpression))] object url, string? displayText = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(url);

        switch (url)
        {
            case string stringUrl:
                AddUrlAnnotation(stringUrl, displayText, endpoint);
                break;
            case ReferenceExpression referenceExpression:
                var urlValue = await referenceExpression.GetValueAsync(_cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(urlValue))
                {
                    AddUrlAnnotation(urlValue, displayText, endpoint);
                }
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported URL type '{url.GetType().Name}'. Expected string or ReferenceExpression.",
                    nameof(url));
        }
    }

    /// <summary>
    /// Gets the execution context associated with this editor.
    /// </summary>
    [AspireExport(Description = "Gets the execution context for this URL editor")]
    public DistributedApplicationExecutionContext ExecutionContext => _executionContext;

    private void AddUrlAnnotation(string url, string? displayText, EndpointReference? endpoint = null)
    {
        _urls.Add(new ResourceUrlAnnotation
        {
            Endpoint = endpoint,
            Url = url,
            DisplayText = displayText
        });
    }
}
