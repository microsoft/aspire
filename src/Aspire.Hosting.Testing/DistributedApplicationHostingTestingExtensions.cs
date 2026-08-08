// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Extensions for working with <see cref="DistributedApplication"/> in test code.
/// </summary>
public static class DistributedApplicationHostingTestingExtensions
{
    /// <summary>
    /// Gets the authenticated login URL for the running Aspire dashboard.
    /// </summary>
    /// <param name="app">The distributed application.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// An absolute <see cref="Uri"/> that authenticates the browser with the running dashboard.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Enable dashboard support with <see cref="DistributedApplicationTestingBuilderOptions.EnableDashboard"/> when creating
    /// the testing builder.
    /// </para>
    /// <para>
    /// This method does not start the distributed application. Call <see cref="DistributedApplication.StartAsync(CancellationToken)"/>
    /// before requesting the dashboard login URL.
    /// </para>
    /// <para>
    /// This method waits for the dashboard resource to become healthy. Pass a cancellation token when the wait must
    /// be bounded. The returned URI contains an authentication credential and should be treated as sensitive.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="app"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the application is in publish mode, the dashboard is disabled, the application has not started,
    /// anonymous dashboard access is enabled, or a dashboard login URL is unavailable.
    /// </exception>
    /// <exception cref="DistributedApplicationException">Thrown when the dashboard reaches a terminal failure state.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the distributed application has been disposed.</exception>
    /// <example>
    /// <code lang="csharp">
    /// var options = new DistributedApplicationTestingBuilderOptions
    /// {
    ///     EnableDashboard = true
    /// };
    ///
    /// var builder = await DistributedApplicationTestingBuilder.CreateAsync&lt;Projects.MyAppHost_AppHost&gt;(options, []);
    /// await using var app = await builder.BuildAsync();
    /// await app.StartAsync();
    ///
    /// var dashboardLoginUrl = await app.GetDashboardLoginUrlAsync();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Dashboard URLs are only available to .NET test code and may contain authentication credentials.")]
    public static async Task<Uri> GetDashboardLoginUrlAsync(
        this DistributedApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        if (executionContext.IsPublishMode)
        {
            throw new InvalidOperationException(Properties.Resources.DashboardLoginUrlPublishModeExceptionMessage);
        }

        var applicationOptions = app.Services.GetRequiredService<DistributedApplicationOptions>();
        if (applicationOptions.DisableDashboard)
        {
            throw new InvalidOperationException(Properties.Resources.DashboardDisabledExceptionMessage);
        }

        ThrowIfNotStarted(app, Properties.Resources.DashboardLoginUrlApplicationNotStartedExceptionMessage);
        cancellationToken.ThrowIfCancellationRequested();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(
            "Aspire.Hosting.Testing.DistributedApplicationDashboard");
        var dashboardInfo = await DashboardUrlsHelper.GetDashboardConnectionInfoOrThrowAsync(
            app.Services,
            logger,
            cancellationToken).ConfigureAwait(false);

        var dashboardUrl = dashboardInfo.CodespacesUrlWithLoginToken ?? dashboardInfo.BaseUrlWithLoginToken;
        if (!dashboardInfo.IsHealthy || !Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var dashboardUri))
        {
            throw new InvalidOperationException(Properties.Resources.DashboardLoginUrlUnavailableExceptionMessage);
        }

        if (!dashboardInfo.HasBrowserToken)
        {
            throw new InvalidOperationException(Properties.Resources.DashboardLoginUrlAnonymousExceptionMessage);
        }

        return dashboardUri;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> configured to communicate with the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resourceName of the resource.</param>
    /// <param name="endpointName">The optional endpoint name. If none is specified, the "https" endpoint is preferred when available, falling back to "http".</param>
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
    [AspireExport("getConnectionString")]
    internal static Task<string?> GetConnectionStringAsyncExport(this DistributedApplication app, string resourceName)
    {
        return app.GetConnectionStringAsync(resourceName, default).AsTask();
    }

    /// <summary>
    /// Gets the endpoint for the specified resource.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="endpointName">The optional endpoint name. If none is specified, the "https" endpoint is preferred when available, falling back to "http".</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    [AspireExport]
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
    /// <param name="endpointName">The optional endpoint name. If none is specified, the "https" endpoint is preferred when available, falling back to "http".</param>
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
    /// <param name="endpointName">The optional endpoint name. If none is specified, the "https" endpoint is preferred when available, falling back to "http".</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    [AspireExport]
    internal static Uri GetEndpointForNetworkExport(this DistributedApplication app, string resourceName, string? networkIdentifier = default, string? endpointName = default)
    {
        return app.GetEndpointForNetwork(resourceName, networkIdentifier is null ? null : new NetworkIdentifier(networkIdentifier), endpointName);
    }

    static IResource GetResource(DistributedApplication app, string resourceName)
    {
        ThrowIfNotStarted(app, Properties.Resources.ApplicationNotStartedExceptionMessage);
        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        if (!applicationModel.Resources.TryGetByName(resourceName, out var resource))
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
            // Prefer https over http to match the default service discovery behavior (https+http://),
            // where https is tried first.
            endpoint = GetEndpointOrDefault(resourceWithEndpoints, "https", networkIdentifier) ?? GetEndpointOrDefault(resourceWithEndpoints, "http", networkIdentifier);
        }

        if (endpoint is null)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.EndpointForResourceNotFoundExceptionMessage, endpointName, resourceName), nameof(endpointName));
        }

        return endpoint.Url;
    }

    static void ThrowIfNotStarted(DistributedApplication app, string exceptionMessage)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        if (!lifetime.ApplicationStarted.IsCancellationRequested)
        {
            throw new InvalidOperationException(exceptionMessage);
        }
    }

    static EndpointReference? GetEndpointOrDefault(IResourceWithEndpoints resourceWithEndpoints, string endpointName, NetworkIdentifier? networkIdentifier = default)
    {
        var reference = resourceWithEndpoints.GetEndpoint(endpointName, networkIdentifier ?? KnownNetworkIdentifiers.LocalhostNetwork);

        return reference.IsAllocated ? reference : null;
    }
}
