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
    // Azure resource name length limits (from Azure portal).
    private const int ProfileNameMaxLength = 90;
    private const int EndpointNameMaxLength = 46;
    private const int OriginGroupNameMaxLength = 90;
    private const int OriginNameMaxLength = 90;
    private const int RouteNameMaxLength = 90;

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
                Name = BicepFunction.Take(BicepFunction.Interpolate($"{infrastructure.AspireResource.Name}-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), ProfileNameMaxLength),
                Location = new AzureLocation("Global"),
                Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
            };
            infrastructure.Add(profile);

            // Create a separate endpoint → origin group → origin → route per WithOrigin call.
            // This gives each backend app its own Front Door hostname.
            var originAnnotations = azureResource.Annotations.OfType<AzureFrontDoorOriginAnnotation>().ToList();
            foreach (var originAnnotation in originAnnotations)
            {
                var originResource = originAnnotation.Resource;
                var originBicepId = Infrastructure.NormalizeBicepIdentifier(originResource.Name);
                var originName = originResource.Name.ToLowerInvariant();

                var endpointReference = GetOriginEndpoint(originResource);

                // Use health probe settings from the resource's probe annotations if available
                var (probePath, probeProtocol) = GetProbeSettings(originResource);

                // Resolve the hostname via the origin resource's compute environment
                var computeEnv = GetEffectiveComputeEnvironment(originResource);
                var hostExpression = computeEnv.GetHostAddressExpression(endpointReference);
                var hostParam = hostExpression.AsProvisioningParameter(infrastructure, $"{originBicepId}_host");

                // Endpoint
                var endpoint = new FrontDoorEndpoint($"{originBicepId}Endpoint")
                {
                    Parent = profile,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), EndpointNameMaxLength),
                    Location = new AzureLocation("Global")
                };
                infrastructure.Add(endpoint);

                // Origin group — LoadBalancingSettings is required by ARM even with a single origin.
                var originGroup = new FrontDoorOriginGroup($"{originBicepId}OriginGroup")
                {
                    Parent = profile,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-og-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), OriginGroupNameMaxLength),
                    HealthProbeSettings = new HealthProbeSettings
                    {
                        ProbeProtocol = probeProtocol,
                        ProbePath = probePath
                    },
                    LoadBalancingSettings = new LoadBalancingSettings()
                    {
                        SampleSize = 4,
                        SuccessfulSamplesRequired = 3,
                        AdditionalLatencyInMilliseconds = 50
                    }
                };
                infrastructure.Add(originGroup);

                // Origin
                var origin = new FrontDoorOrigin($"{originBicepId}Origin")
                {
                    Parent = originGroup,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-origin-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), OriginNameMaxLength),
                    HostName = hostParam,
                    OriginHostHeader = hostParam
                };
                infrastructure.Add(origin);

                // Route
                var route = new FrontDoorRoute($"{originBicepId}Route")
                {
                    Parent = endpoint,
                    Name = BicepFunction.Take(BicepFunction.Interpolate($"{originName}-route-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), RouteNameMaxLength),
                    OriginGroupId = originGroup.Id,
                    PatternsToMatch = ["/*"],
                    ForwardingProtocol = ForwardingProtocol.HttpsOnly,
                    LinkToDefaultDomain = LinkToDefaultDomain.Enabled,
                    HttpsRedirect = HttpsRedirect.Enabled
                };
                // Route must wait for origin to be created — without this, ARM deploys
                // the route in parallel and fails because the origin group has no origins yet.
                route.DependsOn.Add(origin);
                infrastructure.Add(route);

                // Output the endpoint URL for this origin
                infrastructure.Add(new ProvisioningOutput($"{originBicepId}_endpointUrl", typeof(string))
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
    /// <code lang="C#">
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

        if (builder.Resource.Annotations
            .OfType<AzureFrontDoorOriginAnnotation>()
            .Any(a => a.Resource.Name == resource.Resource.Name))
        {
            throw new InvalidOperationException(
                $"Origin resource '{resource.Resource.Name}' has already been added to Azure Front Door resource '{builder.Resource.Name}'. " +
                "Each origin can only be added once.");
        }

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

    private static (string Path, HealthProbeProtocol Protocol) GetProbeSettings(IResourceWithEndpoints resource)
    {
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
