// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Otlp;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring OpenTelemetry endpoints for MAUI platform resources.
/// </summary>
public static class MauiOtlpExtensions
{
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
            appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>((evt, _) =>
            {
                var currentTunnelConfig = tunnelConfig ?? throw new InvalidOperationException("The MAUI OTLP dev tunnel configuration was not initialized before tunnel startup.");
                if (evt.Resource is DevTunnelResource devTunnelResource &&
                    string.Equals(devTunnelResource.Name, tunnelName, StringComparisons.ResourceName) &&
                    !currentTunnelConfig.IsOtlpEndpointResolved)
                {
                    var hasDashboardResource = appBuilder.Resources.Any(resource => string.Equals(resource.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName));
                    if (!hasDashboardResource)
                    {
                        throw new DistributedApplicationException($"The MAUI OTLP dev tunnel for resource '{parentBuilder.Resource.Name}' requires the Aspire dashboard to be enabled or an explicit OTLP endpoint URL to be configured.");
                    }

                    throw new DistributedApplicationException($"The Aspire dashboard resource '{KnownResourceNames.AspireDashboard}' does not have an allocated OTLP endpoint named '{KnownEndpointNames.OtlpGrpcEndpointName}' or '{KnownEndpointNames.OtlpHttpEndpointName}', so the MAUI OTLP dev tunnel for resource '{parentBuilder.Resource.Name}' cannot start. Ensure dashboard OTLP ingestion is enabled, or configure an explicit OTLP endpoint URL.");
                }

                return Task.CompletedTask;
            });

            appBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(async (evt, ct) =>
            {
                var currentTunnelConfig = tunnelConfig ?? throw new InvalidOperationException("The MAUI OTLP dev tunnel configuration was not initialized before endpoint allocation.");
                if (!currentTunnelConfig.IsOtlpEndpointResolved &&
                    await TryResolveDashboardOtlpEndpointAsync(evt.Resource, ct).ConfigureAwait(false) is { } dashboardOtlpEndpoint)
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
            uri.Scheme is not ("http" or "https"))
        {
            throw new DistributedApplicationException($"The configured OTLP endpoint URL '{url}' from '{configKey}' must be an absolute HTTP or HTTPS URL.");
        }

        return new OtlpEndpointTarget(uri.Scheme, uri.Port, protocol);
    }

    private static string ResolveDynamicDashboardOtlpScheme(IConfiguration configuration)
        => configuration.GetBool(KnownConfigNames.AllowUnsecuredTransport) is true ? "http" : "https";

    private static async ValueTask<OtlpEndpointTarget?> TryResolveDashboardOtlpEndpointAsync(IResource resource, CancellationToken cancellationToken)
    {
        if (!string.Equals(resource.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName) || resource is not IResourceWithEndpoints dashboardResource)
        {
            return null;
        }

        // Use the dashboard OTLP/HTTP endpoint for mobile dev tunnels. HTTP/protobuf travels through
        // the dev tunnel's HTTPS forwarding path reliably, while gRPC over the tunnel can fail to
        // deliver client telemetry from mobile runtimes.
        var httpEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpHttpEndpointName);
        if (await TryResolveEndpointAsync(httpEndpoint, OtlpHttpProtobufProtocol, cancellationToken).ConfigureAwait(false) is { } target)
        {
            return target;
        }

        var grpcEndpoint = dashboardResource.GetEndpoint(KnownEndpointNames.OtlpGrpcEndpointName);
        return await TryResolveEndpointAsync(grpcEndpoint, OtlpGrpcProtocol, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<OtlpEndpointTarget?> TryResolveEndpointAsync(EndpointReference endpointReference, string protocol, CancellationToken cancellationToken)
    {
        if (!endpointReference.Exists || endpointReference.EndpointAnnotation.AllocatedEndpoint is not { } allocatedEndpoint)
        {
            return null;
        }

        var port = allocatedEndpoint.Port;

        // For DCP-proxied dashboard endpoints, AllocatedEndpoint.Port is the proxy port, but
        // devtunnel host forwards to localhost:<tunnel-port>. Prefer the target listener port
        // when the endpoint exposes it as a concrete value.
        if (await TryGetEndpointTargetPortAsync(endpointReference, cancellationToken).ConfigureAwait(false) is { } targetPort)
        {
            port = targetPort;
        }

        return new OtlpEndpointTarget(allocatedEndpoint.UriScheme, port, protocol);
    }

    private static async ValueTask<int?> TryGetEndpointTargetPortAsync(EndpointReference endpointReference, CancellationToken cancellationToken)
    {
        string? targetPortValue;
        try
        {
            targetPortValue = await endpointReference.Property(EndpointProperty.TargetPort).GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (endpointReference.IsAllocated)
        {
            return null;
        }

        return int.TryParse(targetPortValue, NumberStyles.None, CultureInfo.InvariantCulture, out var targetPort)
            ? targetPort
            : null;
    }

    private static Task AllocateOtlpStubEndpointAsync(
        OtlpDevTunnelConfigurationAnnotation tunnelConfig,
        OtlpEndpointTarget target,
        IServiceProvider services,
        IDistributedApplicationEventing eventing,
        CancellationToken cancellationToken)
    {
        var endpoint = tunnelConfig.OtlpStub.OtlpEndpoint;

        endpoint.UriScheme = target.Scheme;
        endpoint.Port = target.Port;
        endpoint.TargetPort = target.Port;
        endpoint.Transport = GetEndpointTransport(target.Protocol);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", target.Port);
        tunnelConfig.MarkOtlpEndpointResolved();

        // DCP can assign a provisional port to the synthetic stub before the dashboard endpoint is
        // known. Overwrite it with the real dashboard OTLP listener before endpoint consumers such
        // as dev tunnel ports resolve their target port.
        return eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(tunnelConfig.OtlpStub, services), cancellationToken);
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
        // Get the tunnel endpoint for the OTLP stub directly, bypassing service discovery injection
        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");

        // Ensure the platform resource waits for the tunnel to be ready
        platformBuilder.WithReferenceRelationship(tunnelConfig.DevTunnel);

        // Set OTEL_EXPORTER_OTLP_ENDPOINT directly to the tunnel endpoint URL
        platformBuilder.WithEnvironment(KnownOtelConfigNames.ExporterOtlpEndpoint, tunnelEndpoint);
        platformBuilder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] = GetOtlpProtocol(tunnelConfig.OtlpStub.OtlpEndpoint);
        });
    }

    private static string GetEndpointTransport(string? otlpProtocol)
        => string.Equals(otlpProtocol, OtlpGrpcProtocol, StringComparison.OrdinalIgnoreCase) ? "http2" : "http";

    private static string GetOtlpProtocol(EndpointAnnotation endpoint)
        => string.Equals(endpoint.Transport, "http2", StringComparison.OrdinalIgnoreCase) ? OtlpGrpcProtocol : OtlpHttpProtobufProtocol;

    private readonly record struct OtlpEndpointTarget(string Scheme, int Port, string Protocol);
}
