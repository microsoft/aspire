// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding dev tunnels resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static partial class DevTunnelsResourceBuilderExtensions
{
    private static readonly string s_aspireUserAgent = GetUserAgent();

    /// <summary>
    /// Adds a dev tunnel resource to the application model.
    /// </summary>
    /// <remarks>
    /// Dev tunnels can be used to expose local endpoints to the public internet via a secure tunnel. By default,
    /// the tunnel requires authentication, but anonymous access can be enabled via <see cref="WithAnonymousAccess(IResourceBuilder{DevTunnelResource})"/>.
    /// </remarks>
    /// <example>
    /// The following example shows how to create a dev tunnel resource that exposes all endpoints on a web application project and enable anonymous access:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var web = builder.AddProject&lt;Projects.WebApp&gt;("web");
    /// var tunnel = builder.AddDevTunnel("mytunnel")
    ///     .WithReference(web)
    ///     .WithAnonymousAccess();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IResourceBuilder<DevTunnelResource> AddDevTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? tunnelId = null,
        DevTunnelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var appHostId = builder.Configuration["AppHost:Sha256"]?[..8];
        tunnelId ??= $"{name}-{appHostId}".ToLowerInvariant();

        // Validate the TunnelId format [a-z0-9][a-z0-9-]{1,58}[a-z0-9]
        if (!TunnelIdRegex().IsMatch(tunnelId))
        {
            throw new ArgumentException($"""
                The tunnel ID '{tunnelId}' is invalid. A valid tunnel ID must:
                - start and end with a letter or number
                - consist of lowercase letters, numbers, and hyphens
                - be 1-58 characters long
                """, nameof(tunnelId));
        }

        options ??= new DevTunnelOptions();
        options.Labels ??= [];
        options.Labels.Add($"aspire_{name}-{appHostId}");
        options.Description ??= $"Dev tunnel for '{name}' in Aspire AppHost '{builder.Environment.ApplicationName}'";

        if (!TryValidateLabels(options.Labels, out var errorMessage))
        {
            throw new ArgumentException(errorMessage, nameof(options));
        }

        // Add services
        builder.Services.TryAddSingleton<DevTunnelCliInstallationManager>();
        builder.Services.TryAddSingleton<DevTunnelLoginManager>();
        builder.Services.TryAddSingleton<LoggedOutNotificationManager>();
        builder.Services.TryAddSingleton<IDevTunnelClient, DevTunnelCliClient>();

        var workingDirectory = builder.AppHostDirectory;
        var tunnelResource = new DevTunnelResource(name, tunnelId, DevTunnelCli.GetCliPath(builder.Configuration), workingDirectory, options);

        // Health check
        var healtCheckKey = $"{name}-check";
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healtCheckKey,
            services => new DevTunnelHealthCheck(
                services.GetRequiredService<IDevTunnelClient>(),
                services.GetRequiredService<LoggedOutNotificationManager>(),
                tunnelResource,
                services.GetRequiredService<ILogger<DevTunnelHealthCheck>>()),
            failureStatus: default,
            tags: default,
            timeout: default));
#pragma warning restore ASPIREINTERACTION001

        var rb = builder.AddResource(tunnelResource)
            .WithArgs("host", tunnelResource.ResolvedTunnelId, "--nologo")
            .WithIconName("CloudBidirectional")
            .WithEnvironment("TUNNEL_SERVICE_USER_AGENT", s_aspireUserAgent)
            .WithInitialState(new()
            {
                ResourceType = "DevTunnel",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties = [
                    new("TunnelId", tunnelId)
                ]
            })
            .ExcludeFromManifest() // Dev tunnels do not get deployed
            .WithHealthCheck(healtCheckKey)
            // Lifecycle
            .OnBeforeResourceStarted(static async (tunnelResource, e, ct) =>
            {
                var logger = e.Services.GetRequiredService<ResourceLoggerService>().GetLogger(tunnelResource);
                var eventing = e.Services.GetRequiredService<IDistributedApplicationEventing>();
                var devTunnelCliInstallationManager = e.Services.GetRequiredService<DevTunnelCliInstallationManager>();
                var devTunnelEnvironmentManager = e.Services.GetRequiredService<DevTunnelLoginManager>();
                var devTunnelClient = e.Services.GetRequiredService<IDevTunnelClient>();

                // Ensure CLI is available
                await devTunnelCliInstallationManager.EnsureInstalledAsync(ct).ConfigureAwait(false);

                // Login to the dev tunnels service if needed
                logger.LogInformation("Ensuring user is logged in to dev tunnel service");
                await devTunnelEnvironmentManager.EnsureUserLoggedInAsync(ct).ConfigureAwait(false);

                // Create the dev tunnel
                try
                {
                    logger.LogInformation("Creating dev tunnel '{TunnelId}'", tunnelResource.TunnelId);
                    var tunnelStatus = await devTunnelClient.CreateTunnelAsync(tunnelResource.TunnelId, tunnelResource.Options, logger, ct).ConfigureAwait(false);
                    logger.LogDebug("Dev tunnel '{TunnelId}' created", tunnelResource.TunnelId);
                }
                catch (Exception ex)
                {
                    var exception = new DistributedApplicationException($"Error trying to create the dev tunnel resource '{tunnelResource.TunnelId}' this port belongs to: {ex.Message}", ex);
                    foreach (var portResource in tunnelResource.Ports)
                    {
                        portResource.TunnelEndpointAnnotation.AllocatedEndpointSnapshot.SetException(exception);
                    }
                    throw;
                }

                // Wait for target resource endpoints to be allocated
                await Task.WhenAll(tunnelResource.Ports.Select(p => p.TargetEndpoint.GetValueAsync(ct).AsTask())).ConfigureAwait(false);

                // Start the tunnel ports
                var notifications = e.Services.GetRequiredService<ResourceNotificationService>();

                // Ensure any ports that aren't in the application model are deleted
                var portTasks = new List<Task> { DeleteUnmodeledPortsAsync() };
                portTasks.AddRange(tunnelResource.Ports.Select(StartPortAsync));
                await Task.WhenAll(portTasks).ConfigureAwait(false);

                async Task DeleteUnmodeledPortsAsync()
                {
                    var existingPorts = await devTunnelClient.GetPortListAsync(tunnelResource.ResolvedTunnelId, logger, ct).ConfigureAwait(false);
                    var modeledPortNumbers = tunnelResource.Ports.Select(p => p.TargetEndpoint.Port).ToHashSet();
                    var unmodeledPorts = existingPorts.Ports.Where(p => !modeledPortNumbers.Contains(p.PortNumber)).ToList();
                    if (unmodeledPorts.Count > 0)
                    {
                        logger.LogInformation("Deleting {Count} unmodeled ports from dev tunnel '{TunnelId}': {Ports}", unmodeledPorts.Count, tunnelResource.TunnelId, string.Join(", ", unmodeledPorts.Select(p => p.PortNumber)));
                        await Task.WhenAll(unmodeledPorts.Select(p => devTunnelClient.DeletePortAsync(tunnelResource.ResolvedTunnelId, p.PortNumber, logger, ct))).ConfigureAwait(false);
                    }
                }

                async Task StartPortAsync(DevTunnelPortResource portResource)
                {
                    var portLogger = e.Services.GetRequiredService<ResourceLoggerService>().GetLogger(portResource);

                    // Clear any prior port status
                    portLogger.LogInformation("Tunnel starting");
                    await notifications.PublishUpdateAsync(portResource, snapshot => snapshot with
                    {
                        State = KnownResourceStates.Starting
                    }).ConfigureAwait(false);

                    // Create the tunnel port
                    try
                    {
                        _ = await devTunnelClient.CreatePortAsync(
                                portResource.DevTunnel.ResolvedTunnelId,
                                portResource.TargetEndpoint.Port,
                                portResource.Options,
                                portLogger,
                                ct)
                            .ConfigureAwait(false);

                        portLogger.LogInformation("Created dev tunnel port '{Port}' on tunnel '{Tunnel}' targeting endpoint '{Endpoint}' on resource '{TargetResource}'", portResource.TargetEndpoint.Port, portResource.DevTunnel.TunnelId, portResource.TargetEndpoint.EndpointName, portResource.TargetEndpoint.Resource.Name);
                    }
                    catch (Exception ex)
                    {
                        portLogger.LogError(ex, "Error trying to create dev tunnel port '{Port}' on tunnel '{Tunnel}': {Error}", portResource.TargetEndpoint.Port, portResource.DevTunnel.TunnelId, ex.Message);
                        portResource.TunnelEndpointAnnotation.AllocatedEndpointSnapshot.SetException(ex);
                        throw;
                    }

                    await eventing.PublishAsync<BeforeResourceStartedEvent>(new(portResource, e.Services), EventDispatchBehavior.NonBlockingConcurrent, ct).ConfigureAwait(false);
                }
            })
            .OnResourceStopped(static (tunnelResource, e, ct) =>
            {
                // Tunnel stopped, mark status as null
                tunnelResource.LastKnownStatus = null;
                return Task.CompletedTask;
            });

        // Tunnels will expire after not being hosted for 30 days by default so we won't forcibly delete them when the resource or AppHost is stopped

        return rb;
    }

    /// <summary>
    /// Adds ports on the dev tunnel for all endpoints found on the referenced resource and sets whether anonymous access is allowed.
    /// </summary>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <param name="resourceBuilder">The resource builder for the referenced resource.</param>
    /// <param name="allowAnonymous">Whether anonymous access is allowed.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithReference<TResource>(
        this IResourceBuilder<DevTunnelResource> tunnelBuilder,
        IResourceBuilder<TResource> resourceBuilder,
        bool allowAnonymous)
        where TResource : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(resourceBuilder);

        return tunnelBuilder.WithReference(resourceBuilder, new DevTunnelPortOptions { AllowAnonymous = allowAnonymous });
    }

    /// <summary>
    /// Adds ports on the dev tunnel for all endpoints found on the referenced resource.
    /// </summary>
    /// <remarks>
    /// To expose only specific endpoints on the referenced resource, use <see cref="WithReference(IResourceBuilder{DevTunnelResource}, EndpointReference, DevTunnelPortOptions?)"/>.
    /// </remarks>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <param name="resourceBuilder">The resource builder for the referenced resource.</param>
    /// <param name="portOptions">Options for the dev tunnel ports.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithReference<TResource>(
        this IResourceBuilder<DevTunnelResource> tunnelBuilder,
        IResourceBuilder<TResource> resourceBuilder,
        DevTunnelPortOptions? portOptions = null)
        where TResource : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(resourceBuilder);

        foreach (var endpoint in resourceBuilder.Resource.GetEndpoints())
        {
            AddDevTunnelPort(tunnelBuilder, endpoint, portOptions);
        }

        return tunnelBuilder;
    }

    // NOTE: This is a separate overload to ensure it's bound over the generic service discovery extension method
    /// <summary>
    /// Exposes the specified endpoint via the dev tunnel.
    /// </summary>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <param name="targetEndpoint">The endpoint to expose via the dev tunnel.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithReference(
        this IResourceBuilder<DevTunnelResource> tunnelBuilder,
        EndpointReference targetEndpoint)
        => tunnelBuilder.WithReference(targetEndpoint, portOptions: null);

    /// <summary>
    /// Exposes the specified endpoint via the dev tunnel and sets whether anonymous access is allowed.
    /// </summary>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <param name="targetEndpoint">The endpoint to expose via the dev tunnel.</param>
    /// <param name="allowAnonymous">Whether anonymous access is allowed.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithReference(
        this IResourceBuilder<DevTunnelResource> tunnelBuilder,
        EndpointReference targetEndpoint,
        bool allowAnonymous)
        => tunnelBuilder.WithReference(targetEndpoint, new DevTunnelPortOptions { AllowAnonymous = allowAnonymous });

    /// <summary>
    /// Exposes the specified endpoint via the dev tunnel.
    /// </summary>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <param name="targetEndpoint">The endpoint to expose via the dev tunnel.</param>
    /// <param name="portOptions">Options for the dev tunnel port.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithReference(
        this IResourceBuilder<DevTunnelResource> tunnelBuilder,
        EndpointReference targetEndpoint,
        DevTunnelPortOptions? portOptions)
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(targetEndpoint);

        AddDevTunnelPort(tunnelBuilder, targetEndpoint, portOptions);

        return tunnelBuilder;
    }

    /// <summary>
    /// Allows the tunnel to be publicly accessed without authentication.
    /// </summary>
    /// <remarks>
    /// Sets <see cref="DevTunnelOptions.AllowAnonymous"/> to <c>true</c> on <see cref="DevTunnelResource.Options"/> .
    /// </remarks>
    /// <param name="tunnelBuilder">The resource builder.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<DevTunnelResource> WithAnonymousAccess(this IResourceBuilder<DevTunnelResource> tunnelBuilder)
    {
        tunnelBuilder.Resource.Options.AllowAnonymous = true;
        return tunnelBuilder;
    }

    /// <summary>
    /// Gets the tunnel endpoint reference for the specified target resource and endpoint.
    /// </summary>
    /// <typeparam name="TResource">The type of the target resource.</typeparam>
    /// <param name="tunnelBuilder">The dev tunnel resource builder.</param>
    /// <param name="resourceBuilder">The target resource builder.</param>
    /// <param name="endpointName">The name of the endpoint on the target resource.</param>
    /// <returns>An <see cref="EndpointReference"/> representing the public tunnel endpoint.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the specified endpoint is not found in the tunnel.</exception>
    public static EndpointReference GetEndpoint<TResource>(this IResourceBuilder<DevTunnelResource> tunnelBuilder, IResourceBuilder<TResource> resourceBuilder, string endpointName)
        where TResource : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(resourceBuilder);
        ArgumentNullException.ThrowIfNull(endpointName);

        return tunnelBuilder.GetEndpoint(resourceBuilder.Resource, endpointName);
    }

    /// <summary>
    /// Gets the tunnel endpoint reference for the specified target resource and endpoint.
    /// </summary>
    /// <param name="tunnelBuilder">The dev tunnel resource builder.</param>
    /// <param name="resource">The target resource.</param>
    /// <param name="endpointName">The name of the endpoint on the target resource.</param>
    /// <returns>An <see cref="EndpointReference"/> representing the public tunnel endpoint.</returns>
    public static EndpointReference GetEndpoint(this IResourceBuilder<DevTunnelResource> tunnelBuilder, IResource resource, string endpointName)
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(endpointName);

        var portResource = tunnelBuilder.Resource.Ports
            .FirstOrDefault(p => p.TargetEndpoint.Resource == resource && StringComparers.EndpointAnnotationName.Equals(p.TargetEndpoint.EndpointName, endpointName));

        if (portResource is null)
        {
            return CreateEndpointReferenceWithError(tunnelBuilder.Resource, resource, endpointName);
        }

        return portResource.TunnelEndpoint;
    }

    /// <summary>
    /// Gets the tunnel endpoint reference for the specified target endpoint.
    /// </summary>
    /// <param name="tunnelBuilder">The dev tunnel resource builder.</param>
    /// <param name="targetEndpointReference">The target endpoint reference.</param>
    /// <returns>An <see cref="EndpointReference"/> representing the public tunnel endpoint.</returns>
    public static EndpointReference GetEndpoint(this IResourceBuilder<DevTunnelResource> tunnelBuilder, EndpointReference targetEndpointReference)
    {
        ArgumentNullException.ThrowIfNull(tunnelBuilder);
        ArgumentNullException.ThrowIfNull(targetEndpointReference);

        var portResource = tunnelBuilder.Resource.Ports
            .FirstOrDefault(p => p.TargetEndpoint.Resource == targetEndpointReference.Resource
                && StringComparers.EndpointAnnotationName.Equals(p.TargetEndpoint.EndpointName, targetEndpointReference.EndpointName));

        if (portResource is null)
        {
            return CreateEndpointReferenceWithError(tunnelBuilder.Resource, targetEndpointReference.Resource, targetEndpointReference.EndpointName);
        }

        return portResource.TunnelEndpoint;
    }

    private static EndpointReference CreateEndpointReferenceWithError(DevTunnelResource tunnelResource, IResource targetResource, string endpointName)
    {
        return new EndpointReference(tunnelResource, endpointName)
        {
            ErrorMessage = $"The dev tunnel '{tunnelResource.Name}' has not been associated with '{endpointName}' on resource '{targetResource.Name}'. Use 'WithReference({targetResource.Name})' on the dev tunnel to expose this endpoint."
        };
    }

    /// <summary>
    /// Injects service discovery and endpoint information as environment variables from the dev tunnel resource into the destination resource, using the tunneled resource's name as the service name.
    /// Each endpoint defined on the target resource will be injected using the format defined by the <see cref="ReferenceEnvironmentInjectionAnnotation"/> on the destination resource, i.e.
    /// either "services__{sourceResourceName}__{endpointName}__{endpointIndex}={uriString}" for .NET service discovery, or "{RESOURCE_ENDPOINT}={uri}" for endpoint injection.
    /// </summary>
    /// <remarks>
    /// Referencing a dev tunnel will delay the start of the resource until the referenced dev tunnel's endpoint is allocated.
    /// </remarks>
    /// <param name="builder">The builder.</param>
    /// <param name="targetResource">The resource to inject service discovery information for.</param>
    /// <param name="tunnelResource">The dev tunnel resource to resolve the tunnel address from.</param>
    /// <returns>The builder.</returns>
    public static IResourceBuilder<TResource> WithReference<TResource>(this IResourceBuilder<TResource> builder,
        IResourceBuilder<IResourceWithEndpoints> targetResource, IResourceBuilder<DevTunnelResource> tunnelResource)
        where TResource : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(targetResource);
        ArgumentNullException.ThrowIfNull(tunnelResource);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            // Skip DevTunnel operations during publish mode to avoid hanging
            return builder;
        }

        builder
            .WithReferenceRelationship(tunnelResource)
            .WithEnvironment(context =>
            {
                // Determine what to inject based on the annotation on the destination resource
                var injectionAnnotation = context.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var annotation) ? annotation : null;
                var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

                // Add environment variables for each tunnel port that references an endpoint on the target resource
                foreach (var port in tunnelResource.Resource.Ports.Where(p => p.TargetEndpoint.Resource == targetResource.Resource))
                {
                    var serviceName = targetResource.Resource.Name;
                    var endpointName = port.TargetEndpoint.EndpointName;
                    var encodedServiceName = EnvironmentVariableNameEncoder.Encode(serviceName);
                    var encodedEndpointName = EnvironmentVariableNameEncoder.Encode(endpointName);

                    if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
                    {
                        context.EnvironmentVariables[$"services__{serviceName}__{endpointName}__0"] = port.TunnelEndpoint;
                    }

                    if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
                    {
                        var endpointKey = $"{encodedServiceName.ToUpperInvariant()}_{encodedEndpointName.ToUpperInvariant()}";
                        context.EnvironmentVariables[endpointKey] = port.TunnelEndpoint;
                    }
                }
            });

        return builder;
    }

    private static void AddDevTunnelPort(
        IResourceBuilder<DevTunnelResource> tunnelBuilder,
        EndpointReference targetEndpoint,
        DevTunnelPortOptions? portOptions)
    {
        var tunnel = tunnelBuilder.Resource;
        var targetResource = targetEndpoint.Resource;

        if (tunnel.Ports.FirstOrDefault(p => p.TargetEndpoint == targetEndpoint) is { } existingPort)
        {
            // Port already added to the tunnel for this endpoint
            throw new ArgumentException($"Target endpoint '{targetEndpoint.EndpointName}' on resource '{targetEndpoint.Resource.Name}' has already been added to dev tunnel '{tunnel.Name}'.", nameof(targetEndpoint));
        }

        if (targetEndpoint.Resource.Annotations.OfType<EndpointAnnotation>()
            .SingleOrDefault(a => StringComparers.EndpointAnnotationName.Equals(a.Name, targetEndpoint.EndpointName)) is { } targetEndpointAnnotation)
        {
            // The target endpoint already exists so let's ensure it's target is localhost
            if (!EndpointHostHelpers.IsLocalhostOrLocalhostTld(targetEndpointAnnotation.TargetHost))
            {
                // Target endpoint is not localhost so can't be tunneled
                throw new ArgumentException($"Cannot tunnel endpoint '{targetEndpointAnnotation.Name}' with host '{targetEndpointAnnotation.TargetHost}' on resource '{targetResource.Name}' because it is not a localhost endpoint.", nameof(targetEndpoint));
            }
        }

        portOptions ??= new();
        if (portOptions.Protocol is { } proto && proto is not "http" and not "https" and not "auto")
        {
            throw new ArgumentException($"Invalid protocol '{proto}' specified in port options. Supported protocols are 'http', 'https', or 'auto'. Set protocol to null to use the endpoint's scheme.", nameof(portOptions));
        }
        portOptions.Protocol ??= targetEndpoint.Scheme switch
        {
            "https" or "http" => targetEndpoint.Scheme,
            _ => throw new ArgumentException($"Cannot tunnel endpoint '{targetEndpoint.EndpointName}' on resource '{targetResource.Name}' because it uses the unsupported scheme '{targetEndpoint.Scheme}'. Only 'http' and 'https' endpoints can be tunneled."),
        };
        portOptions.Description ??= $"{targetResource.Name}/{targetEndpoint.EndpointName}";

        var portName = $"{tunnel.Name}-{targetResource.Name}-{targetEndpoint.EndpointName}";
        portOptions.Labels ??= [];
        portOptions.Labels.Add(targetResource.Name);
        portOptions.Labels.Add(targetEndpoint.EndpointName);

        if (!TryValidateLabels(portOptions.Labels, out var errorMessage))
        {
            throw new ArgumentException(errorMessage, nameof(portOptions));
        }

        var portResource = new DevTunnelPortResource(
            portName,
            tunnel,
            targetEndpoint,
            portOptions);

        tunnel.Ports.Add(portResource);

        // Add the tunnel endpoint annotation
        portResource.Annotations.Add(portResource.TunnelEndpointAnnotation);

        // Health check
        var healtCheckKey = $"{portName}-check";
        tunnelBuilder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healtCheckKey,
            services => new DevTunnelPortHealthCheck(tunnel, targetEndpoint.Port),
            failureStatus: default,
            tags: default,
            timeout: default));

        var portBuilder = tunnelBuilder.ApplicationBuilder.AddResource(portResource)
            // visual grouping beneath the tunnel
            .WithParentRelationship(tunnelBuilder)
            // indicate the target resource relationship
            .WithReferenceRelationship(targetResource)
            .ExcludeFromManifest() // Dev tunnels do not get deployed
            .WithHealthCheck(healtCheckKey)
            // NOTE:
            // The endpoint target full host is set by the dev tunnels service and is not known in advance, but the suffix is always devtunnels.ms
            // We might consider updating the central logic that creates endpoint URLs to allow setting a target host like *.devtunnels.ms & if the
            // host of the allocated endpoint matches that pattern, *don't* try to add a localhost version of the URL too (because it won't work), e.g.:
            //  .WithEndpoint(DevTunnelPortResource.TunnelEndpointName, e => { e.TargetHost = "*.devtunnels.ms"; }, createIfNotExists: false)
            .WithUrls(static context =>
            {
                var urls = context.Urls;

                // Remove the port and trailing slash from the tunnel URL since the dev tunnels service always uses 443 for HTTPS
                if (urls.FirstOrDefault(u => string.Equals(u.Endpoint?.EndpointName, DevTunnelPortResource.TunnelEndpointName, StringComparisons.EndpointAnnotationName)
                                             && !string.Equals(new UriBuilder(u.Url).Host, "localhost")) is { } tunnelUrl)
                {
                    tunnelUrl.Url = new UriBuilder(tunnelUrl.Url).Uri.ToString().TrimEnd('/');
                }

                // Remove the localhost version of the tunnel URL that's added by the central endpoint URL logic
                // HACK: See the NOTE above about potentially handling this more generically in the central endpoint URL logic
                if (urls.FirstOrDefault(u => string.Equals(u.Endpoint?.EndpointName, DevTunnelPortResource.TunnelEndpointName, StringComparisons.EndpointAnnotationName)
                                             && string.Equals(new UriBuilder(u.Url).Host, "localhost", StringComparison.OrdinalIgnoreCase)) is { } localhostTunnelUrl)
                {
                    urls.Remove(localhostTunnelUrl);
                }

                // Remove any existing inspect URL
                if (urls.FirstOrDefault(u => string.Equals(u.DisplayText, "Inspect", StringComparison.OrdinalIgnoreCase)) is { } inspectUrl)
                {
                    urls.Remove(inspectUrl);
                }

                // Add the inspect URL if available
                var portResource = (DevTunnelPortResource)context.Resource;
                if (portResource.LastKnownStatus?.PortUri is { } portUri)
                {
                    // If tunnel host is sdfdff-3456.usw.devtunnels.ms, the inspect host is sdfdff-3456-inspect.usw.devtunnels.ms
                    var hostPrefixLength = portUri.Host.IndexOf('.');
                    var hostPrefix = portUri.Host[..hostPrefixLength];
                    var hostSuffix = portUri.Host[hostPrefixLength..];
                    urls.Add(new()
                    {
                        Url = new UriBuilder(portUri) { Host = $"{hostPrefix}-inspect{hostSuffix}" }.Uri.ToString(),
                        DisplayText = "Inspect",
                        DisplayLocation = UrlDisplayLocation.DetailsOnly
                    });
                }
            })
            .ExcludeFromManifest() // Dev tunnels do not get deployed
            .WithIconName("VirtualNetwork")
            .WithInitialState(new()
            {
                ResourceType = "DevTunnelPort",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, $"{targetResource.Name}/{targetEndpoint.EndpointName}"),
                    new("TargetResource", targetResource.Name),
                    new("TargetEndpoint", targetEndpoint.EndpointName),
                    new("Protocol", portOptions.Protocol),
                    new("Description", portOptions.Description),
                    new("Labels", portOptions.Labels is null ? "" : $"{string.Join(", ", portOptions.Labels)}"),
                ]
            });

        // Lifecycle from the tunnel
        tunnelBuilder
            .OnResourceReady(async (tunnelResource, e, ct) =>
            {
                // Update the port now that the tunnel is ready (healthy)
                // We need to do this in this handler so that it runs every time the tunnel is started
                var tunnelStatus = portResource.DevTunnel.LastKnownStatus;
                var tunnelPortStatus = portResource.LastKnownStatus;

                // Ensure the expected state for the port still exists after the ready event was raised
                if (tunnelStatus?.HostConnections is 0 or null || tunnelPortStatus?.PortUri is null)
                {
                    // Tunnel is not ready
                    return;
                }

                var services = e.Services;
                var eventing = services.GetRequiredService<IDistributedApplicationEventing>();
                var notifications = services.GetRequiredService<ResourceNotificationService>();

                // Mark the port as starting
                await eventing.PublishAsync<BeforeResourceStartedEvent>(new(portResource, services), EventDispatchBehavior.NonBlockingSequential, ct).ConfigureAwait(false);
                await notifications.PublishUpdateAsync(portResource, snapshot => snapshot with
                {
                    State = KnownResourceStates.Starting,
                    StartTimeStamp = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Allocate endpoint to the tunnel port
                var raiseEndpointsAllocatedEvent = portResource.TunnelEndpointAnnotation.AllocatedEndpoint is null;
                portResource.TunnelEndpointAnnotation.AllocatedEndpoint = new(portResource.TunnelEndpointAnnotation, tunnelPortStatus.PortUri.Host, 443 /* Always 443 for public tunnel endpoint */);

                // We can only raise the endpoints allocated event once as the central URL logic assumes it's a one-time event per resource.
                if (raiseEndpointsAllocatedEvent)
                {
                    await eventing.PublishAsync<ResourceEndpointsAllocatedEvent>(new(portResource, services), ct).ConfigureAwait(false);
                }

                // Mark the port as running
                await notifications.PublishUpdateAsync(portResource, snapshot => snapshot with
                {
                    State = KnownResourceStates.Running,
                    Urls = [.. snapshot.Urls.Select(u => u with
                        {
                            Url = raiseEndpointsAllocatedEvent
                                  // The event was raised so the URL was already updated
                                  ? u.Url
                                  : string.Equals(u.Name, DevTunnelPortResource.TunnelEndpointName, StringComparisons.EndpointAnnotationName)
                                      // Update the URL to use the allocated tunnel endpoint in case it changed since the last time it started
                                      ? new UriBuilder(portResource.TunnelEndpoint.Url).Uri.ToString().TrimEnd('/')
                                      // Not the tunnel endpoint URL so leave it as-is
                                      : u.Url,
                            IsInactive = false /* All URLs active */
                        })]
                }).ConfigureAwait(false);

                var portLogger = services.GetRequiredService<ResourceLoggerService>().GetLogger(portResource);
                portLogger.LogInformation("Forwarding from {PortUrl} to {TargetUrl} ({TargetResourceName}/{TargetEndpointName})", tunnelPortStatus.PortUri.ToString().TrimEnd('/'), portResource.TargetEndpoint.Url, portResource.TargetEndpoint.Resource.Name, portResource.TargetEndpoint.EndpointName);

                // Log anonymous access status
                try
                {
                    var effectivePolicy = portResource.LastKnownAccessStatus?.LogAnonymousAccessPolicy(portLogger);
                    if (effectivePolicy is not null)
                    {
                        // Set property detailing the anonymous access status
                        await notifications.PublishUpdateAsync(portResource, snapshot => snapshot with
                        {
                            Properties = [
                                .. snapshot.Properties.Where(p => !string.Equals(p.Name, "Anonymous access", StringComparison.OrdinalIgnoreCase)),
                                new("Anonymous access", effectivePolicy)
                            ]
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        portLogger.LogDebug("Anonymous access status unavailable for port at this time (tunnel or port access status null)");
                    }
                }
                catch (Exception ex)
                {
                    portLogger.LogDebug(ex, "Failed to log anonymous access status for port");
                }
            })
            .OnResourceStopped(async (tunnelResource, e, ct) =>
            {
                // Tunnel stopped, mark port as stopped too
                portResource.LastKnownStatus = null;

                var portLogger = e.Services.GetRequiredService<ResourceLoggerService>().GetLogger(portResource);
                var notifications = e.Services.GetRequiredService<ResourceNotificationService>();
                var eventing = e.Services.GetRequiredService<IDistributedApplicationEventing>();

                portLogger.LogInformation("Port forwarding stopped");
                CustomResourceSnapshot? stoppedSnapshot = default;
                await notifications.PublishUpdateAsync(portResource, snapshot => stoppedSnapshot = snapshot with
                {
                    State = KnownResourceStates.Finished,
                    StopTimeStamp = DateTime.UtcNow,
                    Urls = [.. snapshot.Urls.Select(u => u with { IsInactive = true /* All URLs inactive */ })]
                }).ConfigureAwait(false);
                await eventing.PublishAsync<ResourceStoppedEvent>(new(portResource, e.Services, new(portResource, portResource.Name, stoppedSnapshot!)), ct).ConfigureAwait(false);
            });
    }

    private static string GetUserAgent()
    {
        var assembly = typeof(DevTunnelResource).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        return new ProductInfoHeaderValue("Aspire.DevTunnels", version).ToString();
    }

    private static bool TryValidateLabels(List<string>? labels, [NotNullWhen(false)] out string? errorMessage)
    {
        if (labels is null || labels.Count == 0)
        {
            errorMessage = null;
            return true;
        }

        foreach (var label in labels)
        {
            // Validate the label format '[\w-=]{1,50}'
            if (!LabelRegex().IsMatch(label))
            {
                errorMessage = $"""
                    The label '{label}' is invalid. A valid label must:
                    - consist of letters, numbers, underscores, hyphens, or equals signs
                    - be 1-50 characters long
                    """;
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$")]
    private static partial Regex TunnelIdRegex();

    [GeneratedRegex(@"^[\w\-=_]{1,50}$")]
    private static partial Regex LabelRegex();
}
