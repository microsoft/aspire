// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Hosts a gRPC service via <see cref="DashboardService"/> (aka the "Resource Service") that a dashboard can connect to.
/// Configures DI and networking options for the service.
/// </summary>
internal sealed class DashboardServiceHost : IHostedService
{
    /// <summary>
    /// Provides access to the URI at which the resource service endpoint is hosted.
    /// </summary>
    private readonly TaskCompletionSource<string> _resourceServiceUri = new();

    /// <summary>
    /// <see langword="null"/> if <see cref="DistributedApplicationOptions.DashboardEnabled"/> is <see langword="false"/>.
    /// </summary>
    private readonly WebApplication? _app;
    private readonly ILogger<DashboardServiceHost> _logger;

    public DashboardServiceHost(
        DistributedApplicationOptions options,
        DistributedApplicationModel applicationModel,
        IConfiguration configuration,
        DistributedApplicationExecutionContext executionContext,
        IOptions<DcpOptions> dcpOptions,
        ILoggerFactory loggerFactory,
        IConfigureOptions<LoggerFilterOptions> loggerOptions,
        ResourceNotificationService resourceNotificationService,
        ResourceLoggerService resourceLoggerService,
        ResourceCommandService resourceCommandService,
        InteractionService interactionService)
    {
        _logger = loggerFactory.CreateLogger<DashboardServiceHost>();

        if (!options.DashboardEnabled || executionContext.IsPublishMode)
        {
            _logger.LogDebug("Dashboard is not enabled so skipping hosting the resource service.");
            _resourceServiceUri.SetCanceled();
            return;
        }

        try
        {
            var builder = WebApplication.CreateSlimBuilder();

            // Turn on HTTPS
            builder.WebHost.UseKestrelHttpsConfiguration();

            // Configuration
            builder.Services.AddSingleton(configuration);

            var resourceServiceConfigSection = configuration.GetSection("AppHost:ResourceService");
            builder.Services.AddOptions<ResourceServiceOptions>()
                .Bind(resourceServiceConfigSection)
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<ResourceServiceOptions>, ValidateResourceServiceOptions>();

            // Configure authentication scheme for the dashboard service
            builder.Services
                .AddAuthentication()
                .AddScheme<ResourceServiceApiKeyAuthenticationOptions, ResourceServiceApiKeyAuthenticationHandler>(
                    ResourceServiceApiKeyAuthenticationDefaults.AuthenticationScheme,
                    options => { });

            // Configure authorization policy for the dashboard service.
            // The authorization policy accepts anyone who successfully authenticates via the
            // specified scheme, and that scheme enforces a valid API key (when configured to
            // use API keys for calls.)
            builder.Services
                .AddAuthorizationBuilder()
                .AddPolicy(
                    name: ResourceServiceApiKeyAuthorization.PolicyName,
                    policy: new AuthorizationPolicyBuilder(
                        ResourceServiceApiKeyAuthenticationDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .Build());

            // Logging
            builder.Services.AddSingleton(loggerFactory);
            builder.Services.AddSingleton(loggerOptions);
            builder.Services.Add(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));

            builder.Services.AddGrpc();
            builder.Services.AddSingleton(applicationModel);
            builder.Services.AddSingleton(resourceCommandService);
            builder.Services.AddSingleton<DashboardServiceData>();
            builder.Services.AddSingleton(resourceNotificationService);
            builder.Services.AddSingleton(resourceLoggerService);
            builder.Services.AddSingleton(interactionService);

            builder.WebHost.ConfigureKestrel(ConfigureKestrel);

            _app = builder.Build();

            _app.UseAuthentication();
            _app.UseAuthorization();

            _app.MapGrpcService<DashboardService>();
        }
        catch (Exception ex)
        {
            _resourceServiceUri.TrySetException(ex);
            throw;
        }

        return;

        void ConfigureKestrel(KestrelServerOptions kestrelOptions)
        {
            // Inspect environment for the address to listen on.
            // Prefer the new config name, falling back to the legacy name.
            var uri = configuration.GetUri(KnownConfigNames.ResourceServiceEndpointUrl)
                ?? configuration.GetUri(KnownConfigNames.Legacy.ResourceServiceEndpointUrl);
            var allowUnsecuredTransport = configuration.GetBool(KnownConfigNames.AllowUnsecuredTransport) ?? false;
            var randomizePorts = dcpOptions.Value.RandomizePorts;

            var endpoint = ResolveEndpoint(uri, randomizePorts, allowUnsecuredTransport);

            if (endpoint.UseListenLocalhost)
            {
                kestrelOptions.ListenLocalhost(endpoint.Port, ConfigureListen);
            }
            else
            {
                kestrelOptions.Listen(endpoint.BindAddress, endpoint.Port, ConfigureListen);
            }

            _logger.LogDebug("Resource service endpoint: configured={Uri}, binding={BindAddress}:{Port}, scheme={Scheme}",
                uri?.ToString() ?? "(none)", endpoint.BindAddress, endpoint.Port, endpoint.Scheme);

            void ConfigureListen(ListenOptions options)
            {
                // Force HTTP/2 for gRPC, so that it works over non-TLS connections
                // which cannot negotiate between HTTP/1.1 and HTTP/2.
                options.Protocols = HttpProtocols.Http2;

                if (string.Equals(endpoint.Scheme, "https", StringComparison.Ordinal))
                {
                    options.UseHttps();
                }
            }
        }
    }

    /// <summary>
    /// Resolves the endpoint that the resource service should bind to based on the configured URI,
    /// port randomization settings, and transport security preferences.
    /// </summary>
    internal static ResourceServiceEndpointInfo ResolveEndpoint(Uri? configuredUri, bool randomizePorts, bool allowUnsecuredTransport)
    {
        var scheme = ResolveScheme(configuredUri, allowUnsecuredTransport);

        // When randomizePorts is true (e.g. --isolated mode), override a fixed configured
        // port to 0 so multiple instances don't collide. A configured port of 0 is already
        // dynamic, so no override is needed.
        var effectivePort = (randomizePorts && configuredUri is not null && configuredUri.Port != 0) ? 0 : configuredUri?.Port ?? 0;

        if (configuredUri is null)
        {
            // No configured endpoint — bind to IPv4 loopback on a random port.
            return new ResourceServiceEndpointInfo(IPAddress.Loopback, Port: 0, UseListenLocalhost: false, scheme);
        }
        else if (IPAddress.TryParse(configuredUri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip))
        {
            // Bind to the exact loopback address specified (e.g. 127.0.0.1 or [::1]).
            // Use DnsSafeHost to strip brackets from IPv6 literals so TryParse succeeds.
            return new ResourceServiceEndpointInfo(ip, effectivePort, UseListenLocalhost: false, scheme);
        }
        else if (configuredUri.IsLoopback || IsLocalhostOrLocalhostTld(configuredUri))
        {
            // For "localhost" or *.localhost hosts, bind to both IPv4 and IPv6 loopback.
            // Kestrel does not support ListenLocalhost with port 0, so fall back to
            // binding on IPv4 loopback when a dynamic port is needed.
            if (effectivePort == 0)
            {
                return new ResourceServiceEndpointInfo(IPAddress.Loopback, Port: 0, UseListenLocalhost: false, scheme);
            }
            else
            {
                return new ResourceServiceEndpointInfo(IPAddress.Loopback, effectivePort, UseListenLocalhost: true, scheme);
            }
        }
        else
        {
            throw new ArgumentException($"{KnownConfigNames.ResourceServiceEndpointUrl} must contain a local loopback address.");
        }
    }

    /// <summary>
    /// Determines the scheme for the resource service endpoint. When a URI is explicitly
    /// configured, its scheme is used. When no URI is provided, defaults to HTTPS unless
    /// unsecured transport is explicitly allowed.
    /// </summary>
    internal static string ResolveScheme(Uri? configuredUri, bool allowUnsecuredTransport)
    {
        if (configuredUri is not null)
        {
            return configuredUri.Scheme;
        }

        return allowUnsecuredTransport ? "http" : "https";
    }

    private static bool IsLocalhostOrLocalhostTld(Uri uri)
    {
        var host = uri.Host.EndsWith(".", StringComparison.Ordinal)
            ? uri.Host[..^1]
            : uri.Host;

        return EndpointHostHelpers.IsLocalhostOrLocalhostTld(host);
    }

    /// <summary>
    /// Gets the URI upon which the resource service is listening.
    /// </summary>
    /// <remarks>
    /// Intended to be used by the app model when launching the dashboard process, populating its
    /// <c>ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL</c> environment variable with a single URI.
    /// </remarks>
    public async Task<string> GetResourceServiceUriAsync(CancellationToken cancellationToken = default)
    {
        var startTime = Stopwatch.GetTimestamp();

        var uri = await _resourceServiceUri.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var elapsed = Stopwatch.GetElapsedTime(startTime);

        if (elapsed > TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning("Unexpectedly long wait for resource service URI ({Elapsed}).", elapsed);
        }

        return uri;
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addressFeature = _app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();

            if (addressFeature is null)
            {
                _resourceServiceUri.SetException(new InvalidOperationException("Could not obtain IServerAddressesFeature. Resource service URI is not available."));
                return;
            }

            _resourceServiceUri.SetResult(addressFeature.Addresses.Single());
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _resourceServiceUri.TrySetCanceled(cancellationToken);

        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Describes the resolved endpoint binding for the resource service.
/// </summary>
internal readonly record struct ResourceServiceEndpointInfo(
    IPAddress BindAddress,
    int Port,
    bool UseListenLocalhost,
    string Scheme);
