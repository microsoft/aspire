// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Extensions for working with <see cref="DistributedApplication"/> in test code.
/// </summary>
public static class DistributedApplicationHostingTestingExtensions
{
    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured to communicate with the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resourceName of the resource.</param>
    /// <param name="endpointName">The resourceName of the endpoint on the resource to communicate with.</param>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    /// <returns>The <see cref="HttpClient"/>.</returns>
    [AspireExportIgnore(Reason = "HttpClient is not ATS-compatible.")]
    public static HttpClient CreateHttpClient(this DistributedApplication app, string resourceName, string? endpointName = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var baseUri = GetEndpointUriStringCore(app, resourceName, endpointName);
        var clientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
        var client = clientFactory.CreateClient();
        client.BaseAddress = new(baseUri);

        return client;
    }

    /// <summary>
    /// Gets the connection string for the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    /// <remarks>This overload is not available in polyglot app hosts. Use the exported overload without a cancellation token instead.</remarks>
    /// <returns>The connection string for the specified resource.</returns>
    /// <exception cref="ArgumentException">The resource was not found or does not expose a connection string.</exception>
    [AspireExportIgnore(Reason = "Use the exported getConnectionString overload without a cancellation token.")]
    public static ValueTask<string?> GetConnectionStringAsync(this DistributedApplication app, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var resource = GetResource(app, resourceName);
        if (resource is not IResourceWithConnectionString resourceWithConnectionString)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.ResourceDoesNotExposeConnectionStringExceptionMessage, resourceName), nameof(resourceName));
        }

        return resourceWithConnectionString.GetConnectionStringAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the connection string for the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The connection string for the specified resource.</returns>
    /// <exception cref="ArgumentException">The resource was not found or does not expose a connection string.</exception>
    [AspireExport("getConnectionString", Description = "Gets the connection string for the specified resource.")]
    internal static Task<string?> GetConnectionStringAsyncExport(this DistributedApplication app, string resourceName)
    {
        return app.GetConnectionStringAsync(resourceName, default).AsTask();
    }

    /// <summary>
    /// Gets the endpoint for the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="endpointName">The optional endpoint name. If none are specified, the single defined endpoint is returned.</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    [AspireExport("getEndpoint", Description = "Gets the endpoint for the specified resource.")]
    public static Uri GetEndpoint(this DistributedApplication app, string resourceName, string? endpointName = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        return GetEndpointForNetwork(app, resourceName, null, endpointName);
    }

    /// <summary>
    /// Gets the endpoint for the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="networkIdentifier">The optional network identifier. If none is specified, the default network is used.</param>
    /// <param name="endpointName">The optional endpoint name. If none are specified, the single defined endpoint is returned.</param>
    /// <remarks>This overload is not available in polyglot app hosts. Use the exported overload that accepts a network identifier string instead.</remarks>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    [AspireExportIgnore(Reason = "Use the ATS-friendly overload that accepts a network identifier string.")]
    public static Uri GetEndpointForNetwork(this DistributedApplication app, string resourceName, NetworkIdentifier? networkIdentifier, string? endpointName = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        return new(GetEndpointUriStringCore(app, resourceName, endpointName, networkIdentifier));
    }

    /// <summary>
    /// Gets the endpoint for the specified resource in the specified network context.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="networkIdentifier">The optional network identifier string. If none is specified, the default network is used.</param>
    /// <param name="endpointName">The optional endpoint name. If none are specified, the single defined endpoint is returned.</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    [AspireExport("getEndpointForNetwork", Description = "Gets the endpoint for the specified resource in the specified network context.")]
    internal static Uri GetEndpointForNetworkExport(this DistributedApplication app, string resourceName, string? networkIdentifier = default, string? endpointName = default)
    {
        return app.GetEndpointForNetwork(resourceName, networkIdentifier is null ? null : new NetworkIdentifier(networkIdentifier), endpointName);
    }

    static IResource GetResource(DistributedApplication app, string resourceName)
    {
        ThrowIfNotStarted(app);
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resources = applicationModel.Resources;
        var resource = resources.SingleOrDefault(r => string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.ResourceNotFoundExceptionMessage, resourceName), nameof(resourceName));
        }

        return resource;
    }

    static string GetEndpointUriStringCore(DistributedApplication app, string resourceName, string? endpointName = default, NetworkIdentifier? networkIdentifier = default)
    {
        var resource = GetResource(app, resourceName);
        if (resource is not IResourceWithEndpoints resourceWithEndpoints)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.ResourceHasNoAllocatedEndpointsExceptionMessage, resourceName), nameof(resourceName));
        }

        EndpointReference? endpoint;
        if (!string.IsNullOrEmpty(endpointName))
        {
            endpoint = GetEndpointOrDefault(resourceWithEndpoints, endpointName, networkIdentifier);
        }
        else
        {
            endpoint = GetEndpointOrDefault(resourceWithEndpoints, "http", networkIdentifier) ?? GetEndpointOrDefault(resourceWithEndpoints, "https", networkIdentifier);
        }

        if (endpoint is null)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.EndpointForResourceNotFoundExceptionMessage, endpointName, resourceName), nameof(endpointName));
        }

        return endpoint.Url;
    }

    static void ThrowIfNotStarted(DistributedApplication app)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        if (!lifetime.ApplicationStarted.IsCancellationRequested)
        {
            throw new InvalidOperationException(Properties.Resources.ApplicationNotStartedExceptionMessage);
        }
    }

    static EndpointReference? GetEndpointOrDefault(IResourceWithEndpoints resourceWithEndpoints, string endpointName, NetworkIdentifier? networkIdentifier = default)
    {
        var reference = resourceWithEndpoints.GetEndpoint(endpointName, networkIdentifier ?? KnownNetworkIdentifiers.LocalhostNetwork);

        return reference.IsAllocated ? reference : null;
    }
}
