// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning is experimental.
#pragma warning disable ASPIREAZURE003 // Azure provisioning APIs are experimental.
#pragma warning disable ASPIRECOMPUTE002 // Compute environment endpoint projection is experimental.

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure API Management resources to the application model.
/// </summary>
public static class AzureApiManagementExtensions
{
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
    /// <param name="target">The backend compute resource. It must expose an external HTTP or HTTPS endpoint.</param>
    /// <param name="path">The public path beneath the API Management gateway hostname.</param>
    /// <param name="displayName">The API display name. The resource name is used when omitted.</param>
    /// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key.</param>
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
        bool subscriptionRequired = false)
        where T : IComputeResource, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var normalizedPath = path.Trim('/');
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);

        var resource = new AzureApiManagementApiResource(
            name,
            apiName: name,
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
    /// Adds an explicitly modeled operation to an API.
    /// </summary>
    /// <param name="builder">The API resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical operation name.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="urlTemplate">The URL template relative to the API path.</param>
    /// <param name="displayName">The operation display name. The resource name is used when omitted.</param>
    /// <returns>A builder for the operation resource.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string is empty.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementOperationResource> AddOperation(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        [ResourceName] string name,
        string method,
        string urlTemplate,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(urlTemplate);

        var resource = new AzureApiManagementOperationResource(
            name,
            operationName: name,
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
    /// APIM policy updates replace the complete policy document at a scope. This method therefore replaces all
    /// service-level policies generated by <see cref="WithInboundPolicy(IResourceBuilder{AzureApiManagementResource}, string)"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policyXml"/> is not a complete policy document.</exception>
    [AspireExport("withApiManagementServicePolicy", MethodName = "withPolicy")]
    public static IResourceBuilder<AzureApiManagementResource> WithPolicy(
        this IResourceBuilder<AzureApiManagementResource> builder,
        string policyXml)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidatePolicyDocument(policyXml);
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
        var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

        if (hasPrivateEndpoint && azureResource.VirtualNetworkConfiguration is not null)
        {
            throw new InvalidOperationException(
                $"API Management resource '{azureResource.Name}' cannot combine a private endpoint with classic virtual network injection.");
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
            PublicNetworkAccess = hasPrivateEndpoint ? "Disabled" : "Enabled",
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

        foreach (var apiResource in azureResource.Apis)
        {
            AddApi(infrastructure, apiResource, service);
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
        ApiManagementServiceProvisioningResource service)
    {
        if (apiResource.Target is not IResourceWithEndpoints endpointResource)
        {
            throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not expose endpoints and cannot be used as an API Management backend.");
        }

        var endpoint = endpointResource.GetEndpoints()
            .FirstOrDefault(e => e.EndpointAnnotation.IsExternal &&
                e.EndpointAnnotation.UriScheme is "http" or "https")
            ?? throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not have an external HTTP or HTTPS endpoint. " +
                "Call .WithExternalHttpEndpoints() before adding it to API Management.");

        if (!ComputeEnvironmentEndpointResolver.TryGetEffectiveComputeEnvironment(apiResource.Target, out var computeEnvironment))
        {
            throw new InvalidOperationException(
                $"Resource '{apiResource.Target.Name}' does not have a compute environment. " +
                "Configure an Azure deployment environment before adding it to API Management.");
        }

        var apiIdentifier = Infrastructure.NormalizeBicepIdentifier(apiResource.Name);
        var backendIdentifier = $"{apiIdentifier}Backend";
        var endpointExpression = computeEnvironment.GetEndpointPropertyExpression(
            endpoint.Property(EndpointProperty.Url));
        var backendUrl = endpointExpression.AsProvisioningParameter(infrastructure, $"{apiIdentifier}_url");

        var backend = new ApiManagementBackendProvisioningResource(
            backendIdentifier)
        {
            Parent = service,
            Name = backendIdentifier,
            Protocol = "http",
            Uri = backendUrl,
            Title = apiResource.DisplayName,
            Type = "Single",
            ValidateCertificateChain = true,
            ValidateCertificateName = true,
        };
        infrastructure.Add(backend);

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
            CreatePolicyDocument(apiResource.InboundPolicyStatements, backendIdentifier);

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

    private static string? CreatePolicyDocument(
        IReadOnlyList<string> inboundStatements,
        string? backendIdentifier = null,
        bool inheritParentPolicy = true)
    {
        if (backendIdentifier is null && inboundStatements.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<policies>");
        builder.AppendLine("  <inbound>");
        if (inheritParentPolicy)
        {
            builder.AppendLine("    <base />");
        }

        if (backendIdentifier is not null)
        {
            builder.Append("    <set-backend-service backend-id=\"")
                .Append(backendIdentifier)
                .AppendLine("\" />");
        }

        foreach (var statement in inboundStatements)
        {
            foreach (var line in statement.Split('\n'))
            {
                builder.Append("    ").AppendLine(line.TrimEnd('\r'));
            }
        }

        builder.AppendLine("  </inbound>");
        if (inheritParentPolicy)
        {
            builder.AppendLine("  <backend><base /></backend>");
            builder.AppendLine("  <outbound><base /></outbound>");
            builder.AppendLine("  <on-error><base /></on-error>");
        }
        else
        {
            // A service policy is the root of the policy hierarchy, so <base /> has no parent
            // scope to inherit. It must forward the request explicitly instead.
            builder.AppendLine("  <backend><forward-request /></backend>");
            builder.AppendLine("  <outbound />");
            builder.AppendLine("  <on-error />");
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
