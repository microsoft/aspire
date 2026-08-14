// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Otlp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring OpenTelemetry endpoints for MAUI platform resources.
/// </summary>
public static class MauiOtlpExtensions
{
    private const string DcpExecutableTerminatedState = "Terminated";
    private const string OtlpGrpcProtocol = "grpc";
    private const string OtlpHttpProtobufProtocol = "http/protobuf";

    /// <summary>
    /// Configures the MAUI platform resource to send OpenTelemetry data through an automatically created dev tunnel.
    /// This is the easiest option for most scenarios, as it handles tunnel creation, configuration, and endpoint
    /// injection automatically.
    /// </summary>
    /// <typeparam name="T">The MAUI platform resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// This method creates a dev tunnel automatically and configures the MAUI platform resource to route
    /// OTLP traffic through it. This is the recommended approach for most scenarios as it requires minimal
    /// configuration and works reliably across all mobile platforms.
    /// </para>
    /// <para>
    /// Prerequisites:
    /// <list type="bullet">
    ///   <item>Aspire.Hosting.DevTunnels package must be referenced</item>
    ///   <item>Dev tunnel CLI must be installed (automatic prompt if missing)</item>
    ///   <item>User must be logged in to dev tunnel service (automatic prompt if needed)</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// Configure a MAUI Android device to automatically use a dev tunnel for telemetry:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// maui.AddAndroidDevice()
    ///     .WithOtlpDevTunnel(); // That's it - everything is configured automatically!
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithOtlpDevTunnel<T>(
        this IResourceBuilder<T> builder)
        where T : IMauiPlatformResource, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Get shared state - only create stub + tunnel once per app
        var platformResource = builder.Resource;
        var parentBuilder = builder.ApplicationBuilder.CreateResourceBuilder(platformResource.Parent);
        var configuration = builder.ApplicationBuilder.Configuration;

        // Check if we already created the stub + tunnel for this MAUI project
        if (!parentBuilder.Resource.TryGetLastAnnotation<OtlpDevTunnelConfigurationAnnotation>(out var tunnelConfig))
        {
            // First time - create stub and dev tunnel
            tunnelConfig = CreateOtlpDevTunnelInfrastructure(parentBuilder, configuration);
            parentBuilder.Resource.Annotations.Add(tunnelConfig);
        }

        // Now apply the configuration to this specific platform
        ApplyOtlpConfigurationToPlatform(builder, tunnelConfig);

        return builder;
    }

    /// <summary>
    /// Creates the OTLP dev tunnel infrastructure (stub resource + dev tunnel).
    /// This is only created once per MAUI project and shared across all platforms.
    /// </summary>
    private static OtlpDevTunnelConfigurationAnnotation CreateOtlpDevTunnelInfrastructure(
        IResourceBuilder<MauiProjectResource> parentBuilder,
        IConfiguration configuration)
    {
        var appBuilder = parentBuilder.ApplicationBuilder;
        var configuredOtlpEndpoint = ResolveConfiguredOtlpEndpoint(configuration);
        // Dynamic dashboard endpoints start with a provisional scheme and no port. The actual
        // scheme and port are copied from the dashboard allocation event before the dev tunnel
        // can consume the endpoint.
        var initialOtlpScheme = configuredOtlpEndpoint?.Scheme ?? ResolveDynamicDashboardOtlpScheme(configuration);

        // Create names for the tunnel infrastructure
        // Use a short random suffix to ensure uniqueness (similar to DCP naming strategy)
        // The dev tunnel port resource name will be: {parent resource name}-{random}-otlp
        var randomSuffix = Guid.NewGuid().ToString("N")[..8];
        var tunnelName = parentBuilder.Resource.Name;
        var stubName = $"t{randomSuffix}"; // Prefix with 't' to ensure valid resource name

        // Create OtlpLoopbackResource - a synthetic IResourceWithEndpoints for service discovery
        var stubResource = new OtlpLoopbackResource(stubName, configuredOtlpEndpoint?.Port, initialOtlpScheme);
        stubResource.OtlpEndpoint.Transport = GetEndpointTransport(configuredOtlpEndpoint?.Protocol);
        OtlpDevTunnelConfigurationAnnotation? tunnelConfig = null;

        var stubBuilder = appBuilder.AddResource(stubResource)
            .ExcludeFromManifest();

        // Hide the stub from the dashboard UI
        stubBuilder.WithHidden().WithInitialState(new CustomResourceSnapshot
        {
            ResourceType = "OtlpStub",
            Properties = []
        });

        if (configuredOtlpEndpoint is null)
        {
            appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(async (evt, ct) =>
            {
                if (evt.Resource is DevTunnelResource devTunnelResource &&
                    string.Equals(devTunnelResource.Name, tunnelName, StringComparisons.ResourceName))
                {
                    var currentTunnelConfig = tunnelConfig ?? throw new InvalidOperationException("The MAUI OTLP dev tunnel configuration was not initialized before tunnel startup.");
                    if (currentTunnelConfig.IsOtlpEndpointResolved)
                    {
                        return;
                    }

                    var dashboardResource = appBuilder.Resources.FirstOrDefault(resource =>
                        string.Equals(resource.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName));
                    if (dashboardResource is null)
                    {
                        throw new DistributedApplicationException($"The MAUI OTLP dev tunnel for resource '{parentBuilder.Resource.Name}' requires the Aspire dashboard to be enabled or an explicit OTLP endpoint URL to be configured.");
                    }

                    evt.Services.GetRequiredService<ResourceLoggerService>()
                        .GetLogger(devTunnelResource)
                        .LogInformation(
                            "Waiting up to {Timeout} for the Aspire dashboard to publish a concrete OTLP listener.",
                            currentTunnelConfig.RuntimeSnapshotResolutionTimeout);

                    if (await TryResolveDashboardOtlpEndpointAsync(
                        dashboardResource,
                        evt.Services,
                        waitForRuntimeSnapshot: true,
                        currentTunnelConfig.RuntimeSnapshotResolutionTimeout,
                        ct).ConfigureAwait(false) is { } dashboardOtlpEndpoint)
                    {
                        await AllocateOtlpStubEndpointAsync(currentTunnelConfig, dashboardOtlpEndpoint, evt.Services, appBuilder.Eventing, ct).ConfigureAwait(false);
                        return;
                    }

                    throw new DistributedApplicationException($"The Aspire dashboard resource '{KnownResourceNames.AspireDashboard}' terminated or does not have a concrete OTLP endpoint named '{KnownEndpointNames.OtlpHttpEndpointName}' or '{KnownEndpointNames.OtlpGrpcEndpointName}', so the MAUI OTLP dev tunnel for resource '{parentBuilder.Resource.Name}' cannot start. Ensure dashboard OTLP ingestion is enabled, or configure an explicit OTLP endpoint URL.");
                }
            });

            appBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(async (evt, ct) =>
            {
                var currentTunnelConfig = tunnelConfig ?? throw new InvalidOperationException("The MAUI OTLP dev tunnel configuration was not initialized before endpoint allocation.");
                if (await TryResolveDashboardOtlpEndpointAsync(
                    evt.Resource,
                    evt.Services,
                    waitForRuntimeSnapshot: false,
                    currentTunnelConfig.RuntimeSnapshotResolutionTimeout,
                    ct).ConfigureAwait(false) is { } dashboardOtlpEndpoint)
                {
                    await AllocateOtlpStubEndpointAsync(currentTunnelConfig, dashboardOtlpEndpoint, evt.Services, appBuilder.Eventing, ct).ConfigureAwait(false);
                }
            });
        }
        else
        {
            appBuilder.OnBeforeStart((evt, ct) =>
                appBuilder.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(stubResource, evt.Services), ct));
        }

        // Create dev tunnel with anonymous access for OTLP. The dynamic unresolved-endpoint guard above
        // must be registered first so it can fail fast before the dev tunnel waits on the target endpoint.
        var devTunnel = appBuilder.AddDevTunnel(tunnelName)
            .WithAnonymousAccess()
            .WithReference(stubBuilder, new DevTunnelPortOptions { Protocol = "https" });

        tunnelConfig = new OtlpDevTunnelConfigurationAnnotation(
            stubResource,
            stubBuilder,
            devTunnel,
            isOtlpEndpointResolved: configuredOtlpEndpoint is not null);
        return tunnelConfig;
    }

    private static OtlpEndpointTarget? ResolveConfiguredOtlpEndpoint(IConfiguration configuration)
    {
        var configuredGrpcUrl = configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl, fallbackOnEmpty: true);
        var configuredHttpUrl = configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl, fallbackOnEmpty: true);

        if (string.IsNullOrWhiteSpace(configuredGrpcUrl) && string.IsNullOrWhiteSpace(configuredHttpUrl))
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(configuredHttpUrl)
            ? CreateConfiguredOtlpEndpointTarget(configuredHttpUrl, KnownConfigNames.DashboardOtlpHttpEndpointUrl, OtlpHttpProtobufProtocol)
            : CreateConfiguredOtlpEndpointTarget(configuredGrpcUrl!, KnownConfigNames.DashboardOtlpGrpcEndpointUrl, OtlpGrpcProtocol);
    }

    private static OtlpEndpointTarget CreateConfiguredOtlpEndpointTarget(string url, string configKey, string protocol)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !IsLocalDashboardBinding(uri) ||
            uri.Port is < 1 or > 65535)
        {
            throw new DistributedApplicationException($"The configured OTLP endpoint URL '{url}' from '{configKey}' must be an absolute locally reachable HTTP or HTTPS URL with a port between 1 and 65535.");
        }

        return new OtlpEndpointTarget(uri.Scheme, uri.Port, protocol);
    }

    private static string ResolveDynamicDashboardOtlpScheme(IConfiguration configuration)
        => configuration.GetBool(KnownConfigNames.AllowUnsecuredTransport) is true ? "http" : "https";

    private static async ValueTask<OtlpEndpointTarget?> TryResolveDashboardOtlpEndpointAsync(
        IResource resource,
        IServiceProvider services,
        bool waitForRuntimeSnapshot,
        TimeSpan runtimeSnapshotResolutionTimeout,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(resource.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName) || resource is not IResourceWithEndpoints dashboardResource)
        {
            return null;
        }

        // Mobile runtimes use the dev tunnel's HTTPS forwarding path most reliably with OTLP/HTTP.
        // An existing but unresolved HTTP endpoint is not a reason to fall back to gRPC.
        var httpEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpHttpEndpointName);
        if (httpEndpoint.Exists)
        {
            if (await TryResolveEndpointAsync(httpEndpoint, OtlpHttpProtobufProtocol, cancellationToken).ConfigureAwait(false) is { } httpTarget)
            {
                return httpTarget;
            }

            if (await HasUnresolvedTargetPortExpressionAsync(httpEndpoint, cancellationToken).ConfigureAwait(false))
            {
                return await TryResolveDashboardOtlpEndpointFromRuntimeSnapshotAsync(
                    httpEndpoint,
                    services,
                    waitForRuntimeSnapshot,
                    runtimeSnapshotResolutionTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var grpcEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpGrpcEndpointName);
        if (await TryResolveEndpointAsync(grpcEndpoint, OtlpGrpcProtocol, cancellationToken).ConfigureAwait(false) is { } grpcTarget)
        {
            return grpcTarget;
        }

        if (!await HasUnresolvedTargetPortExpressionAsync(grpcEndpoint, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await TryResolveDashboardOtlpEndpointFromRuntimeSnapshotAsync(
            grpcEndpoint,
            services,
            waitForRuntimeSnapshot,
            runtimeSnapshotResolutionTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<OtlpEndpointTarget?> TryResolveEndpointAsync(
        EndpointReference endpointReference,
        string protocol,
        CancellationToken cancellationToken)
    {
        if (!endpointReference.Exists || endpointReference.EndpointAnnotation.AllocatedEndpoint is not { } allocatedEndpoint)
        {
            return null;
        }

        var port = allocatedEndpoint.Port;
        if (endpointReference.Resource.IsContainer())
        {
            var allocatedPort = await TryGetEndpointPortAsync(endpointReference, EndpointProperty.Port, cancellationToken).ConfigureAwait(false);
            if (allocatedPort.Port is { } resolvedAllocatedPort)
            {
                port = resolvedAllocatedPort;
            }
        }
        else
        {
            // DCP-proxied executable endpoints expose the proxy as Port. Dev tunnels run on the host
            // and must forward to the dashboard's concrete target listener instead.
            var targetPort = await TryGetEndpointPortAsync(endpointReference, EndpointProperty.TargetPort, cancellationToken).ConfigureAwait(false);
            if (IsLocalTunnelTargetHost(endpointReference.EndpointAnnotation.TargetHost) &&
                targetPort.Port is { } resolvedTargetPort)
            {
                port = resolvedTargetPort;
            }
            else if (IsLocalTunnelTargetHost(endpointReference.EndpointAnnotation.TargetHost) &&
                     targetPort.HasUnresolvedExpression)
            {
                return null;
            }
        }

        return new OtlpEndpointTarget(allocatedEndpoint.UriScheme, port, protocol);
    }

    private static async ValueTask<bool> HasUnresolvedTargetPortExpressionAsync(
        EndpointReference endpointReference,
        CancellationToken cancellationToken)
    {
        if (!endpointReference.Exists ||
            endpointReference.EndpointAnnotation.AllocatedEndpoint is null ||
            endpointReference.Resource.IsContainer())
        {
            return false;
        }

        var targetPort = await TryGetEndpointPortAsync(endpointReference, EndpointProperty.TargetPort, cancellationToken).ConfigureAwait(false);
        return targetPort.HasUnresolvedExpression;
    }

    private static async ValueTask<EndpointPortResolution> TryGetEndpointPortAsync(
        EndpointReference endpointReference,
        EndpointProperty property,
        CancellationToken cancellationToken)
    {
        string? portValue;
        try
        {
            portValue = await endpointReference.Property(property).GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (property == EndpointProperty.TargetPort && endpointReference.IsAllocated)
        {
            return default;
        }

        if (int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return new(port, HasUnresolvedExpression: false);
        }

        // DCP reports dynamically assigned target listener ports as expressions such as:
        //   {{- portForServing "dashboard-otlp-http" -}}
        return string.IsNullOrWhiteSpace(portValue)
            ? default
            : new(null, HasUnresolvedExpression: true);
    }

    private static async ValueTask<OtlpEndpointTarget?> TryResolveDashboardOtlpEndpointFromRuntimeSnapshotAsync(
        EndpointReference endpointReference,
        IServiceProvider services,
        bool waitForRuntimeSnapshot,
        TimeSpan runtimeSnapshotResolutionTimeout,
        CancellationToken cancellationToken)
    {
        var notificationService = services.GetService<ResourceNotificationService>();
        if (notificationService is null)
        {
            return null;
        }

        var resourceName = endpointReference.Resource.Name;
        if (notificationService.TryGetCurrentState(resourceName, out var currentState) &&
            TryResolveDashboardOtlpEndpointFromSnapshot(endpointReference, currentState) is { } currentTarget)
        {
            return currentTarget;
        }

        if (!waitForRuntimeSnapshot)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(runtimeSnapshotResolutionTimeout);

        ResourceEvent resourceEvent;
        try
        {
            resourceEvent = await notificationService.WaitForResourceAsync(
                resourceName,
                resourceEvent => TryResolveDashboardOtlpEndpointFromSnapshot(endpointReference, resourceEvent) is not null ||
                    IsUnavailableState(resourceEvent.Snapshot.State?.Text),
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DistributedApplicationException(
                $"The Aspire dashboard resource '{resourceName}' did not publish a concrete OTLP listener within {runtimeSnapshotResolutionTimeout:c}.",
                ex);
        }

        return TryResolveDashboardOtlpEndpointFromSnapshot(endpointReference, resourceEvent);
    }

    private static OtlpEndpointTarget? TryResolveDashboardOtlpEndpointFromSnapshot(
        EndpointReference endpointReference,
        ResourceEvent resourceEvent)
    {
        if (IsUnavailableState(resourceEvent.Snapshot.State?.Text) ||
            !endpointReference.Exists ||
            endpointReference.EndpointAnnotation.AllocatedEndpoint is null)
        {
            return null;
        }

        if (endpointReference.Resource.IsContainer())
        {
            var allocatedEndpoint = endpointReference.EndpointAnnotation.AllocatedEndpoint;
            return new OtlpEndpointTarget(
                allocatedEndpoint.UriScheme,
                allocatedEndpoint.Port,
                GetOtlpProtocol(endpointReference.EndpointAnnotation));
        }

        string[]? environmentVariableNames =
            string.Equals(endpointReference.EndpointName, KnownEndpointNames.OtlpHttpEndpointName, StringComparisons.EndpointAnnotationName)
                ? [KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl]
                : string.Equals(endpointReference.EndpointName, KnownEndpointNames.OtlpGrpcEndpointName, StringComparisons.EndpointAnnotationName)
                    ? [KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl]
                    : null;

        var endpointUrl = resourceEvent.Snapshot.EnvironmentVariables
            .FirstOrDefault(environmentVariable => environmentVariableNames?.Contains(
                environmentVariable.Name,
                StringComparers.EnvironmentVariableName) is true)
            ?.Value;

        return Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) &&
            IsLocalDashboardBinding(uri) &&
            uri.Port is >= 1 and <= 65535 &&
            uri.Scheme is "http" or "https"
                ? new OtlpEndpointTarget(uri.Scheme, uri.Port, GetOtlpProtocol(endpointReference.EndpointAnnotation))
                : null;
    }

    private static bool IsUnavailableState(string? state) =>
        state is not null &&
        (KnownResourceStates.TerminalStates.Contains(state, StringComparers.ResourceState) ||
         // Resource notifications can expose the raw DCP executable state before it is normalized.
         // DCP emits "Terminated" when the controller kills the dashboard executable.
         string.Equals(state, DcpExecutableTerminatedState, StringComparisons.ResourceState) ||
         string.Equals(state, KnownResourceStates.RuntimeUnhealthy, StringComparisons.ResourceState));

    private static bool IsLocalDashboardBinding(Uri uri) =>
        IsLocalTunnelTargetHost(uri.Host);

    private static bool IsLocalTunnelTargetHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host is "*" or "+" ||
            EndpointHostHelpers.IsLocalhostOrLocalhostTld(host))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) &&
            (IPAddress.IsLoopback(address) ||
             address.Equals(IPAddress.Any) ||
             address.Equals(IPAddress.IPv6Any));
    }

    private static async Task AllocateOtlpStubEndpointAsync(
        OtlpDevTunnelConfigurationAnnotation tunnelConfig,
        OtlpEndpointTarget target,
        IServiceProvider services,
        IDistributedApplicationEventing eventing,
        CancellationToken cancellationToken)
    {
        if (!tunnelConfig.UpdateOtlpEndpoint(target.Scheme, target.Port, GetEndpointTransport(target.Protocol)))
        {
            return;
        }

        // The stub endpoint is synthetic and not allocated by DCP. Publishing the event keeps
        // endpoint consumers such as dev tunnel ports on the normal endpoint-allocation path.
        await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(tunnelConfig.OtlpStub, services), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies OTLP configuration to a specific MAUI platform resource.
    /// Gets the tunneled endpoint directly and sets OTEL_EXPORTER_OTLP_ENDPOINT.
    /// </summary>
    private static void ApplyOtlpConfigurationToPlatform<T>(
        IResourceBuilder<T> platformBuilder,
        OtlpDevTunnelConfigurationAnnotation tunnelConfig)
        where T : IMauiPlatformResource, IResourceWithEnvironment
    {
        if (platformBuilder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return;
        }

        // Get the tunnel endpoint for the OTLP stub directly, bypassing service discovery injection
        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");

        // Ensure the platform resource waits for the tunnel to be ready
        platformBuilder.WithReferenceRelationship(tunnelConfig.DevTunnel);

        // Set OTEL_EXPORTER_OTLP_ENDPOINT directly to the tunnel endpoint URL
        platformBuilder.WithEnvironment(KnownOtelConfigNames.ExporterOtlpEndpoint, tunnelEndpoint);
        platformBuilder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] =
                new OtlpProtocolValueProvider(
                    tunnelConfig.OtlpStub.OtlpEndpoint,
                    tunnelConfig.RuntimeSnapshotResolutionTimeout);
        });
    }

    private static string GetEndpointTransport(string? otlpProtocol)
        => string.Equals(otlpProtocol, OtlpGrpcProtocol, StringComparison.OrdinalIgnoreCase) ? "http2" : "http";

    private static string GetOtlpProtocol(EndpointAnnotation endpoint)
        => string.Equals(endpoint.Transport, "http2", StringComparison.OrdinalIgnoreCase) ? OtlpGrpcProtocol : OtlpHttpProtobufProtocol;

    private readonly record struct OtlpEndpointTarget(string Scheme, int Port, string Protocol);
    private readonly record struct EndpointPortResolution(int? Port, bool HasUnresolvedExpression);

    private sealed class OtlpProtocolValueProvider(
        EndpointAnnotation endpoint,
        TimeSpan resolutionTimeout) : IValueProvider
    {
        public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            try
            {
#pragma warning disable CS0618 // Type or member is obsolete
                await endpoint.AllocatedEndpointSnapshot
                    .GetValueAsync(cancellationToken)
                    .WaitAsync(resolutionTimeout, cancellationToken)
                    .ConfigureAwait(false);
#pragma warning restore CS0618 // Type or member is obsolete
            }
            catch (TimeoutException ex)
            {
                throw new DistributedApplicationException(
                    $"The MAUI OTLP protocol could not be determined because the dashboard did not publish a concrete OTLP listener within {resolutionTimeout:c}.",
                    ex);
            }

            return GetOtlpProtocol(endpoint);
        }
    }
}
