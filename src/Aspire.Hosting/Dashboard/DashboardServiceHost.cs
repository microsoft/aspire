// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
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

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// Hosts a gRPC service via <see cref="DashboardService"/> (aka the "Resource Service") that a dashboard can connect to.
/// Configures DI and networking options for the service.
/// </summary>
internal sealed class DashboardServiceHost : IHostedService
{
    /// <summary>
    /// Name of the environment variable that optionally specifies the resource service URL,
    /// which the dashboard will connect to over gRPC.
    /// </summary>
    /// <remarks>
    /// This is primarily intended for cases outside of the local developer environment.
    /// If no value exists for this variable, a port is assigned dynamically.
    /// </remarks>
    private const string ResourceServiceUrlVariableName = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";

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
            var uri = configuration.GetUri(ResourceServiceUrlVariableName);
            var allowUnsecuredTransport = configuration.GetBool(KnownConfigNames.AllowUnsecuredTransport) ?? false;

            string? scheme;

            if (uri is null)
            {
                // No URI available from the environment.
                scheme = allowUnsecuredTransport ? "http" : "https";

                // Listen on a random port.
                kestrelOptions.Listen(IPAddress.Loopback, port: 0, ConfigureListen);
            }
            else if (uri.IsLoopback)
            {
                scheme = uri.Scheme;

                // Listen on the requested localhost port.
                kestrelOptions.ListenLocalhost(uri.Port, ConfigureListen);
            }
            else
            {
                throw new ArgumentException($"{ResourceServiceUrlVariableName} must contain a local loopback address.");
            }

            void ConfigureListen(ListenOptions options)
            {
                // Force HTTP/2 for gRPC, so that it works over non-TLS connections
                // which cannot negotiate between HTTP/1.1 and HTTP/2.
                options.Protocols = HttpProtocols.Http2;

                if (string.Equals(scheme, "https", StringComparison.Ordinal))
                {
                    options.UseHttps();
                }
            }
        }
    }

    /// <summary>
    /// Gets the URI upon which the resource service is listening.
    /// </summary>
    /// <remarks>
    /// Intended to be used by the app model when launching the dashboard process, populating its
    /// <c>DOTNET_RESOURCE_SERVICE_ENDPOINT_URL</c> environment variable with a single URI.
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

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
