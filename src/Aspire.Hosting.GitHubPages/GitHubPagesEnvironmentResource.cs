// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHubPages;

/// <summary>
/// Represents a GitHub Pages environment that hosts one static website in a repository.
/// </summary>
/// <remarks>
/// The environment has no local runtime. Its addresses describe the intended public site,
/// not a verified deployment. Private enterprise sites and inherited account custom domains
/// require an explicit custom domain and are not discovered during bundle generation.
/// </remarks>
[Experimental("ASPIREGITHUBPAGES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class GitHubPagesEnvironmentResource : Resource, IComputeEnvironmentResource, IContainerFilesDestinationResource
{
    /// <summary>
    /// Initializes a GitHub Pages environment for a repository on github.com.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="repository">The repository in owner/name form.</param>
    /// <exception cref="ArgumentException">Thrown when the repository is not in owner/name form.</exception>
    public GitHubPagesEnvironmentResource(string name, string repository) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts[0].Length is 0 or > 39 || parts[1].Length is 0 or > 100 ||
            parts[0].StartsWith('-') || parts[0].EndsWith('-') ||
            parts[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            parts[1] is "." or ".." ||
            parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("The repository must use owner/name form for a repository on github.com.", nameof(repository));
        }

        Repository = repository;
        DefaultHost = $"{parts[0].ToLowerInvariant()}.github.io";
        DefaultBasePath = string.Equals(parts[1], DefaultHost, StringComparison.OrdinalIgnoreCase) ? "/" : $"/{parts[1]}/";
    }

    /// <summary>
    /// Gets the destination repository in owner/name form.
    /// </summary>
    public string Repository { get; }

    /// <summary>
    /// Gets the configured canonical custom domain, or <see langword="null"/> for the default github.io address.
    /// </summary>
    public string? CustomDomain { get; internal set; }

    /// <summary>
    /// Gets the intended public browser origin, without a path or trailing slash.
    /// </summary>
    public string Origin => $"https://{CustomDomain ?? DefaultHost}";

    /// <summary>
    /// Gets the site's content base path, including its leading and trailing slashes.
    /// </summary>
    public string BasePath => CustomDomain is null ? DefaultBasePath : "/";

    /// <summary>
    /// Gets the intended canonical HTTPS site URL, including the content base path.
    /// </summary>
    public Uri SiteUrl => new($"{Origin}{BasePath}");

    internal string DefaultHost { get; }
    internal string DefaultBasePath { get; }
    internal GitHubPagesSiteResource? Site { get; set; }

    /// <inheritdoc />
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        ValidateEndpoint(endpointReference);
        return ReferenceExpression.Create($"{CustomDomain ?? DefaultHost}");
    }

    /// <inheritdoc />
    public ReferenceExpression GetEndpointPropertyExpression(EndpointReferenceExpression endpointReferenceExpression)
    {
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);
        ValidateEndpoint(endpointReferenceExpression.Endpoint);

        // The local Vite scheme and port describe its dev server, not Pages' HTTPS endpoint.
        // Project sites also need a URL path, which the default compute resolver cannot express.
        return endpointReferenceExpression.Property switch
        {
            EndpointProperty.Url => ReferenceExpression.Create($"{SiteUrl.AbsoluteUri}"),
            EndpointProperty.Host => ReferenceExpression.Create($"{CustomDomain ?? DefaultHost}"),
            EndpointProperty.HostAndPort => ReferenceExpression.Create($"{CustomDomain ?? DefaultHost}:443"),
            EndpointProperty.Port => ReferenceExpression.Create($"443"),
            EndpointProperty.Scheme => ReferenceExpression.Create($"https"),
            EndpointProperty.TlsEnabled => ReferenceExpression.Create($"{bool.TrueString}"),
            _ => throw new InvalidOperationException($"GitHub Pages does not provide endpoint property '{endpointReferenceExpression.Property}'.")
        };
    }

    private void ValidateEndpoint(EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var owner = endpoint.Resource.GetComputeEnvironment() ?? endpoint.Resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment;
        if (owner is null ? Site?.Source.Name != endpoint.Resource.Name : !ReferenceEquals(owner, this))
        {
            throw new InvalidOperationException($"Resource '{endpoint.Resource.Name}' is not hosted by GitHub Pages environment '{Name}'.");
        }
        if (endpoint.EndpointAnnotation.UriScheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("GitHub Pages only supports HTTP website endpoints.");
        }
    }
}
