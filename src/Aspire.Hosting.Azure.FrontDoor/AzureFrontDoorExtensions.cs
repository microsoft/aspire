// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource.GetHostAddressExpression is experimental

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
    /// <remarks>
    /// <para>
    /// Azure Front Door is a global, scalable entry point that uses the Microsoft global edge network to create
    /// fast, secure, and widely scalable web applications. Use <see cref="WithOrigin"/> to add origins
    /// (backends) to the Front Door resource. Each origin gets its own Front Door endpoint, origin group,
    /// and route, so each backend app is independently routable via its own <c>*.azurefd.net</c> hostname.
    /// </para>
    /// <para>
    /// For advanced scenarios (shared origin groups, path-based routing, custom routes), use
    /// <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}"/> to customize the
    /// generated infrastructure directly.
    /// </para>
    /// <example>
    /// Add an Azure Front Door resource with origins:
    /// <code>
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api");
    /// var web = builder.AddProject&lt;Projects.Web&gt;("web");
    /// var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOrigin(api)
    ///     .WithOrigin(web);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds an Azure Front Door resource")]
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

            // Create the CDN profile (Front Door)
            var profile = new CdnProfile(infrastructure.AspireResource.GetBicepIdentifier())
            {
                SkuName = CdnSkuName.StandardAzureFrontDoor,
                Name = BicepFunction.Take(BicepFunction.Interpolate($"{infrastructure.AspireResource.Name}-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 90),
                Location = new AzureLocation("Global"),
                Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
            };
            infrastructure.Add(profile);

            // Create a separate endpoint → origin group → origin → route per WithOrigin call.
            // This gives each backend app its own Front Door hostname.
            var originAnnotations = azureResource.Annotations.OfType<AzureFrontDoorOriginAnnotation>().ToList();
            for (var i = 0; i < originAnnotations.Count; i++)
            {
                var originAnnotation = originAnnotations[i];
                var originResource = originAnnotation.Resource;
                var originName = originResource.Name.ToLowerInvariant();

                var endpointReference = GetOriginEndpoint(originResource);

                // Resolve the hostname via the origin resource's compute environment
                var computeEnv = GetEffectiveComputeEnvironment(originResource);
                var hostExpression = computeEnv.GetHostAddressExpression(endpointReference);
                var hostParam = hostExpression.AsProvisioningParameter(infrastructure, $"{originName}_host");

                // Endpoint
                var endpoint = new FrontDoorEndpoint($"{originName}Endpoint")
                {
                    Parent = profile,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 46),
                    Location = new AzureLocation("Global"),
                    EnabledState = EnabledState.Enabled
                };
                infrastructure.Add(endpoint);

                // Origin group
                var originGroup = new FrontDoorOriginGroup($"{originName}OriginGroup")
                {
                    Parent = profile,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-og-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 90),
                    HealthProbeSettings = new HealthProbeSettings
                    {
                        ProbePath = "/",
                        ProbeRequestType = HealthProbeRequestType.Head,
                        ProbeProtocol = HealthProbeProtocol.Https,
                        ProbeIntervalInSeconds = 240
                    },
                    LoadBalancingSettings = new LoadBalancingSettings
                    {
                        SampleSize = 4,
                        SuccessfulSamplesRequired = 3,
                        AdditionalLatencyInMilliseconds = 50
                    },
                    SessionAffinityState = EnabledState.Disabled
                };
                infrastructure.Add(originGroup);

                // Origin
                var origin = new FrontDoorOrigin($"{originName}Origin")
                {
                    Parent = originGroup,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-origin-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 90),
                    HostName = hostParam,
                    OriginHostHeader = hostParam,
                    HttpPort = 80,
                    HttpsPort = 443,
                    Priority = 1,
                    Weight = 1000,
                    EnabledState = EnabledState.Enabled,
                    EnforceCertificateNameCheck = true
                };
                infrastructure.Add(origin);

                // Route
                var route = new FrontDoorRoute($"{originName}Route")
                {
                    Parent = endpoint,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-route-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 90),
                    OriginGroupId = originGroup.Id,
                    PatternsToMatch = ["/*"],
                    ForwardingProtocol = ForwardingProtocol.HttpsOnly,
                    LinkToDefaultDomain = LinkToDefaultDomain.Enabled,
                    HttpsRedirect = HttpsRedirect.Enabled,
                    EnabledState = EnabledState.Enabled,
                    OriginPath = "/",
                    SupportedProtocols = [FrontDoorEndpointProtocol.Http, FrontDoorEndpointProtocol.Https],
                    CacheConfiguration = new FrontDoorRouteCacheConfiguration
                    {
                        QueryStringCachingBehavior = FrontDoorQueryStringCachingBehavior.IgnoreQueryString,
                        CompressionSettings = new RouteCacheCompressionSettings
                        {
                            IsCompressionEnabled = true,
                            ContentTypesToCompress =
                            [
                                "text/plain",
                                "text/html",
                                "text/css",
                                "application/javascript",
                                "application/json",
                                "image/svg+xml"
                            ]
                        }
                    }
                };
                // Route must wait for origin to be created — without this, ARM deploys
                // the route in parallel and fails because the origin group has no origins yet.
                route.DependsOn.Add(origin);
                infrastructure.Add(route);

                // Output the endpoint URL for this origin
                infrastructure.Add(new ProvisioningOutput($"{originName}_endpointUrl", typeof(string))
                {
                    Value = BicepFunction.Interpolate($"https://{endpoint.HostName}")
                });
            }
        };

        var resource = new AzureFrontDoorResource(name, configureInfrastructure);

        return builder.ExecutionContext.IsPublishMode
            ? builder.AddResource(resource)
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
    /// <remarks>
    /// <example>
    /// Add multiple origins (each gets its own Front Door endpoint):
    /// <code>
    /// var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    ///     .WithOrigin(api)
    ///     .WithOrigin(web);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds an origin (backend) to the Azure Front Door resource")]
    public static IResourceBuilder<AzureFrontDoorResource> WithOrigin<T>(
        this IResourceBuilder<AzureFrontDoorResource> builder,
        IResourceBuilder<T> resource) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return builder.WithAnnotation(new AzureFrontDoorOriginAnnotation(resource.Resource));
    }

    private static IComputeEnvironmentResource GetEffectiveComputeEnvironment(IResource resource)
    {
        if (resource.GetComputeEnvironment() is { } computeEnvironment)
        {
            return computeEnvironment;
        }

        if (resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment is { } deploymentComputeEnvironment)
        {
            return deploymentComputeEnvironment;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have a compute environment. " +
            "Ensure a compute environment (e.g., Azure Container Apps, Azure App Service) is configured in the application model.");
    }

    private static EndpointReference GetOriginEndpoint(IResourceWithEndpoints resource)
    {
        var endpoints = resource.GetEndpoints().ToArray();

        if (endpoints.FirstOrDefault(endpoint => endpoint.EndpointAnnotation.IsExternal) is { } externalEndpoint)
        {
            return externalEndpoint;
        }

        if (endpoints.FirstOrDefault() is { } endpoint)
        {
            return endpoint;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have any endpoints. " +
            "Azure Front Door requires a resource to expose at least one endpoint before it can be added as an origin.");
    }
}
