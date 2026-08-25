// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning is experimental.
#pragma warning disable ASPIREAZURE003 // Azure provisioning APIs are experimental.
#pragma warning disable ASPIRECOMPUTE002 // Compute environment endpoint projection is experimental.

using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Aspire.Hosting.Foundry;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure API Management resources to the application model.
/// </summary>
public static class AzureApiManagementExtensions
{
    private static readonly char[] s_invalidApiIdentifierCharacters = ['*', '#', '&', '+', ':', '<', '>', '?'];

    /// <summary>
    /// Adds an Azure API Management service.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="options">The API Management service options.</param>
    /// <returns>A builder for the Azure API Management resource.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when required option values are empty or capacity is invalid for the selected SKU.</exception>
    /// <example>
    /// <code>
    /// var apim = builder.AddAzureApiManagement("apim", new()
    /// {
    ///     PublisherEmail = "api-owners@example.com",
    ///     Sku = AzureApiManagementSku.StandardV2,
    /// });
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementResource> AddAzureApiManagement(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        AzureApiManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.PublisherEmail);
        ArgumentException.ThrowIfNullOrEmpty(options.PublisherName);
        ValidateCapacity(options.Sku, options.Capacity);

        var resource = new AzureApiManagementResource(name, options, ConfigureInfrastructure);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        builder.AddAzureProvisioning();

        return builder.AddResource(resource)
            .WithIconName("GlobeShield");
    }

    /// <summary>
    /// Adds an API that routes requests to an Aspire compute resource.
    /// </summary>
    /// <typeparam name="T">The backend compute resource type.</typeparam>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical API name.</param>
    /// <param name="target">
    /// The backend compute resource. It must expose an HTTP or HTTPS endpoint. Internal endpoints require the
    /// API Management service to have virtual network connectivity to the target.
    /// </param>
    /// <param name="path">The public path beneath the API Management gateway hostname.</param>
    /// <param name="displayName">The API display name. The resource name is used when omitted.</param>
    /// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key. The default is <see langword="true"/>.</param>
    /// <param name="apiName">The physical API identifier in API Management. The Aspire resource name is used when omitted.</param>
    /// <returns>A builder for the API resource.</returns>
    /// <remarks>
    /// The generated API contains a wildcard operation for all HTTP methods and paths. The generated policy routes
    /// requests through an API Management backend entity whose URL is resolved from the target deployment environment.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string is empty.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementApiResource> AddApi<T>(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<T> target,
        string path,
        string? displayName = null,
        bool subscriptionRequired = true,
        string? apiName = null)
        where T : IComputeResource, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ValidateApiIdentifier(apiName ?? name, nameof(apiName));

        var normalizedPath = path.Trim('/');
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ValidateApiUniqueness(builder.Resource, apiName ?? name, normalizedPath);

        var resource = new AzureApiManagementApiResource(
            name,
            apiName ?? name,
            normalizedPath,
            displayName ?? name,
            subscriptionRequired,
            target.Resource,
            builder.Resource);

        builder.Resource.Apis.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("DocumentTableArrowRight");
    }

    /// <summary>
    /// Adds an OpenAI-compatible API backed by an API Management load-balancing pool.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical API name.</param>
    /// <param name="path">The public path beneath the API Management gateway hostname.</param>
    /// <param name="displayName">The API display name. The resource name is used when omitted.</param>
    /// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key. The default is <see langword="true"/>.</param>
    /// <param name="apiName">The physical API identifier in API Management. The Aspire resource name is used when omitted.</param>
    /// <returns>A builder for the API resource.</returns>
    /// <remarks>
    /// Add one or more pool members with <see cref="WithAzureOpenAIBackend"/> or
    /// <see cref="WithFoundryBackend"/>. Requests beneath <paramref name="path"/> are appended to each
    /// selected deployment URL. For example, <c>/chat/completions</c> is forwarded to
    /// <c>/openai/deployments/{deployment}/chat/completions</c>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementApiResource> AddOpenAIApi(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string path,
        string? displayName = null,
        bool subscriptionRequired = true,
        string? apiName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ValidateApiIdentifier(apiName ?? name, nameof(apiName));

        if (builder.Resource.Options.Sku == AzureApiManagementSku.Consumption)
        {
            throw new InvalidOperationException(
                "OpenAI backend pools require API Management backend circuit breakers, which are not supported by the Consumption SKU.");
        }

        var normalizedPath = path.Trim('/');
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ValidateApiUniqueness(builder.Resource, apiName ?? name, normalizedPath);

        var resource = new AzureApiManagementApiResource(
            name,
            apiName ?? name,
            normalizedPath,
            displayName ?? name,
            subscriptionRequired,
            builder.Resource);

        builder.Resource.Apis.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        var apiBuilder = resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("DocumentTableArrowRight");

        apiBuilder.AddOperation(
            CreateBoundedIdentifier($"{name}-chat-completions", 64),
            method: "POST",
            urlTemplate: "/chat/completions",
            displayName: "Create chat completion",
            operationName: "chat-completions");

        return apiBuilder;
    }

    /// <summary>
    /// Adds an Azure OpenAI deployment to an OpenAI-compatible API backend pool.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="deployment">The Azure OpenAI deployment.</param>
    /// <param name="priority">The failover priority. Lower values are selected first.</param>
    /// <param name="weight">The relative traffic weight among healthy members at the same priority.</param>
    /// <returns>The API resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementApiResource> WithAzureOpenAIBackend(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<AzureOpenAIDeploymentResource> deployment,
        int priority = 1,
        int weight = 1)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        return WithOpenAIBackend(
            builder,
            deployment.Resource,
            deployment.Resource.Parent,
            ReferenceExpression.Create($"{deployment.Resource.Parent.Endpoint}"),
            deployment.Resource.DeploymentName,
            priority,
            weight);
    }

    /// <summary>
    /// Adds a Microsoft Foundry OpenAI deployment to an OpenAI-compatible API backend pool.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="deployment">The Microsoft Foundry deployment. Its format must be <c>OpenAI</c>.</param>
    /// <param name="priority">The failover priority. Lower values are selected first.</param>
    /// <param name="weight">The relative traffic weight among healthy members at the same priority.</param>
    /// <returns>The API resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementApiResource> WithFoundryBackend(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<FoundryDeploymentResource> deployment,
        int priority = 1,
        int weight = 1)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        if (!string.Equals(deployment.Resource.Format, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
               $"Foundry deployment '{deployment.Resource.Name}' uses format '{deployment.Resource.Format}'. " +
               "Only OpenAI-format deployments can be added to an OpenAI-compatible API.");
        }

        return WithOpenAIBackend(
            builder,
            deployment.Resource,
            deployment.Resource.Parent,
            ReferenceExpression.Create($"{deployment.Resource.Parent.Endpoint}"),
            deployment.Resource.DeploymentName,
            priority,
            weight);
    }

    /// <summary>
    /// Adds an explicitly modeled operation to an API.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical operation name.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="urlTemplate">The URL template relative to the API path.</param>
    /// <param name="displayName">The operation display name. The resource name is used when omitted.</param>
    /// <param name="operationName">The physical operation identifier in API Management. The Aspire resource name is used when omitted.</param>
    /// <returns>A builder for the operation resource.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string is empty.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementOperationResource> AddOperation(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        [ResourceName] string name,
        string method,
        string urlTemplate,
        string? displayName = null,
        string? operationName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(urlTemplate);
        var physicalOperationName = operationName ?? name;
        ValidateOperationIdentifier(physicalOperationName, nameof(operationName));

        if (string.Equals(physicalOperationName, "proxy", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The API Management operation identifier 'proxy' is reserved for the generated catch-all operation.",
                nameof(operationName));
        }

        if (builder.Resource.Operations.Any(
            operation => string.Equals(operation.OperationName, physicalOperationName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"API '{builder.Resource.Name}' already contains an operation with the physical identifier '{physicalOperationName}'.");
        }

        var resource = new AzureApiManagementOperationResource(
            name,
            physicalOperationName,
            method.ToUpperInvariant(),
            urlTemplate,
            displayName ?? name,
            builder.Resource);

        builder.Resource.Operations.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("DocumentTableArrowRight");
    }

    /// <summary>
    /// Appends an XML policy statement to the service-level inbound policy section.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="policyXml">One or more well-formed APIM policy elements without a <c>policies</c> envelope.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is empty or is not a valid XML fragment.</exception>
    [AspireExport("withApiManagementServiceInboundPolicy", MethodName = "withInboundPolicy")]
    public static IResourceBuilder<AzureApiManagementResource> WithInboundPolicy(
        this IResourceBuilder<AzureApiManagementResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyFragment(policyXml);
        EnsurePolicyFragmentCanBeAdded(builder.Resource.PolicyXml, "service");
        builder.Resource.AddInboundPolicyStatement(policyXml);
        return builder;
    }

    /// <summary>
    /// Replaces the complete service-level policy document.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="policyXml">A complete APIM <c>policies</c> XML document.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// APIM policy updates replace the complete policy document at a scope. To prevent silently dropping policy fragments,
    /// this method cannot be combined with <see cref="WithInboundPolicy(IResourceBuilder{AzureApiManagementResource}, string)"/>
    /// at the same scope.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is not a complete policy document.</exception>
    [AspireExport("withApiManagementServicePolicy", MethodName = "withPolicy")]
    public static IResourceBuilder<AzureApiManagementResource> WithPolicy(
        this IResourceBuilder<AzureApiManagementResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyDocument(policyXml);
        EnsureCompletePolicyCanBeSet(builder.Resource.InboundPolicyStatements, "service");
        builder.Resource.PolicyXml = policyXml;
        return builder;
    }

    /// <summary>
    /// Appends an XML policy statement to the API-level inbound policy section.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="policyXml">One or more well-formed APIM policy elements without a <c>policies</c> envelope.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is empty or is not a valid XML fragment.</exception>
    [AspireExport("withApiManagementApiInboundPolicy", MethodName = "withInboundPolicy")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithInboundPolicy(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyFragment(policyXml);
        EnsurePolicyFragmentCanBeAdded(builder.Resource.PolicyXml, "API");
        builder.Resource.AddInboundPolicyStatement(policyXml);
        return builder;
    }

    /// <summary>
    /// Replaces the complete API-level policy document.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="policyXml">A complete APIM <c>policies</c> XML document.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Replacing the API policy also replaces the generated backend-routing policy. The supplied document must
    /// configure backend routing when the API should continue forwarding requests.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is not a complete policy document.</exception>
    [AspireExport("withApiManagementApiPolicy", MethodName = "withPolicy")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithPolicy(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyDocument(policyXml);
        EnsureCompletePolicyCanBeSet(builder.Resource.InboundPolicyStatements, "API");
        builder.Resource.PolicyXml = policyXml;
        return builder;
    }

    /// <summary>
    /// Appends an XML policy statement to the operation-level inbound policy section.
    /// </summary>
    /// <param name="builder">The operation resource builder.</param>
    /// <param name="policyXml">One or more well-formed APIM policy elements without a <c>policies</c> envelope.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is empty or is not a valid XML fragment.</exception>
    [AspireExport("withApiManagementOperationInboundPolicy", MethodName = "withInboundPolicy")]
    public static IResourceBuilder<AzureApiManagementOperationResource> WithInboundPolicy(
        this IResourceBuilder<AzureApiManagementOperationResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyFragment(policyXml);
        EnsurePolicyFragmentCanBeAdded(builder.Resource.PolicyXml, "operation");
        builder.Resource.AddInboundPolicyStatement(policyXml);
        return builder;
    }

    /// <summary>
    /// Replaces the complete operation-level policy document.
    /// </summary>
    /// <param name="builder">The operation resource builder.</param>
    /// <param name="policyXml">A complete APIM <c>policies</c> XML document.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is not a complete policy document.</exception>
    [AspireExport("withApiManagementOperationPolicy", MethodName = "withPolicy")]
    public static IResourceBuilder<AzureApiManagementOperationResource> WithPolicy(
        this IResourceBuilder<AzureApiManagementOperationResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyDocument(policyXml);
        EnsureCompletePolicyCanBeSet(builder.Resource.InboundPolicyStatements, "operation");
        builder.Resource.PolicyXml = policyXml;
        return builder;
    }

    /// <summary>
    /// Injects a classic Developer or Premium API Management service into a virtual network subnet.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="subnet">The subnet resource. Classic APIM injection requires an undelegated subnet with an NSG.</param>
    /// <param name="mode">Whether the APIM endpoints are externally or internally accessible.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// This API models classic VNet injection only. Standard v2 outbound integration and Premium v2 injection have
    /// different subnet delegations and lifecycle constraints and can be configured with <c>ConfigureInfrastructure</c>.
    /// See <see href="https://learn.microsoft.com/azure/api-management/virtual-network-concepts">APIM virtual networking</see>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the selected SKU does not support classic VNet injection or the subnet is delegated.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementResource> WithClassicVirtualNetwork(
        this IResourceBuilder<AzureApiManagementResource> builder,
        IResourceBuilder<AzureSubnetResource> subnet,
        AzureApiManagementVirtualNetworkMode mode)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subnet);

        if (builder.Resource.Options.Sku is not (AzureApiManagementSku.Developer or AzureApiManagementSku.Premium))
        {
            throw new InvalidOperationException(
                $"Classic virtual network injection is supported only by the Developer and Premium SKUs, not '{builder.Resource.Options.Sku}'.");
        }

        if (subnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>().Any())
        {
            throw new InvalidOperationException(
                $"Subnet '{subnet.Resource.Name}' is delegated to another Azure service. Classic API Management injection requires an undelegated subnet.");
        }

        if (builder.Resource.VirtualNetworkConfiguration is { } existing &&
            !ReferenceEquals(existing.Subnet, subnet.Resource))
        {
            throw new InvalidOperationException(
                $"API Management resource '{builder.Resource.Name}' is already associated with subnet '{existing.Subnet.Name}'.");
        }

        builder.Resource.VirtualNetworkConfiguration = new(subnet.Resource, mode);
        return builder.WithRelationship(subnet.Resource, "Virtual network");
    }

    private static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var azureResource = (AzureApiManagementResource)infrastructure.AspireResource;

        if (azureResource.IsExisting())
        {
            throw new InvalidOperationException(
                $"API Management resource '{azureResource.Name}' cannot be published as an existing resource. " +
                "Existing APIM services are not yet supported because child APIs and policies would mutate the service.");
        }

        var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

        if (hasPrivateEndpoint)
        {
            if (azureResource.Options.Sku is AzureApiManagementSku.Consumption or AzureApiManagementSku.BasicV2)
            {
                throw new InvalidOperationException(
                    $"API Management SKU '{azureResource.Options.Sku}' does not support private endpoints.");
            }

            throw new InvalidOperationException(
                $"Private endpoints are not yet supported for API Management resource '{azureResource.Name}'. " +
                "APIM requires public network access during initial provisioning and a separate post-deployment update after the private endpoint is created.");
        }

        var service = new ApiManagementServiceProvisioningResource(
            infrastructure.AspireResource.GetBicepIdentifier())
        {
            PublisherEmail = azureResource.Options.PublisherEmail,
            PublisherName = azureResource.Options.PublisherName,
            SkuName = GetProvisioningSku(azureResource.Options.Sku),
            SkuCapacity = azureResource.Options.Capacity,
            Identity = new ManagedServiceIdentity
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned,
            },
            PublicNetworkAccess = "Enabled",
            VirtualNetworkType = "None",
            Tags =
            {
                { "aspire-resource-name", azureResource.Name },
            },
        };

        if (azureResource.VirtualNetworkConfiguration is { } virtualNetwork)
        {
            service.VirtualNetworkType = virtualNetwork.Mode switch
            {
                AzureApiManagementVirtualNetworkMode.External => "External",
                AzureApiManagementVirtualNetworkMode.Internal => "Internal",
                _ => throw new UnreachableException(),
            };
            service.SubnetResourceId = virtualNetwork.Subnet.Id.AsProvisioningParameter(infrastructure);
        }

        infrastructure.Add(service);

        AddServicePolicy(infrastructure, azureResource, service);

        var roleAssignedAccounts = new HashSet<AzureProvisioningResource>();
        foreach (var apiResource in azureResource.Apis)
        {
            AddApi(infrastructure, apiResource, service, roleAssignedAccounts);
        }

        infrastructure.Add(new ProvisioningOutput("gatewayUrl", typeof(string))
        {
            Value = service.GatewayUri,
        });
        infrastructure.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = service.Id,
        });
        infrastructure.Add(new ProvisioningOutput("principalId", typeof(string))
        {
            Value = service.Identity.PrincipalId,
        });
    }

    private static void AddServicePolicy(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service)
    {
        var policyXml = azureResource.PolicyXml ??
            CreatePolicyDocument(azureResource.InboundPolicyStatements, inheritParentPolicy: false);

        if (policyXml is null)
        {
            return;
        }

        var policy = new ApiManagementServicePolicyProvisioningResource(
            $"{service.BicepIdentifier}Policy")
        {
            Parent = service,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml!,
        };
        infrastructure.Add(policy);
    }

    private static void AddApi(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementApiResource apiResource,
        ApiManagementServiceProvisioningResource service,
        HashSet<AzureProvisioningResource> roleAssignedAccounts)
    {
        var apiIdentifier = Infrastructure.NormalizeBicepIdentifier(apiResource.Name);
        var (backendIdentifier, backend, authenticateWithManagedIdentity) = apiResource.Target is not null
            ? AddComputeBackend(infrastructure, apiResource, service, apiIdentifier)
            : AddOpenAIBackendPool(infrastructure, apiResource, service, apiIdentifier, roleAssignedAccounts);

        var api = new ApiManagementApiProvisioningResource(
            apiIdentifier)
        {
            Parent = service,
            Name = apiResource.ApiName,
            DisplayName = apiResource.DisplayName,
            Path = apiResource.Path,
            SubscriptionRequired = apiResource.SubscriptionRequired,
            Type = "http",
            Protocols =
            {
                "https",
            },
        };
        infrastructure.Add(api);

        var catchAllOperation = new ApiManagementOperationProvisioningResource(
            $"{apiIdentifier}Proxy")
        {
            Parent = api,
            Name = "proxy",
            DisplayName = "Proxy",
            Method = "*",
            UriTemplate = "/*",
        };
        infrastructure.Add(catchAllOperation);

        foreach (var operationResource in apiResource.Operations)
        {
            AddOperation(infrastructure, operationResource, api);
        }

        var policyXml = apiResource.PolicyXml ??
            CreatePolicyDocument(
                apiResource.InboundPolicyStatements,
                backendIdentifier,
                authenticateWithManagedIdentity: authenticateWithManagedIdentity);

        var policy = new ApiManagementApiPolicyProvisioningResource(
            $"{apiIdentifier}Policy")
        {
            Parent = api,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml!,
        };
        policy.DependsOn.Add(backend);
        infrastructure.Add(policy);
    }

    private static (string Identifier, ApiManagementBackendProvisioningResource Backend, bool AuthenticateWithManagedIdentity)
        AddComputeBackend(
            AzureResourceInfrastructure infrastructure,
            AzureApiManagementApiResource apiResource,
            ApiManagementServiceProvisioningResource service,
            string apiIdentifier)
    {
        Debug.Assert(apiResource.Target is not null);

        if (apiResource.Target is not IResourceWithEndpoints endpointResource)
        {
            throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not expose endpoints and cannot be used as an API Management backend.");
        }

        var httpEndpoints = endpointResource.GetEndpoints()
            .Where(e => e.EndpointAnnotation.UriScheme is "http" or "https")
            .ToArray();
        var endpoint = httpEndpoints.FirstOrDefault(e => e.EndpointAnnotation.IsExternal)
            ?? httpEndpoints.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not have an HTTP or HTTPS endpoint.");

        if (!endpoint.EndpointAnnotation.IsExternal && apiResource.Parent.VirtualNetworkConfiguration is null)
        {
            throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' exposes only internal HTTP endpoints. " +
                $"Configure virtual network connectivity for API Management resource '{apiResource.Parent.Name}' before adding it as a backend.");
        }

        if (!ComputeEnvironmentEndpointResolver.TryGetEffectiveComputeEnvironment(apiResource.Target, out var computeEnvironment))
        {
            throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not have a compute environment. " +
                "Configure an Azure deployment environment before adding it to API Management.");
        }

        if (computeEnvironment is AzureContainerAppEnvironmentResource
            {
                InternalLoadBalancerVirtualNetwork: { } backendVirtualNetwork,
            })
        {
            var apiManagementNetwork = apiResource.Parent.VirtualNetworkConfiguration;
            if (apiManagementNetwork is null ||
                !ReferenceEquals(apiManagementNetwork.Subnet.Parent, backendVirtualNetwork))
            {
                throw new InvalidOperationException(
                    $"Resource '{apiResource.Target.Name}' is deployed to an internal Container Apps environment. " +
                    $"API Management resource '{apiResource.Parent.Name}' must be injected into the same virtual network.");
            }
        }

        var backendIdentifier = $"{apiIdentifier}Backend";
        var backendName = CreateBoundedIdentifier(backendIdentifier, 80);
        var endpointExpression = computeEnvironment.GetEndpointPropertyExpression(
            endpoint.Property(EndpointProperty.Url));
        var backendUrl = endpointExpression.AsProvisioningParameter(infrastructure, $"{apiIdentifier}_url");

        var backend = new ApiManagementBackendProvisioningResource(backendIdentifier)
        {
            Parent = service,
            Name = backendName,
            Protocol = "http",
            Uri = backendUrl,
            Title = apiResource.DisplayName,
            Type = "Single",
            ValidateCertificateChain = true,
            ValidateCertificateName = true,
        };
        infrastructure.Add(backend);

        return (backendName, backend, false);
    }

    private static (string Identifier, ApiManagementBackendProvisioningResource Backend, bool AuthenticateWithManagedIdentity)
        AddOpenAIBackendPool(
            AzureResourceInfrastructure infrastructure,
            AzureApiManagementApiResource apiResource,
            ApiManagementServiceProvisioningResource service,
            string apiIdentifier,
            HashSet<AzureProvisioningResource> roleAssignedAccounts)
    {
        if (apiResource.OpenAIBackends.Count == 0)
        {
            throw new InvalidOperationException(
                $"OpenAI API '{apiResource.Name}' does not have any backend deployments. " +
                $"Call {nameof(WithAzureOpenAIBackend)} or {nameof(WithFoundryBackend)} at least once.");
        }

        var poolIdentifier = $"{apiIdentifier}Pool";
        var poolName = CreateBoundedIdentifier(poolIdentifier, 80);
        var pool = new ApiManagementBackendProvisioningResource(poolIdentifier)
        {
            Parent = service,
            Name = poolName,
            Title = apiResource.DisplayName,
            Type = "Pool",
            Pool = new ApiManagementBackendPoolProvisioningModel(),
        };

        foreach (var backendResource in apiResource.OpenAIBackends)
        {
            var backendIdentifier = Infrastructure.NormalizeBicepIdentifier(
                $"{apiIdentifier}_{backendResource.Name}_Backend");
            var backendName = CreateBoundedIdentifier(backendIdentifier, 80);
            var backendUrl = ReferenceExpression.Create(
                $"{backendResource.Endpoint}openai/deployments/{backendResource.DeploymentName}");
            var backend = new ApiManagementBackendProvisioningResource(backendIdentifier)
            {
                Parent = service,
                Name = backendName,
                Protocol = "http",
                Uri = backendUrl.AsProvisioningParameter(infrastructure, $"{backendIdentifier}_url"),
                Title = backendResource.Name,
                Type = "Single",
                ValidateCertificateChain = true,
                ValidateCertificateName = true,
                CircuitBreaker = CreateOpenAICircuitBreaker(),
            };
            infrastructure.Add(backend);

            pool.Pool.Services.Add(new ApiManagementBackendPoolMemberProvisioningModel
            {
                Id = backend.Id,
                Priority = backendResource.Priority,
                Weight = backendResource.Weight,
            });
            pool.DependsOn.Add(backend);
        }

        infrastructure.Add(pool);

        foreach (var accountResource in apiResource.OpenAIBackends
            .Select(backend => backend.Account)
            .Where(roleAssignedAccounts.Add))
        {
            var account = (CognitiveServicesAccount)accountResource.AddAsExistingResource(infrastructure);
            var roleAssignment = account.CreateRoleAssignment(
                accountResource is AzureOpenAIResource
                    ? CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser
                    : CognitiveServicesBuiltInRole.CognitiveServicesUser,
                RoleManagementPrincipalType.ServicePrincipal,
                service.Identity.PrincipalId);
            roleAssignment.Name = BicepFunction.CreateGuid(account.Id, service.Id, roleAssignment.RoleDefinitionId);
            infrastructure.Add(roleAssignment);
        }

        return (poolName, pool, true);
    }

    private static ApiManagementCircuitBreakerProvisioningModel CreateOpenAICircuitBreaker() =>
        new()
        {
            Rules =
            {
                new ApiManagementCircuitBreakerRuleProvisioningModel
                {
                    Name = "openAIThrottling",
                    FailureCondition = new ApiManagementCircuitBreakerFailureConditionProvisioningModel
                    {
                        Count = 1,
                        Interval = "PT10S",
                        StatusCodeRanges =
                        {
                            new ApiManagementStatusCodeRangeProvisioningModel
                            {
                                Minimum = 429,
                                Maximum = 429,
                            },
                        },
                    },
                    TripDuration = "PT10S",
                    AcceptRetryAfter = true,
                },
            },
        };

    private static void AddOperation(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementOperationResource operationResource,
        ApiManagementApiProvisioningResource api)
    {
        var operationIdentifier = Infrastructure.NormalizeBicepIdentifier(operationResource.Name);
        var operation = new ApiManagementOperationProvisioningResource(
            operationIdentifier)
        {
            Parent = api,
            Name = operationResource.OperationName,
            DisplayName = operationResource.DisplayName,
            Method = operationResource.Method,
            UriTemplate = operationResource.UrlTemplate,
        };

        foreach (var parameterName in GetTemplateParameterNames(operationResource.UrlTemplate))
        {
            operation.TemplateParameters.Add(new ApiManagementParameterProvisioningModel
            {
                Name = parameterName,
                Type = "string",
                Required = true,
            });
        }

        infrastructure.Add(operation);

        var policyXml = operationResource.PolicyXml ??
            CreatePolicyDocument(operationResource.InboundPolicyStatements);

        if (policyXml is null)
        {
            return;
        }

        var policy = new ApiManagementOperationPolicyProvisioningResource(
            $"{operationIdentifier}Policy")
        {
            Parent = operation,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml,
        };
        infrastructure.Add(policy);
    }

    private static IResourceBuilder<AzureApiManagementApiResource> WithOpenAIBackend(
        IResourceBuilder<AzureApiManagementApiResource> builder,
        IResource deployment,
        AzureProvisioningResource account,
        ReferenceExpression endpoint,
        string deploymentName,
        int priority,
        int weight)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 100);

        if (builder.Resource.Target is not null)
        {
            throw new InvalidOperationException(
                $"API '{builder.Resource.Name}' already routes to compute resource '{builder.Resource.Target.Name}' and cannot also use a backend pool.");
        }

        if (builder.Resource.OpenAIBackends.Any(backend => ReferenceEquals(backend.Account, account) &&
            string.Equals(backend.DeploymentName, deploymentName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Deployment '{deploymentName}' from account '{account.Name}' is already a backend of API '{builder.Resource.Name}'.");
        }

        if (builder.Resource.OpenAIBackends.Count == 30)
        {
            throw new InvalidOperationException(
                $"API Management backend pool '{builder.Resource.Name}' cannot contain more than 30 backends.");
        }

        builder.Resource.OpenAIBackends.Add(new(
            deployment.Name,
            account,
            endpoint,
            deploymentName,
            priority,
            weight));

        return builder.WithRelationship(deployment, "Backend pool");
    }

    private static string? CreatePolicyDocument(
        IReadOnlyList<string> inboundStatements,
        string? backendIdentifier = null,
        bool inheritParentPolicy = true,
        bool authenticateWithManagedIdentity = false)
    {
        if (backendIdentifier is null && inboundStatements.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("<policies>\n");
        builder.Append("  <inbound>\n");
        if (inheritParentPolicy)
        {
            builder.Append("    <base />\n");
        }

        if (authenticateWithManagedIdentity)
        {
            builder.Append("    <authentication-managed-identity resource=\"https://cognitiveservices.azure.com\" />\n");
        }

        if (backendIdentifier is not null)
        {
            builder.Append("    <set-backend-service backend-id=\"")
                .Append(backendIdentifier)
                .Append("\" />\n");
        }

        foreach (var statement in inboundStatements)
        {
            foreach (var line in statement.Split('\n'))
            {
                builder.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
            }
        }

        builder.Append("  </inbound>\n");
        if (inheritParentPolicy)
        {
            builder.Append("  <backend><base /></backend>\n");
            builder.Append("  <outbound><base /></outbound>\n");
            builder.Append("  <on-error><base /></on-error>\n");
        }
        else
        {
            // A service policy is the root of the policy hierarchy, so <base /> has no parent
            // scope to inherit. It must forward the request explicitly instead.
            builder.Append("  <backend><forward-request /></backend>\n");
            builder.Append("  <outbound />\n");
            builder.Append("  <on-error />\n");
        }
        builder.Append("</policies>");

        return builder.ToString();
    }

    private static IEnumerable<string> GetTemplateParameterNames(string uriTemplate)
    {
        // APIM operation templates use placeholders such as:
        //   /products/{productId}/items/{itemId}?locale={locale}
        // Each distinct placeholder must have a matching templateParameters entry.
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        var searchIndex = 0;

        while (searchIndex < uriTemplate.Length)
        {
            var openingBrace = uriTemplate.IndexOf('{', searchIndex);
            if (openingBrace < 0)
            {
                yield break;
            }

            var closingBrace = uriTemplate.IndexOf('}', openingBrace + 1);
            if (closingBrace < 0)
            {
                yield break;
            }

            var parameterName = uriTemplate[(openingBrace + 1)..closingBrace];
            if (parameterName.Length > 0 && parameterNames.Add(parameterName))
            {
                yield return parameterName;
            }

            searchIndex = closingBrace + 1;
        }
    }

    private static void ValidatePolicyFragment(string policyXml)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyXml);

        try
        {
            var document = XDocument.Parse($"<fragment>{policyXml}</fragment>");
            if (document.Root?.Elements().Any(e => e.Name.LocalName == "policies") is true)
            {
                throw new ArgumentException(
                    "An inbound policy statement must not contain a complete <policies> document.",
                    nameof(policyXml));
            }
        }
        catch (System.Xml.XmlException exception)
        {
            throw new ArgumentException("The policy statement must be well-formed XML.", nameof(policyXml), exception);
        }
    }

    private static void ValidatePolicyDocument(string policyXml)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyXml);

        try
        {
            var document = XDocument.Parse(policyXml);
            if (document.Root?.Name.LocalName != "policies")
            {
                throw new ArgumentException(
                    "A complete policy document must have a <policies> root element.",
                    nameof(policyXml));
            }

        }
        catch (System.Xml.XmlException exception)
        {
            throw new ArgumentException("The policy document must be well-formed XML.", nameof(policyXml), exception);
        }
    }

    private static void EnsurePolicyFragmentCanBeAdded(string? completePolicyXml, string scope)
    {
        if (completePolicyXml is not null)
        {
            throw new InvalidOperationException(
                $"A complete {scope}-level policy has already been configured. " +
                $"{nameof(WithPolicy)} and {nameof(WithInboundPolicy)} cannot be combined at the same scope.");
        }
    }

    private static void EnsureCompletePolicyCanBeSet(IReadOnlyList<string> inboundPolicyStatements, string scope)
    {
        if (inboundPolicyStatements.Count > 0)
        {
            throw new InvalidOperationException(
                $"{scope}-level inbound policy statements have already been configured. " +
                $"{nameof(WithPolicy)} and {nameof(WithInboundPolicy)} cannot be combined at the same scope.");
        }
    }

    private static void ValidateApiIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);

        if (value.Length > 256)
        {
            throw new ArgumentException(
                "API Management API identifiers cannot exceed 256 characters.",
                parameterName);
        }

        if (value.IndexOfAny(s_invalidApiIdentifierCharacters) >= 0)
        {
            throw new ArgumentException(
                "API Management API identifiers cannot contain '*', '#', '&', '+', ':', '<', '>', or '?'.",
                parameterName);
        }
    }

    private static void ValidateOperationIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);

        if (value.Length > 80)
        {
            throw new ArgumentException(
                "API Management operation identifiers cannot exceed 80 characters.",
                parameterName);
        }
    }

    private static void ValidateApiUniqueness(
        AzureApiManagementResource service,
        string apiName,
        string normalizedPath)
    {
        if (service.Apis.Any(api => string.Equals(api.ApiName, apiName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"API Management service '{service.Name}' already contains an API with the physical identifier '{apiName}'.");
        }

        if (service.Apis.Any(api => string.Equals(api.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"API Management service '{service.Name}' already contains an API at path '{normalizedPath}'.");
        }
    }

    internal static string CreateBoundedIdentifier(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..8];
        return $"{value[..(maximumLength - hash.Length - 1)]}-{hash}";
    }

    private static void ValidateCapacity(AzureApiManagementSku sku, int capacity)
    {
        var (minimum, maximum) = sku switch
        {
            AzureApiManagementSku.Consumption => (0, 0),
            AzureApiManagementSku.Developer => (1, 1),
            AzureApiManagementSku.Basic => (1, 2),
            AzureApiManagementSku.BasicV2 => (1, 10),
            AzureApiManagementSku.Standard => (1, 4),
            AzureApiManagementSku.StandardV2 => (1, 10),
            AzureApiManagementSku.Premium => (1, 12),
            AzureApiManagementSku.PremiumV2 => (1, 30),
            _ => throw new UnreachableException(),
        };

        if (capacity < minimum || capacity > maximum)
        {
            throw new ArgumentException(
                $"Capacity for SKU '{sku}' must be between {minimum} and {maximum}.",
                nameof(capacity));
        }
    }

    private static string GetProvisioningSku(AzureApiManagementSku sku) =>
        sku switch
        {
            AzureApiManagementSku.Consumption => "Consumption",
            AzureApiManagementSku.Developer => "Developer",
            AzureApiManagementSku.Basic => "Basic",
            AzureApiManagementSku.BasicV2 => "BasicV2",
            AzureApiManagementSku.Standard => "Standard",
            AzureApiManagementSku.StandardV2 => "StandardV2",
            AzureApiManagementSku.Premium => "Premium",
            AzureApiManagementSku.PremiumV2 => "PremiumV2",
            _ => throw new UnreachableException(),
        };
}
