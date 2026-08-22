// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource.GetHostAddressExpression is experimental
#pragma warning disable ASPIREPROBES001 // EndpointProbeAnnotation is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Cdn;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Front Door resources to an Aspire application.
/// </summary>
public static class AzureFrontDoorExtensions
{
    /// <summary>
    /// Adds an Azure Front Door resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// Azure Front Door is a global, scalable entry point that uses the Microsoft global edge network to create
    /// fast, secure, and widely scalable web applications. Use <see cref="WithOrigin"/> to add origins
    /// (backends) to the Front Door resource. Each origin gets its own Front Door endpoint, origin group,
    /// and route, so each backend app is independently routable via its own <c>*.azurefd.net</c> hostname.
    /// </para>
    /// <para>
    /// When a backend is deployed as several regional stamps (see <c>WithComputeEnvironments</c>), its origin
    /// group holds one origin per stamp, and Front Door health-probes and load-balances across all of them
    /// behind that single hostname. That is what makes Front Door the global entry point of a multi-region
    /// deployment. Use <see cref="WithOriginGroup"/> to control routing, health probes, and per-stamp
    /// priorities or weights.
    /// </para>
    /// <para>
    /// For advanced scenarios (path-based routing, rule sets, WAF policies), use
    /// <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}"/> to customize the
    /// generated infrastructure directly.
    /// </para>
    /// <example>
    /// Add an Azure Front Door resource with origins:
    /// <code lang="C#">
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithExternalHttpEndpoints();
    /// var web = builder.AddProject&lt;Projects.Web&gt;("web")
    ///     .WithExternalHttpEndpoints();
    /// var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOrigin(api)
    ///     .WithOrigin(web);
    /// </code>
    /// </example>
    /// <example>
    /// Put one global entry point in front of an application deployed to two regions:
    /// <code lang="C#">
    /// var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
    /// var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithExternalHttpEndpoints()
    ///     .WithComputeEnvironments(eastus, westeu);
    ///
    /// builder.AddAzureFrontDoor("frontdoor").WithOrigin(api);
    /// </code>
    /// </example>
    /// </remarks>
    /// <ats-remarks />
    [AspireExport]
    public static IResourceBuilder<AzureFrontDoorResource> AddAzureFrontDoor(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = static (AzureResourceInfrastructure infrastructure) =>
        {
            var azureResource = (AzureFrontDoorResource)infrastructure.AspireResource;

            // Front Door is a global resource: it is deployed once and fronts every region, so it must not
            // itself be stamped.
            if (azureResource.IsStamped())
            {
                throw new InvalidOperationException(
                    $"Azure Front Door resource '{azureResource.Name}' is bound to multiple compute environments. " +
                    "Front Door is a global resource that is deployed once and routes to the regional stamps of its origins, so it cannot be stamped itself.");
            }

            // Create the CDN profile (Front Door)
            var profile = new CdnProfile(infrastructure.AspireResource.GetBicepIdentifier())
            {
                SkuName = CdnSkuName.StandardAzureFrontDoor,
                Location = new AzureLocation("Global"),
                Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
            };
            infrastructure.Add(profile);

            // Create a separate endpoint → origin group → route per WithOrigin call, and one origin per
            // regional stamp of that backend. Each backend app keeps its own Front Door hostname, and Front
            // Door load-balances across the app's regions behind it.
            var originAnnotations = azureResource.Annotations.OfType<AzureFrontDoorOriginAnnotation>().ToList();
            foreach (var originAnnotation in originAnnotations)
            {
                AddOriginGroup(infrastructure, profile, originAnnotation);
            }
        };

        var resource = new AzureFrontDoorResource(name, configureInfrastructure);

        return builder.ExecutionContext.IsPublishMode
            ? builder.AddResource(resource).WithIconName("GlobeArrowForward")
            : builder.CreateResourceBuilder(resource);
    }

    /// <summary>
    /// Adds an origin (backend) to the Azure Front Door resource.
    /// Each origin gets its own Front Door endpoint with a distinct <c>*.azurefd.net</c> hostname,
    /// its own origin group, and a default route.
    /// </summary>
    /// <typeparam name="T">The type of the resource with endpoints.</typeparam>
    /// <param name="builder">The Azure Front Door resource builder.</param>
    /// <param name="resource">The resource to add as an origin (e.g., a project, container, or other compute resource with endpoints).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// When <paramref name="resource"/> is deployed as several regional stamps, the origin group contains one
    /// origin per stamp and Front Door routes each request to the closest healthy region.
    /// </para>
    /// <example>
    /// Add multiple origins (each gets its own Front Door endpoint):
    /// <code lang="C#">
    /// var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOrigin(api)
    ///     .WithOrigin(web);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<AzureFrontDoorResource> WithOrigin<T>(
        this IResourceBuilder<AzureFrontDoorResource> builder,
        IResourceBuilder<T> resource) where T : IComputeResource, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return AddOriginAnnotation(builder, resource, new AzureFrontDoorOriginGroupBuilder());
    }

    /// <summary>
    /// Adds an origin (backend) to the Azure Front Door resource and configures the origin group that fronts it.
    /// </summary>
    /// <typeparam name="T">The type of the resource with endpoints.</typeparam>
    /// <param name="builder">The Azure Front Door resource builder.</param>
    /// <param name="resource">The resource to add as an origin (e.g., a project, container, or other compute resource with endpoints).</param>
    /// <param name="configure">Callback that configures routing, health probes, session affinity, per-stamp priorities and weights, and an optional custom domain.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <example>
    /// Route to the closest healthy region, probing a real health endpoint, and serve from a custom domain:
    /// <code lang="C#">
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithExternalHttpEndpoints()
    ///     .WithComputeEnvironments(eastus, westeu);
    ///
    /// builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOriginGroup(api, g =&gt; g
    ///         .WithRouting(FrontDoorOriginRouting.LatencyBased)
    ///         .WithHealthProbe("/health", FrontDoorHealthProbeProtocol.Https, TimeSpan.FromSeconds(30))
    ///         .WithCustomDomain("www.contoso.com"));
    /// </code>
    /// </example>
    /// <example>
    /// Run active/passive, preferring one region and failing over to another:
    /// <code lang="C#">
    /// builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOriginGroup(api, g =&gt; g
    ///         .WithRouting(FrontDoorOriginRouting.Failover)
    ///         .WithStampPriority(eastus, 1)
    ///         .WithStampPriority(westeu, 2));
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzureFrontDoorResource> WithOriginGroup<T>(
        this IResourceBuilder<AzureFrontDoorResource> builder,
        IResourceBuilder<T> resource,
        Action<AzureFrontDoorOriginGroupBuilder> configure) where T : IComputeResource, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configure);

        var settings = new AzureFrontDoorOriginGroupBuilder();
        configure(settings);

        return AddOriginAnnotation(builder, resource, settings);
    }

    private static IResourceBuilder<AzureFrontDoorResource> AddOriginAnnotation<T>(
        IResourceBuilder<AzureFrontDoorResource> builder,
        IResourceBuilder<T> resource,
        AzureFrontDoorOriginGroupBuilder settings) where T : IComputeResource, IResourceWithEndpoints
    {
        if (builder.Resource.Annotations
            .OfType<AzureFrontDoorOriginAnnotation>()
            .Any(a => a.Resource.Name == resource.Resource.Name))
        {
            throw new InvalidOperationException(
                $"Origin resource '{resource.Resource.Name}' has already been added to Azure Front Door resource '{builder.Resource.Name}'. " +
                "Each origin can only be added once.");
        }

        return builder.WithAnnotation(new AzureFrontDoorOriginAnnotation(resource.Resource, settings));
    }

    private static void AddOriginGroup(
        AzureResourceInfrastructure infrastructure,
        CdnProfile profile,
        AzureFrontDoorOriginAnnotation originAnnotation)
    {
        var originResource = originAnnotation.Resource;
        var settings = originAnnotation.Settings;
        var originBicepId = Infrastructure.NormalizeBicepIdentifier(originResource.Name);

        var endpointReference = GetOriginEndpoint(originResource);

        // Health probe settings come from the origin group configuration, falling back to the resource's own
        // probe annotations.
        var (probePath, probeProtocol) = GetProbeSettings(originResource, settings);

        // Endpoint
        var endpoint = new FrontDoorEndpoint($"{originBicepId}Endpoint")
        {
            Parent = profile,
            Location = new AzureLocation("Global")
        };
        infrastructure.Add(endpoint);

        var healthProbeSettings = new HealthProbeSettings
        {
            ProbeProtocol = probeProtocol,
            ProbePath = probePath
        };

        if (settings.ProbeInterval is { } probeInterval)
        {
            healthProbeSettings.ProbeIntervalInSeconds = (int)probeInterval.TotalSeconds;
        }

        // Origin group — LoadBalancingSettings is required by ARM even with a single origin.
        var originGroup = new FrontDoorOriginGroup($"{originBicepId}OriginGroup")
        {
            Parent = profile,
            HealthProbeSettings = healthProbeSettings,
            LoadBalancingSettings = new LoadBalancingSettings()
            {
                SampleSize = settings.SampleSize ?? 4,
                SuccessfulSamplesRequired = settings.SuccessfulSamplesRequired ?? 3,
                AdditionalLatencyInMilliseconds = settings.AdditionalLatencyMilliseconds ?? 50
            }
        };

        if (settings.SessionAffinityEnabled is { } sessionAffinityEnabled)
        {
            originGroup.SessionAffinityState = sessionAffinityEnabled ? EnabledState.Enabled : EnabledState.Disabled;
        }

        if (settings.TrafficRestorationTime is { } trafficRestorationTime)
        {
            originGroup.TrafficRestorationTimeInMinutes = (int)trafficRestorationTime.TotalMinutes;
        }

        infrastructure.Add(originGroup);

        // One origin per regional stamp of the backend. A backend bound to a single compute environment has
        // exactly one stamp, which produces the same infrastructure as before stamping existed.
        var stamps = GetOriginStamps(originResource);
        var origins = new List<FrontDoorOrigin>(stamps.Count);

        for (var i = 0; i < stamps.Count; i++)
        {
            var stamp = stamps[i];

            // Resolve the hostname through the stamp's own compute environment so each origin points at the
            // region that stamp is deployed to.
            var hostExpression = stamp.Environment.GetHostAddressExpression(endpointReference);

            // Suffix the bicep identifiers per stamp so the stamps do not collide. The single-stamp case
            // keeps the original unsuffixed names, which is what keeps generated bicep stable for
            // applications that are not stamped.
            var stampSuffix = stamp.QualifiesNames ? $"_{Infrastructure.NormalizeBicepIdentifier(stamp.Name)}" : string.Empty;

            var hostParam = hostExpression.AsProvisioningParameter(infrastructure, $"{originBicepId}{stampSuffix}_host");

            var origin = new FrontDoorOrigin($"{originBicepId}{stampSuffix}Origin")
            {
                Parent = originGroup,
                HostName = hostParam,
                OriginHostHeader = hostParam
            };

            // Priority and weight are only emitted when they carry information. Leaving them unset for the
            // default latency-based case keeps the generated bicep identical to what earlier versions
            // produced, so redeploying an existing single-region application does not churn infrastructure.
            if (GetOriginPriority(settings, stamp, i) is { } priority)
            {
                origin.Priority = priority;
            }

            if (settings.GetWeight(stamp.Environment) is { } weight)
            {
                origin.Weight = weight;
            }

            infrastructure.Add(origin);
            origins.Add(origin);
        }

        FrontDoorCustomDomain? customDomain = null;
        if (settings.CustomDomainHostName is { } customDomainHostName)
        {
            customDomain = new FrontDoorCustomDomain($"{originBicepId}CustomDomain")
            {
                Parent = profile,
                HostName = customDomainHostName
            };
            infrastructure.Add(customDomain);
        }

        // Route
        var route = new FrontDoorRoute($"{originBicepId}Route")
        {
            Parent = endpoint,
            OriginGroupId = originGroup.Id,
            PatternsToMatch = ["/*"],
            ForwardingProtocol = ForwardingProtocol.HttpsOnly,
            LinkToDefaultDomain = LinkToDefaultDomain.Enabled,
            HttpsRedirect = HttpsRedirect.Enabled
        };

        if (customDomain is not null)
        {
            route.CustomDomains.Add(new FrontDoorActivatedResourceInfo { Id = customDomain.Id });
        }

        // Route must wait for the origins to be created — without this, ARM deploys
        // the route in parallel and fails because the origin group has no origins yet.
        foreach (var origin in origins)
        {
            route.DependsOn.Add(origin);
        }

        infrastructure.Add(route);

        // Output the endpoint URL for this origin
        infrastructure.Add(new ProvisioningOutput($"{originBicepId}_endpointUrl", typeof(string))
        {
            Value = BicepFunction.Interpolate($"https://{endpoint.HostName}")
        });

        if (customDomain is not null)
        {
            // A custom domain does not serve traffic until its ownership is proven, so surface the token that
            // has to be published as a DNS TXT record.
            infrastructure.Add(new ProvisioningOutput($"{originBicepId}_customDomainValidationToken", typeof(string))
            {
                Value = customDomain.ValidationProperties.ValidationToken
            });
        }
    }

    /// <summary>
    /// Gets the regional stamps that become origins for the backend, or a single implicit stamp when the
    /// backend is not explicitly bound to a compute environment.
    /// </summary>
    private static IReadOnlyList<ComputeStamp> GetOriginStamps(IResourceWithEndpoints originResource)
    {
        var stamps = originResource.GetComputeStamps();
        if (stamps.Count > 0)
        {
            return stamps;
        }

        // Not explicitly bound: fall back to whichever environment the resource ended up deployed to.
        return [new ComputeStamp(GetEffectiveComputeEnvironment(originResource), originResource.Name, qualifiesNames: false)];
    }

    /// <summary>
    /// Gets the Front Door priority for a stamp, or <see langword="null"/> to leave the ARM default in place.
    /// </summary>
    private static int? GetOriginPriority(AzureFrontDoorOriginGroupBuilder settings, ComputeStamp stamp, int stampIndex)
    {
        if (settings.GetPriority(stamp.Environment) is { } explicitPriority)
        {
            return explicitPriority;
        }

        // Failover routing means "try the stamps in declaration order", which Front Door expresses as
        // ascending priorities. Azure rejects priorities above 5, so the tail of a long stamp list shares
        // the last usable priority rather than producing an invalid template.
        if (settings.Routing == FrontDoorOriginRouting.Failover)
        {
            return Math.Min(stampIndex + 1, 5);
        }

        return null;
    }

    private static IComputeEnvironmentResource GetEffectiveComputeEnvironment(IResource resource)
    {
        if (ComputeEnvironmentEndpointResolver.TryGetEffectiveComputeEnvironment(resource, out var computeEnvironment))
        {
            return computeEnvironment;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have a compute environment. " +
            "Ensure a compute environment (e.g., Azure Container Apps, Azure App Service) is configured in the application model.");
    }

    private static EndpointReference GetOriginEndpoint(IResourceWithEndpoints resource)
    {
        var externalHttpEndpoint = resource.GetEndpoints()
            .Where(e => e.EndpointAnnotation.UriScheme is "http" or "https")
            .FirstOrDefault(e => e.EndpointAnnotation.IsExternal);

        if (externalHttpEndpoint is not null)
        {
            return externalHttpEndpoint;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have an external HTTP or HTTPS endpoint. " +
            "Azure Front Door requires an origin to expose an external HTTP or HTTPS endpoint. " +
            "Call .WithExternalHttpEndpoints() on the resource before adding it as an origin.");
    }

    private static (string Path, HealthProbeProtocol Protocol) GetProbeSettings(
        IResourceWithEndpoints resource,
        AzureFrontDoorOriginGroupBuilder settings)
    {
        // An explicitly configured probe always wins.
        if (settings.ProbePath is { } configuredPath)
        {
            var configuredProtocol = settings.ProbeProtocol == FrontDoorHealthProbeProtocol.Http
                ? HealthProbeProtocol.Http
                : HealthProbeProtocol.Https;
            return (configuredPath, configuredProtocol);
        }

        // Use settings from EndpointProbeAnnotation if available (set by WithHttpProbe).
        // Prefer liveness probes, matching the pattern used by App Service.
        var probeAnnotation = resource.Annotations
            .OfType<EndpointProbeAnnotation>()
            .OrderBy(p => p.Type == ProbeType.Liveness ? 0 : 1)
            .FirstOrDefault();

        if (probeAnnotation is not null)
        {
            var protocol = probeAnnotation.EndpointReference.Scheme == "http"
                ? HealthProbeProtocol.Http
                : HealthProbeProtocol.Https;
            return (probeAnnotation.Path, protocol);
        }

        return ("/", HealthProbeProtocol.Https);
    }
}
