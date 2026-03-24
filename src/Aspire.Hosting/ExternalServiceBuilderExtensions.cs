// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using HealthChecks.Uris;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding external services to an application.
/// </summary>
public static class ExternalServiceBuilderExtensions
{
    /// <summary>
    /// Adds an external service resource to the distributed application with the specified URL.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="url">The URL of the external service.</param>
    /// <returns>An <see cref="IResourceBuilder{ExternalServiceResource}"/> instance.</returns>
    [AspireExport("addExternalService", Description = "Adds an external service resource")]
    public static IResourceBuilder<ExternalServiceResource> AddExternalService(this IDistributedApplicationBuilder builder, [ResourceName] string name, string url)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);

        if (!ExternalServiceResource.UrlIsValidForExternalService(url, out var uri, out var message))
        {
            throw new ArgumentException($"The external service URL '{url}' is invalid: {message}", nameof(url));
        }

        return AddExternalServiceImpl(builder, name, uri);
    }

    /// <summary>
    /// Adds an external service resource to the distributed application with the specified URI.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="uri">The URI of the external service.</param>
    /// <returns>An <see cref="IResourceBuilder{ExternalServiceResource}"/> instance.</returns>
    [AspireExport("addExternalServiceUri", Description = "Adds an external service with a URI")]
    public static IResourceBuilder<ExternalServiceResource> AddExternalService(this IDistributedApplicationBuilder builder, [ResourceName] string name, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(uri);

        return AddExternalServiceImpl(builder, name, uri);
    }

    /// <summary>
    /// Adds an external service resource to the distributed application with the URL coming from the specified parameter.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="urlParameter">The parameter containing the URL of the external service.</param>
    /// <returns>An <see cref="IResourceBuilder{ExternalServiceResource}"/> instance.</returns>
    [AspireExport("addExternalServiceParameter", Description = "Adds an external service with a parameter URL")]
    public static IResourceBuilder<ExternalServiceResource> AddExternalService(this IDistributedApplicationBuilder builder, [ResourceName] string name, IResourceBuilder<ParameterResource> urlParameter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(urlParameter);

        return AddExternalServiceImpl(builder, name, urlParameter: urlParameter.Resource);
    }

    private static IResourceBuilder<ExternalServiceResource> AddExternalServiceImpl(IDistributedApplicationBuilder builder, string name, Uri? uri = null, ParameterResource? urlParameter = null)
    {
        Debug.Assert(uri is not null || urlParameter is not null, "Either uri or urlParameter must be provided.");

        var resource = uri is not null
            ? new ExternalServiceResource(name, uri)
            : new ExternalServiceResource(name, urlParameter!);

        var resourceBuilder = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "ExternalService",
                State = KnownResourceStates.Waiting,
                Properties = []
            })
            .ExcludeFromManifest();

        if (resource.Uri is not null)
        {
            resourceBuilder.WithUrl(resource.Uri.ToString());
        }
        else if (resource.UrlParameter is not null)
        {
            resourceBuilder.WithUrl(ReferenceExpression.Create($"{resource.UrlParameter}"));
        }

        // Subscribe to the InitializeResourceEvent to finish setting up the resource
        builder.Eventing.Subscribe<InitializeResourceEvent>(resource, static async (e, ct) =>
        {
            var resource = e.Resource as ExternalServiceResource;
            if (resource is not null)
            {
                var uri = resource.Uri;

                if (uri is null)
                {
                    // If the URI is not set, it means we are using a parameterized URL
                    string? url;
                    try
                    {
                        url = resource.UrlParameter is null
                                ? null
                                : await resource.UrlParameter.GetValueAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        e.Logger.LogError(ex, "Failed to get value for URL parameter '{ParameterName}'", resource.UrlParameter?.Name);

                        await e.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
                        {
                            State = KnownResourceStates.FailedToStart
                        }).ConfigureAwait(false);

                        return;
                    }

                    if (!ExternalServiceResource.UrlIsValidForExternalService(url, out uri, out var message))
                    {
                        e.Logger.LogError("The value for URL parameter '{ParameterName}' is invalid: {Error}", resource.UrlParameter?.Name, message);

                        await e.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
                        {
                            State = KnownResourceStates.FailedToStart
                        }).ConfigureAwait(false);

                        return;
                    }
                }

                Debug.Assert(uri is not null, "URI must be set at this point.");

                await e.Eventing.PublishAsync(new BeforeResourceStartedEvent(e.Resource, e.Services), ct).ConfigureAwait(false);

                await e.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
                {
                    Properties = snapshot.Properties.SetResourceProperty(CustomResourceKnownProperties.Source, uri.Host),
                    // Add the URL if it came from a parameter as non-static URLs must be published by the owning custom resource
                    Urls = AddUrlIfNotPresent(snapshot.Urls, uri),
                    // Required in order for health checks to work
                    State = KnownResourceStates.Running
                }).ConfigureAwait(false);

                static ImmutableArray<UrlSnapshot> AddUrlIfNotPresent(ImmutableArray<UrlSnapshot> urlSnapshots, Uri uri)
                {
                    if (urlSnapshots.Any(u => string.Equals(u.Url, uri.ToString(), StringComparisons.Url)))
                    {
                        return urlSnapshots; // URL already exists, no need to add it again
                    }

                    return urlSnapshots.Add(new(Name: null, uri.ToString(), IsInternal: false));
                }
            }
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a health check to the external service resource.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="path">The relative path to use for the HTTP health check.</param>
    /// <param name="statusCode"></param>
    /// <returns></returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check to the health check service which polls the specified external service
    /// on a periodic basis. The address is based on the URL of the external service.
    /// A path for the health check request can be specified. The expected status code is set to <c>200</c> by default but a
    /// different one can be specified.
    /// </para>
    /// </remarks>
    [AspireExport("withExternalServiceHttpHealthCheck", Description = "Adds an HTTP health check to an external service")]
    public static IResourceBuilder<ExternalServiceResource> WithHttpHealthCheck(this IResourceBuilder<ExternalServiceResource> builder, string? path = null, int? statusCode = null)
    {
        if (path is not null && !Uri.IsWellFormedUriString(path, UriKind.Relative))
        {
            throw new ArgumentException($"The path '{path}' is not a valid relative URL.", nameof(path));
        }

        if (builder.Resource.UrlParameter is null)
        {
            if (builder.Resource.Uri is null)
            {
                throw new ArgumentException($"The URL for external service '{builder.Resource.Name}' is null.", nameof(builder));
            }
            else if (builder.Resource.Uri.Scheme != "http" && builder.Resource.Uri.Scheme != "https")
            {
                throw new ArgumentException($"The URL '{builder.Resource.Uri}' for external service '{builder.Resource.Name}' cannot be used for HTTP health checks because it has a non-HTTP scheme.", nameof(builder));
            }
        }

        statusCode ??= 200;

        var pathKey = path is not null ? $"_{path}" : string.Empty;
        var healthCheckKey = $"{builder.Resource.Name}_external{pathKey}_{statusCode}_check";

        builder.ApplicationBuilder.Services.AddHttpClient();
        builder.ApplicationBuilder.Services.SuppressHealthCheckHttpClientLogging(healthCheckKey);

        // Check if the external service uses a parameter for its URL
        if (builder.Resource.UrlParameter is not null)
        {
            // For parameter-based URLs, use the custom health check that resolves the URL asynchronously
            builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                healthCheckKey,
                serviceProvider => new ParameterUriHealthCheck(
                    builder.Resource.UrlParameter,
                    path,
                    statusCode.Value,
                    () => serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(healthCheckKey)),
                failureStatus: default,
                tags: default,
                timeout: default));
        }
        else
        {
            var uri = builder.Resource.Uri!;

            // Use the existing AddUrlGroup approach for static URLs
            builder.ApplicationBuilder.Services.AddHealthChecks().AddUrlGroup(options =>
            {
                var targetUri = uri;
                if (path is not null)
                {
                    targetUri = new Uri(uri, path);
                }

                options.AddUri(targetUri, setup => setup.ExpectHttpCode(statusCode.Value));
            }, healthCheckKey);
        }

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }
}

/// <summary>
/// A health check that resolves URL from a parameter asynchronously and delegates to UriHealthCheck.
/// </summary>
internal sealed class ParameterUriHealthCheck : IHealthCheck
{
    private readonly ParameterResource _urlParameter;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly string? _path;
    private readonly int _expectedStatusCode;
    private readonly UriHealthCheckOptions _options;
    private readonly UriHealthCheck _uriHealthCheck;

    public ParameterUriHealthCheck(ParameterResource urlParameter, string? path, int expectedStatusCode, Func<HttpClient> httpClientFactory)
    {
        _urlParameter = urlParameter ?? throw new ArgumentNullException(nameof(urlParameter));
        _path = path;
        _expectedStatusCode = expectedStatusCode;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = new UriHealthCheckOptions();
        _uriHealthCheck = new UriHealthCheck(_options, _httpClientFactory);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve the URL from the parameter asynchronously
            var urlValue = await _urlParameter.GetValueAsync(cancellationToken).ConfigureAwait(false);

            // Use ExternalServiceResource validation for the base URL
            if (!ExternalServiceResource.UrlIsValidForExternalService(urlValue, out var uri, out var message))
            {
                return HealthCheckResult.Unhealthy($"The URL '{urlValue}' from parameter '{_urlParameter.Name}' is invalid: {message}");
            }

            // Additional validation for health check: ensure HTTP/HTTPS scheme
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                return HealthCheckResult.Unhealthy($"The URL '{uri}' from parameter '{_urlParameter.Name}' cannot be used for HTTP health checks because it has a non-HTTP scheme.");
            }

            // Apply path if specified
            if (_path is not null)
            {
                uri = new Uri(uri, _path);
            }

            _options.AddUri(uri, setup => setup.ExpectHttpCode(_expectedStatusCode));

            return await _uriHealthCheck.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
