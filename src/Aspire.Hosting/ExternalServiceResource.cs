// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Represents an external service resource with service discovery capabilities.
/// </summary>
public sealed class ExternalServiceResource : Resource
{
    private readonly Uri? _uri;
    private readonly ParameterResource? _urlParameter;

    /// <summary>
    /// Creates a new instance of <see cref="ExternalServiceResource"/> with a specified name and URI.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="uri">The URI for the external service.</param>
    /// <remarks>
    /// The URI must be an absolute URI with the absolute path ending with '/'.
    /// The URI cannot contain a fragment or query string.
    /// </remarks>
    public ExternalServiceResource(string name, Uri uri) : base(name)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri), "The URI for the external service cannot be null.");

        if (GetUriValidationException(_uri) is { } exception)
        {
            throw exception;
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="ExternalServiceResource"/> with a specified name and URL parameter.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="urlParameter">The parameter to use for the URL of the external service.</param>
    public ExternalServiceResource(string name, ParameterResource urlParameter) : base(name)
    {
        _urlParameter = urlParameter ?? throw new ArgumentNullException(nameof(urlParameter), "The URL parameter for the external service cannot be null.");
    }

    /// <summary>
    /// Gets the URI for the external service, if defined.
    /// </summary>
    /// <remarks>
    /// If <see cref="Uri"/> is <c>null</c>, the external service URL is parameterized and can be accessed via <see cref="UrlParameter"/>."/>
    /// </remarks>
    public Uri? Uri => _uri;

    /// <summary>
    /// Gets the URL parameter for the external service, if defined.
    /// </summary>
    /// <remarks>
    /// If <see cref="UrlParameter"/> is <c>null</c>, the external service URL is not parameterized and can be accessed directly via <see cref="Uri"/>.
    /// </remarks>
    public ParameterResource? UrlParameter => _urlParameter;

    internal static bool UrlIsValidForExternalService(string? url, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out string? message)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            uri = null;
            message = "The URL for the external service must be an absolute URI.";
            return false;
        }

        if (GetUriValidationException(uri) is { } exception)
        {
            message = exception.Message;
            uri = null;
            return false;
        }

        message = null;

        return true;
    }

    private static ArgumentException? GetUriValidationException(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return new ArgumentException("The URI for the external service must be absolute.", nameof(uri));
        }
        if (!uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return new ArgumentException("The URI absolute path must end with '/'.", nameof(uri));
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return new ArgumentException("The URI cannot contain a fragment.", nameof(uri));
        }
        if (!string.IsNullOrEmpty(uri.Query))
        {
            return new ArgumentException("The URI cannot contain a query string.", nameof(uri));
        }
        return null;
    }
}
