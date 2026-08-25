// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning is experimental.
#pragma warning disable ASPIREAZURE003 // Azure provisioning APIs are experimental.
#pragma warning disable ASPIRECOMPUTE002 // Compute environment endpoint projection is experimental.

using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Foundry;
using Azure.Provisioning;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Storage;

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
        ValidateMaximumLength(options.PublisherEmail, 100, "The publisher email address", nameof(options));
        ValidateMaximumLength(options.PublisherName, 100, "The publisher name", nameof(options));
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
        ArgumentNullException.ThrowIfNull(target);
        return AddApiCore(builder, name, path, displayName, subscriptionRequired, apiName, target.Resource);
    }

    /// <summary>
    /// Adds an API whose backend is configured separately.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical API name.</param>
    /// <param name="path">The public path beneath the API Management gateway hostname.</param>
    /// <param name="displayName">The API display name. The resource name is used when omitted.</param>
    /// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key. The default is <see langword="true"/>.</param>
    /// <param name="apiName">The physical API identifier in API Management. The Aspire resource name is used when omitted.</param>
    /// <returns>A builder for the API resource.</returns>
    /// <remarks>
    /// Configure the API backend with <see cref="WithBackend(IResourceBuilder{AzureApiManagementApiResource}, IResourceBuilder{AzureApiManagementBackendResource})"/>
    /// or <see cref="WithBackend(IResourceBuilder{AzureApiManagementApiResource}, IResourceBuilder{AzureApiManagementBackendPoolResource})"/>.
    /// </remarks>
    [AspireExport("addApiWithoutTarget")]
    public static IResourceBuilder<AzureApiManagementApiResource> AddApi(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string path,
        string? displayName = null,
        bool subscriptionRequired = true,
        string? apiName = null)
    {
        return AddApiCore(builder, name, path, displayName, subscriptionRequired, apiName);
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
    /// Create backends with <see cref="AddAzureOpenAIBackend"/> or <see cref="AddFoundryBackend"/>,
    /// compose them with <see cref="AddBackendPool"/>, and attach the pool with <see cref="WithBackend(IResourceBuilder{AzureApiManagementApiResource}, IResourceBuilder{AzureApiManagementBackendPoolResource})"/>.
    /// Requests beneath <paramref name="path"/> are appended to the selected backend URI.
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
        var apiBuilder = AddApiCore(builder, name, path, displayName, subscriptionRequired, apiName);

        apiBuilder.AddOperation(
            CreateBoundedIdentifier($"{name}-chat-completions", 64),
            method: "POST",
            urlTemplate: "/chat/completions",
            displayName: "Create chat completion",
            operationName: "chat-completions");

        return apiBuilder;
    }

    /// <summary>
    /// Adds a product to the API Management service.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical product name.</param>
    /// <param name="displayName">The product display name.</param>
    /// <param name="options">The product options.</param>
    /// <param name="productName">The physical product identifier in API Management. The resource name is used when omitted.</param>
    /// <returns>A builder for the product resource.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementProductResource> AddProduct(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string displayName,
        AzureApiManagementProductOptions? options = null,
        string? productName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(displayName);

        var resolvedProductName = productName ?? name;
        ValidateGeneralIdentifier(resolvedProductName, 256, nameof(productName));
        if (displayName.Length > 300)
        {
            throw new ArgumentException("The product display name cannot exceed 300 characters.", nameof(displayName));
        }

        if (builder.Resource.Products.Any(product =>
            string.Equals(product.ProductName, resolvedProductName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An API Management product with physical name '{resolvedProductName}' has already been added.");
        }

        options ??= new AzureApiManagementProductOptions();
        if (options.Description?.Length > 1000)
        {
            throw new ArgumentException("The product description cannot exceed 1000 characters.", nameof(options));
        }
        if (options.ApprovalRequired && !options.SubscriptionRequired)
        {
            throw new ArgumentException("Product approval can only be required when subscriptions are required.", nameof(options));
        }
        if (options.SubscriptionsLimit is not null && !options.SubscriptionRequired)
        {
            throw new ArgumentException("A product subscription limit can only be configured when subscriptions are required.", nameof(options));
        }
        if (options.SubscriptionsLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SubscriptionsLimit,
                "The product subscription limit must be greater than zero.");
        }

        var resource = new AzureApiManagementProductResource(
            name,
            resolvedProductName,
            displayName,
            options,
            builder.Resource);
        builder.Resource.Products.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("BoxMultiple");
    }

    /// <summary>
    /// Adds an API to an API Management product.
    /// </summary>
    /// <param name="builder">The API Management product resource builder.</param>
    /// <param name="api">The API to add to the product.</param>
    /// <returns>The product resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementProductResource> WithApi(
        this IResourceBuilder<AzureApiManagementProductResource> builder,
        IResourceBuilder<AzureApiManagementApiResource> api)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(api);

        if (!ReferenceEquals(builder.Resource.Parent, api.Resource.Parent))
        {
            throw new InvalidOperationException("An API Management product can only contain APIs from the same API Management service.");
        }
        if (!builder.Resource.Apis.Contains(api.Resource))
        {
            builder.Resource.Apis.Add(api.Resource);
        }

        return builder.WithRelationship(api.Resource, "Includes");
    }

    /// <summary>
    /// Adds a subscription scoped to an API Management product.
    /// </summary>
    /// <param name="builder">The API Management product resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical subscription name.</param>
    /// <param name="displayName">The subscription display name.</param>
    /// <param name="options">The subscription options.</param>
    /// <param name="subscriptionName">The physical subscription identifier in API Management. The resource name is used when omitted.</param>
    /// <returns>A builder for the subscription resource.</returns>
    /// <remarks>API Management generates the primary and secondary subscription keys during deployment.</remarks>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementSubscriptionResource> AddSubscription(
        this IResourceBuilder<AzureApiManagementProductResource> builder,
        [ResourceName] string name,
        string displayName,
        AzureApiManagementSubscriptionOptions? options = null,
        string? subscriptionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(displayName);

        var resolvedSubscriptionName = subscriptionName ?? name;
        ValidateGeneralIdentifier(resolvedSubscriptionName, 256, nameof(subscriptionName));
        if (displayName.Length > 100)
        {
            throw new ArgumentException("The subscription display name cannot exceed 100 characters.", nameof(displayName));
        }

        if (builder.Resource.Parent.Products
            .SelectMany(product => product.Subscriptions)
            .Any(subscription => string.Equals(
                subscription.SubscriptionName,
                resolvedSubscriptionName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An API Management subscription with physical name '{resolvedSubscriptionName}' has already been added.");
        }

        var resource = new AzureApiManagementSubscriptionResource(
            name,
            resolvedSubscriptionName,
            displayName,
            options ?? new AzureApiManagementSubscriptionOptions(),
            builder.Resource);
        builder.Resource.Subscriptions.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("Key");
    }

    /// <summary>
    /// Adds a non-secret named value to the API Management service.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical named-value name.</param>
    /// <param name="value">The named value.</param>
    /// <param name="displayName">The name used to reference the value in policies. The resource name is used when omitted.</param>
    /// <param name="namedValueName">The physical named-value identifier in API Management. The resource name is used when omitted.</param>
    /// <param name="tags">Tags used to organize the named value.</param>
    /// <returns>A builder for the named-value resource.</returns>
    [AspireExport("addApiManagementNamedValue", MethodName = "addNamedValue")]
    public static IResourceBuilder<AzureApiManagementNamedValueResource> AddNamedValue(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string value,
        string? displayName = null,
        string? namedValueName = null,
        string[]? tags = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return AddNamedValueCore(builder, name, value, secret: false, displayName, namedValueName, tags);
    }

    /// <summary>
    /// Adds a secret parameter as a named value in the API Management service.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical named-value name.</param>
    /// <param name="value">The secret parameter containing the named value.</param>
    /// <param name="displayName">The name used to reference the value in policies. The resource name is used when omitted.</param>
    /// <param name="namedValueName">The physical named-value identifier in API Management. The resource name is used when omitted.</param>
    /// <param name="tags">Tags used to organize the named value.</param>
    /// <returns>A builder for the named-value resource.</returns>
    [AspireExport("addApiManagementSecretNamedValue", MethodName = "addSecretNamedValue")]
    public static IResourceBuilder<AzureApiManagementNamedValueResource> AddSecretNamedValue(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> value,
        string? displayName = null,
        string? namedValueName = null,
        string[]? tags = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Resource.Secret)
        {
            throw new ArgumentException(
                "The named-value parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.",
                nameof(value));
        }

        return AddNamedValueCore(builder, name, value.Resource, secret: true, displayName, namedValueName, tags);
    }

    /// <summary>
    /// Adds a Key Vault-backed named value to the API Management service.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical named-value name.</param>
    /// <param name="value">The Key Vault secret reference.</param>
    /// <param name="displayName">The name used to reference the value in policies. The resource name is used when omitted.</param>
    /// <param name="namedValueName">The physical named-value identifier in API Management. The resource name is used when omitted.</param>
    /// <param name="tags">Tags used to organize the named value.</param>
    /// <returns>A builder for the named-value resource.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts pass the Key Vault resource and secret name separately.")]
    public static IResourceBuilder<AzureApiManagementNamedValueResource> AddKeyVaultNamedValue(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IAzureKeyVaultSecretReference value,
        string? displayName = null,
        string? namedValueName = null,
        string[]? tags = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddNamedValueCore(builder, name, value, secret: true, displayName, namedValueName, tags)
            .WithRelationship(value.Resource, "Secret");
    }

    /// <summary>
    /// Adds a Key Vault-backed named value to the API Management service from a polyglot AppHost.
    /// </summary>
    [AspireExport("addApiManagementKeyVaultNamedValue", MethodName = "addKeyVaultNamedValue")]
    internal static IResourceBuilder<AzureApiManagementNamedValueResource> AddKeyVaultNamedValueForPolyglot(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<AzureKeyVaultResource> vault,
        string secretName,
        string? displayName = null,
        string? namedValueName = null,
        string[]? tags = null)
    {
        ArgumentNullException.ThrowIfNull(vault);

        return builder.AddKeyVaultNamedValue(
            name,
            vault.GetSecret(secretName),
            displayName,
            namedValueName,
            tags);
    }

    /// <summary>
    /// Adds a reusable policy fragment to the API Management service.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical policy-fragment name.</param>
    /// <param name="policyXml">One or more well-formed APIM policy elements without a <c>fragment</c> envelope.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="fragmentName">The physical policy-fragment identifier in API Management. The resource name is used when omitted.</param>
    /// <returns>A builder for the policy-fragment resource.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementPolicyFragmentResource> AddPolicyFragment(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string policyXml,
        string? description = null,
        string? fragmentName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidatePolicyFragment(policyXml);

        var resolvedFragmentName = fragmentName ?? name;
        ValidatePolicyFragmentIdentifier(resolvedFragmentName, nameof(fragmentName));
        if (description?.Length > 1000)
        {
            throw new ArgumentException("The policy-fragment description cannot exceed 1000 characters.", nameof(description));
        }
        if (builder.Resource.PolicyFragments.Any(fragment =>
            string.Equals(fragment.FragmentName, resolvedFragmentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An API Management policy fragment with physical name '{resolvedFragmentName}' has already been added.");
        }

        var resource = new AzureApiManagementPolicyFragmentResource(
            name,
            resolvedFragmentName,
            policyXml,
            description,
            builder.Resource);
        builder.Resource.PolicyFragments.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("Code");
    }

    /// <summary>
    /// Includes a policy fragment in the service-level inbound policy.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="fragment">The policy fragment to include.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withApiManagementServiceInboundPolicyFragment", MethodName = "withInboundPolicyFragment")]
    public static IResourceBuilder<AzureApiManagementResource> WithInboundPolicyFragment(
        this IResourceBuilder<AzureApiManagementResource> builder,
        IResourceBuilder<AzureApiManagementPolicyFragmentResource> fragment)
    {
        ValidatePolicyFragmentParent(builder.Resource, fragment);
        builder.WithInboundPolicy(CreateIncludeFragmentPolicy(fragment.Resource.FragmentName));
        return builder.WithRelationship(fragment.Resource, "Policy fragment");
    }

    /// <summary>
    /// Includes a policy fragment in the API-level inbound policy.
    /// </summary>
    /// <param name="builder">The API Management API resource builder.</param>
    /// <param name="fragment">The policy fragment to include.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withApiManagementApiInboundPolicyFragment", MethodName = "withInboundPolicyFragment")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithInboundPolicyFragment(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<AzureApiManagementPolicyFragmentResource> fragment)
    {
        ValidatePolicyFragmentParent(builder.Resource.Parent, fragment);
        builder.WithInboundPolicy(CreateIncludeFragmentPolicy(fragment.Resource.FragmentName));
        return builder.WithRelationship(fragment.Resource, "Policy fragment");
    }

    /// <summary>
    /// Includes a policy fragment in the operation-level inbound policy.
    /// </summary>
    /// <param name="builder">The API Management operation resource builder.</param>
    /// <param name="fragment">The policy fragment to include.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withApiManagementOperationInboundPolicyFragment", MethodName = "withInboundPolicyFragment")]
    public static IResourceBuilder<AzureApiManagementOperationResource> WithInboundPolicyFragment(
        this IResourceBuilder<AzureApiManagementOperationResource> builder,
        IResourceBuilder<AzureApiManagementPolicyFragmentResource> fragment)
    {
        ValidatePolicyFragmentParent(builder.Resource.Parent.Parent, fragment);
        builder.WithInboundPolicy(CreateIncludeFragmentPolicy(fragment.Resource.FragmentName));
        return builder.WithRelationship(fragment.Resource, "Policy fragment");
    }

    /// <summary>
    /// Sends service-level API Management diagnostics to Application Insights.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="applicationInsights">The Application Insights resource.</param>
    /// <param name="options">The diagnostic options.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withApiManagementServiceApplicationInsights", MethodName = "withApplicationInsights")]
    public static IResourceBuilder<AzureApiManagementResource> WithApplicationInsights(
        this IResourceBuilder<AzureApiManagementResource> builder,
        IResourceBuilder<AzureApplicationInsightsResource> applicationInsights,
        AzureApiManagementDiagnosticOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(applicationInsights);
        if (builder.Resource.Diagnostic is not null)
        {
            throw new InvalidOperationException("Application Insights diagnostics have already been configured for this API Management service.");
        }

        options ??= new AzureApiManagementDiagnosticOptions();
        ValidateDiagnosticOptions(options);
        builder.Resource.Diagnostic = new(applicationInsights.Resource, options);
        return builder.WithRelationship(applicationInsights.Resource, "Diagnostics");
    }

    /// <summary>
    /// Sends API-level API Management diagnostics to Application Insights.
    /// </summary>
    /// <param name="builder">The API Management API resource builder.</param>
    /// <param name="applicationInsights">The Application Insights resource.</param>
    /// <param name="options">The diagnostic options.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withApiManagementApiApplicationInsights", MethodName = "withApplicationInsights")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithApplicationInsights(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<AzureApplicationInsightsResource> applicationInsights,
        AzureApiManagementDiagnosticOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(applicationInsights);
        if (builder.Resource.Diagnostic is not null)
        {
            throw new InvalidOperationException("Application Insights diagnostics have already been configured for this API Management API.");
        }

        options ??= new AzureApiManagementDiagnosticOptions();
        ValidateDiagnosticOptions(options);
        builder.Resource.Diagnostic = new(applicationInsights.Resource, options);
        return builder.WithRelationship(applicationInsights.Resource, "Diagnostics");
    }

    /// <summary>
    /// Configures a custom hostname using a certificate stored in Azure Key Vault.
    /// </summary>
    /// <param name="builder">The Azure API Management resource builder.</param>
    /// <param name="hostname">The fully qualified custom hostname.</param>
    /// <param name="certificate">The Key Vault secret containing a PFX certificate.</param>
    /// <param name="type">The API Management endpoint to configure.</param>
    /// <param name="defaultSslBinding">Whether this certificate is the default SNI fallback. This is valid only for gateway hostnames.</param>
    /// <param name="negotiateClientCertificate">Whether the endpoint negotiates client certificates.</param>
    /// <returns>The resource builder.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts pass the Key Vault resource and secret name separately.")]
    public static IResourceBuilder<AzureApiManagementResource> WithCustomDomain(
        this IResourceBuilder<AzureApiManagementResource> builder,
        string hostname,
        IAzureKeyVaultSecretReference certificate,
        AzureApiManagementHostnameType type = AzureApiManagementHostnameType.Proxy,
        bool defaultSslBinding = false,
        bool negotiateClientCertificate = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(hostname);
        ArgumentNullException.ThrowIfNull(certificate);

        if (Uri.CheckHostName(hostname) != UriHostNameType.Dns)
        {
            throw new ArgumentException("The custom hostname must be a fully qualified DNS name.", nameof(hostname));
        }
        if (defaultSslBinding && type != AzureApiManagementHostnameType.Proxy)
        {
            throw new ArgumentException("The default SSL binding can only be configured for a proxy hostname.", nameof(defaultSslBinding));
        }
        if (defaultSslBinding && builder.Resource.CustomDomains.Any(domain => domain.DefaultSslBinding))
        {
            throw new InvalidOperationException("Only one custom hostname can be configured as the default SSL binding.");
        }
        if (builder.Resource.Options.Sku == AzureApiManagementSku.Consumption)
        {
            throw new InvalidOperationException("Custom domains are not supported by the API Management Consumption SKU.");
        }
        if (builder.Resource.Options.Sku is AzureApiManagementSku.BasicV2 or AzureApiManagementSku.StandardV2 or AzureApiManagementSku.PremiumV2 &&
            type is AzureApiManagementHostnameType.Management or AzureApiManagementHostnameType.Scm)
        {
            throw new InvalidOperationException(
                $"Custom domains for the '{type}' endpoint are not supported by API Management v2 SKUs.");
        }
        if (type == AzureApiManagementHostnameType.Proxy &&
            builder.Resource.CustomDomains.Any(domain => domain.Type == AzureApiManagementHostnameType.Proxy) &&
            builder.Resource.Options.Sku is not (AzureApiManagementSku.Developer or AzureApiManagementSku.Premium or AzureApiManagementSku.PremiumV2))
        {
            throw new InvalidOperationException(
                $"Multiple gateway custom domains are not supported by the API Management '{builder.Resource.Options.Sku}' SKU.");
        }
        if (builder.Resource.CustomDomains.Any(domain =>
            domain.Type == type &&
            string.Equals(domain.Hostname, hostname, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The custom hostname '{hostname}' has already been configured for endpoint type '{type}'.");
        }

        builder.Resource.CustomDomains.Add(new(
            hostname,
            certificate,
            type,
            defaultSslBinding,
            negotiateClientCertificate));

        return builder.WithRelationship(certificate.Resource, "Certificate");
    }

    /// <summary>
    /// Configures a custom hostname from a polyglot AppHost using a certificate stored in Azure Key Vault.
    /// </summary>
    [AspireExport("withCustomDomain")]
    internal static IResourceBuilder<AzureApiManagementResource> WithCustomDomainForPolyglot(
        this IResourceBuilder<AzureApiManagementResource> builder,
        string hostname,
        IResourceBuilder<AzureKeyVaultResource> vault,
        string certificateSecretName,
        AzureApiManagementHostnameType type = AzureApiManagementHostnameType.Proxy,
        bool defaultSslBinding = false,
        bool negotiateClientCertificate = false)
    {
        ArgumentNullException.ThrowIfNull(vault);

        return builder.WithCustomDomain(
            hostname,
            vault.GetSecret(certificateSecretName),
            type,
            defaultSslBinding,
            negotiateClientCertificate);
    }

    /// <summary>
    /// Adds a configurable backend to the API Management service.
    /// </summary>
    /// <param name="builder">The API Management resource builder.</param>
    /// <param name="name">The globally unique Aspire resource name and default physical backend name.</param>
    /// <param name="uri">The deferred URI of the backend service.</param>
    /// <param name="options">The backend options.</param>
    /// <param name="backendName">The physical backend identifier in API Management. The Aspire resource name is used when omitted.</param>
    /// <returns>A builder for the backend resource.</returns>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementBackendResource> AddBackend(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        ReferenceExpression uri,
        AzureApiManagementBackendOptions? options = null,
        string? backendName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(uri);

        var resolvedBackendName = backendName ?? name;
        ValidateGeneralIdentifier(resolvedBackendName, 80, nameof(backendName));
        if (builder.Resource.Backends.Any(backend =>
            string.Equals(backend.BackendName, resolvedBackendName, StringComparison.OrdinalIgnoreCase)) ||
            builder.Resource.BackendPools.Any(pool =>
                string.Equals(pool.BackendPoolName, resolvedBackendName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"An API Management backend with physical name '{resolvedBackendName}' has already been added.");
        }

        options ??= new AzureApiManagementBackendOptions();
        ValidateBackendOptions(builder.Resource, options);
        ValidateDisplayName(options.Title ?? name, "backend title", nameof(options));

        var resource = new AzureApiManagementBackendResource(name, resolvedBackendName, uri, options, builder.Resource);
        builder.Resource.Backends.Add(resource);

        return (builder.ApplicationBuilder.ExecutionContext.IsRunMode
                ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
                : builder.ApplicationBuilder.AddResource(resource))
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("ArrowRouting");
    }

    /// <summary>
    /// Adds an Azure OpenAI deployment as an API Management backend.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementBackendResource> AddAzureOpenAIBackend(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<AzureOpenAIDeploymentResource> deployment,
        string? backendName = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var backend = AddOpenAIBackend(
            builder,
            name,
            deployment.Resource.Parent,
            ReferenceExpression.Create($"{deployment.Resource.Parent.Endpoint}"),
            deployment.Resource.DeploymentName,
            backendName);
        backend.Resource.RoleAssignments.Add(new(
            deployment.Resource.Parent,
            CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser));

        return backend.WithRelationship(deployment.Resource, "Backend");
    }

    /// <summary>
    /// Adds a Microsoft Foundry OpenAI deployment as an API Management backend.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementBackendResource> AddFoundryBackend(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<FoundryDeploymentResource> deployment,
        string? backendName = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (!string.Equals(deployment.Resource.Format, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
               $"Foundry deployment '{deployment.Resource.Name}' uses format '{deployment.Resource.Format}'. " +
               "Only OpenAI-format deployments can be added as a Foundry backend.");
        }

        var backend = AddOpenAIBackend(
            builder,
            name,
            deployment.Resource.Parent,
            ReferenceExpression.Create($"{deployment.Resource.Parent.Endpoint}"),
            deployment.Resource.DeploymentName,
            backendName);
        backend.Resource.RoleAssignments.Add(new(
            deployment.Resource.Parent,
            CognitiveServicesBuiltInRole.CognitiveServicesUser));

        return backend.WithRelationship(deployment.Resource, "Backend");
    }

    /// <summary>
    /// Adds an Azure Blob Storage service as an API Management backend.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementBackendResource> AddBlobStorageBackend(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        IResourceBuilder<AzureBlobStorageResource> blobs,
        string? backendName = null)
    {
        ArgumentNullException.ThrowIfNull(blobs);

        var backend = builder.AddBackend(
            name,
            blobs.Resource.UriExpression,
            new AzureApiManagementBackendOptions
            {
                ManagedIdentityResource = "https://storage.azure.com/",
            },
            backendName);
        backend.Resource.RoleAssignments.Add(new(
            blobs.Resource.Parent,
            StorageBuiltInRole.StorageBlobDataReader));

        return backend.WithRelationship(blobs.Resource, "Backend");
    }

    /// <summary>
    /// Adds a reusable load-balancing pool to the API Management service.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<AzureApiManagementBackendPoolResource> AddBackendPool(
        this IResourceBuilder<AzureApiManagementResource> builder,
        [ResourceName] string name,
        string? displayName = null,
        string? backendPoolName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resolvedBackendPoolName = backendPoolName ?? name;
        ValidateGeneralIdentifier(resolvedBackendPoolName, 80, nameof(backendPoolName));
        if (builder.Resource.BackendPools.Any(pool =>
            string.Equals(pool.BackendPoolName, resolvedBackendPoolName, StringComparison.OrdinalIgnoreCase)) ||
            builder.Resource.Backends.Any(backend =>
                string.Equals(backend.BackendName, resolvedBackendPoolName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"An API Management backend or backend pool with physical name '{resolvedBackendPoolName}' has already been added.");
        }

        var resource = new AzureApiManagementBackendPoolResource(
            name,
            resolvedBackendPoolName,
            displayName ?? name,
            builder.Resource);
        ValidateDisplayName(resource.DisplayName, "backend-pool display name", nameof(displayName));
        builder.Resource.BackendPools.Add(resource);

        return (builder.ApplicationBuilder.ExecutionContext.IsRunMode
                ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
                : builder.ApplicationBuilder.AddResource(resource))
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("ArrowRouting");
    }

    /// <summary>
    /// Adds a backend to a load-balancing pool.
    /// </summary>
    [AspireExport("addBackendPoolMember")]
    public static IResourceBuilder<AzureApiManagementBackendPoolResource> WithBackend(
        this IResourceBuilder<AzureApiManagementBackendPoolResource> builder,
        IResourceBuilder<AzureApiManagementBackendResource> backend,
        int priority = 1,
        int weight = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(backend);
        if (!ReferenceEquals(builder.Resource.Parent, backend.Resource.Parent))
        {
            throw new InvalidOperationException(
                $"Backend '{backend.Resource.Name}' and pool '{builder.Resource.Name}' must belong to the same API Management service.");
        }
        if (priority is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Priority must be between 0 and 100.");
        }
        if (weight is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be between 0 and 100.");
        }
        if (builder.Resource.Backends.Count == 30)
        {
            throw new InvalidOperationException("An API Management backend pool cannot contain more than 30 backends.");
        }
        if (builder.Resource.Backends.Any(member => ReferenceEquals(member.Backend, backend.Resource)))
        {
            throw new InvalidOperationException(
                $"Backend '{backend.Resource.Name}' has already been added to pool '{builder.Resource.Name}'.");
        }

        var existingAudience = builder.Resource.Backends
            .Select(member => member.Backend.Options.ManagedIdentityResource)
            .FirstOrDefault();
        if (builder.Resource.Backends.Count > 0 &&
            !string.Equals(existingAudience, backend.Resource.Options.ManagedIdentityResource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"All backends in pool '{builder.Resource.Name}' must use the same managed-identity resource URI.");
        }

        builder.Resource.Backends.Add(new(backend.Resource, priority, weight));
        return builder.WithRelationship(backend.Resource, "Member");
    }

    /// <summary>
    /// Configures an API to route requests to a backend.
    /// </summary>
    [AspireExport("withApiBackend")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithBackend(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<AzureApiManagementBackendResource> backend)
    {
        return WithBackendCore(builder, backend.Resource);
    }

    /// <summary>
    /// Configures an API to route requests to a backend pool.
    /// </summary>
    [AspireExport("withApiBackendPool")]
    public static IResourceBuilder<AzureApiManagementApiResource> WithBackend(
        this IResourceBuilder<AzureApiManagementApiResource> builder,
        IResourceBuilder<AzureApiManagementBackendPoolResource> backend)
    {
        return WithBackendCore(builder, backend.Resource);
    }

    private static IResourceBuilder<AzureApiManagementApiResource> AddApiCore(
        IResourceBuilder<AzureApiManagementResource> builder,
        string name,
        string path,
        string? displayName,
        bool subscriptionRequired,
        string? apiName,
        IComputeResource? target = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ValidateApiIdentifier(apiName ?? name, nameof(apiName));

        var normalizedPath = path.Trim('/');
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);
        ValidateMaximumLength(normalizedPath, 400, "The API path", nameof(path));
        ValidateDisplayName(displayName ?? name, "API display name", nameof(displayName));
        ValidateApiUniqueness(builder.Resource, apiName ?? name, normalizedPath);

        var resource = target is null
            ? new AzureApiManagementApiResource(
                name,
                apiName ?? name,
                normalizedPath,
                displayName ?? name,
                subscriptionRequired,
                builder.Resource)
            : new AzureApiManagementApiResource(
                name,
                apiName ?? name,
                normalizedPath,
                displayName ?? name,
                subscriptionRequired,
                target,
                builder.Resource);
        builder.Resource.Apis.Add(resource);

        return (builder.ApplicationBuilder.ExecutionContext.IsRunMode
                ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
                : builder.ApplicationBuilder.AddResource(resource))
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName("DocumentTableArrowRight");
    }

    private static IResourceBuilder<AzureApiManagementBackendResource> AddOpenAIBackend(
        IResourceBuilder<AzureApiManagementResource> builder,
        string name,
        AzureProvisioningResource account,
        ReferenceExpression endpoint,
        string deploymentName,
        string? backendName)
    {
        if (builder.Resource.Options.Sku == AzureApiManagementSku.Consumption)
        {
            throw new InvalidOperationException(
                "OpenAI backends require API Management backend circuit breakers, which are not supported by the Consumption SKU.");
        }

        var backend = builder.AddBackend(
            name,
            ReferenceExpression.Create($"{endpoint}openai/deployments/{deploymentName}"),
            new AzureApiManagementBackendOptions
            {
                ManagedIdentityResource = "https://cognitiveservices.azure.com",
                CircuitBreaker = new AzureApiManagementCircuitBreakerOptions
                {
                    Name = "openAIThrottling",
                    StatusCodeRanges = [new(429, 429)],
                    AcceptRetryAfter = true,
                },
            },
            backendName);

        return backend.WithRelationship(account, "Account");
    }

    private static IResourceBuilder<AzureApiManagementApiResource> WithBackendCore(
        IResourceBuilder<AzureApiManagementApiResource> builder,
        IResource backend)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(backend);
        if (builder.Resource.Target is not null)
        {
            throw new InvalidOperationException(
                $"API '{builder.Resource.Name}' already targets compute resource '{builder.Resource.Target.Name}'.");
        }

        var backendParent = backend switch
        {
            AzureApiManagementBackendResource value => value.Parent,
            AzureApiManagementBackendPoolResource value => value.Parent,
            _ => throw new ArgumentException("The resource must be an API Management backend or backend pool.", nameof(backend)),
        };
        if (!ReferenceEquals(builder.Resource.Parent, backendParent))
        {
            throw new InvalidOperationException(
                $"API '{builder.Resource.Name}' and backend '{backend.Name}' must belong to the same API Management service.");
        }
        if (builder.Resource.Backend is not null)
        {
            throw new InvalidOperationException($"API '{builder.Resource.Name}' already has a backend.");
        }

        builder.Resource.Backend = backend;
        return builder.WithRelationship(backend, "Backend");
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
        ValidateMaximumLength(urlTemplate, 1000, "The operation URL template", nameof(urlTemplate));
        ValidateDisplayName(displayName ?? name, "operation display name", nameof(displayName));
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

        UserAssignedIdentity? keyVaultIdentity = null;
        if (azureResource.CustomDomains.Count > 0 ||
            azureResource.NamedValues.Any(namedValue => namedValue.Value is IAzureKeyVaultSecretReference))
        {
            keyVaultIdentity = new UserAssignedIdentity(
                CreateGeneratedBicepIdentifier("keyVaultIdentity", azureResource.Name));
            infrastructure.Add(keyVaultIdentity);

            service.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssignedUserAssigned;
            service.Identity.UserAssignedIdentities[
                BicepFunction.Interpolate($"{keyVaultIdentity.Id}").Compile().ToString()] = new UserAssignedIdentityDetails();
        }

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

        var keyVaultRoleAssignments = new Dictionary<string, RoleAssignment>(StringComparer.Ordinal);
        foreach (var customDomain in azureResource.CustomDomains)
        {
            Debug.Assert(keyVaultIdentity is not null);
            var secret = customDomain.Certificate.AsKeyVaultSecret(infrastructure);
            var roleAssignment = AddKeyVaultRoleAssignment(
                infrastructure,
                customDomain.Certificate,
                keyVaultIdentity.PrincipalId,
                KeyVaultBuiltInRole.KeyVaultCertificateUser,
                keyVaultRoleAssignments);
            service.DependsOn.Add(roleAssignment);
            service.HostnameConfigurations.Add(new ApiManagementHostnameConfigurationProvisioningModel
            {
                Type = GetProvisioningHostnameType(customDomain.Type),
                HostName = customDomain.Hostname,
                KeyVaultId = CreateVersionlessSecretUri(secret),
                IdentityClientId = keyVaultIdentity.ClientId,
                DefaultSslBinding = customDomain.DefaultSslBinding,
                NegotiateClientCertificate = customDomain.NegotiateClientCertificate,
            });
        }

        infrastructure.Add(service);

        var policyFragments = AddPolicyFragments(infrastructure, azureResource, service);
        AddNamedValues(infrastructure, azureResource, service, keyVaultIdentity, keyVaultRoleAssignments);
        AddServicePolicy(infrastructure, azureResource, service, policyFragments);

        var provisionedBackends = AddBackends(infrastructure, azureResource, service);
        var provisionedBackendPools = AddBackendPools(infrastructure, azureResource, service, provisionedBackends);
        AddBackendRoleAssignments(infrastructure, azureResource, service);

        var provisionedApis = new Dictionary<AzureApiManagementApiResource, ApiManagementApiProvisioningResource>();
        foreach (var apiResource in azureResource.Apis)
        {
            provisionedApis.Add(
                apiResource,
                AddApi(
                    infrastructure,
                    apiResource,
                    service,
                    provisionedBackends,
                    provisionedBackendPools,
                    policyFragments));
        }

        AddProducts(infrastructure, azureResource, service, provisionedApis);
        AddDiagnostics(infrastructure, azureResource, service, provisionedApis);

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
        ApiManagementServiceProvisioningResource service,
        IReadOnlyList<ApiManagementPolicyFragmentProvisioningResource> policyFragments)
    {
        var policyXml = azureResource.PolicyXml ??
            CreatePolicyDocument(azureResource.InboundPolicyStatements, inheritParentPolicy: false);

        if (policyXml is null)
        {
            return;
        }

        var policy = new ApiManagementServicePolicyProvisioningResource(
            CreateGeneratedBicepIdentifier("servicePolicy", service.BicepIdentifier))
        {
            Parent = service,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml!,
        };
        foreach (var policyFragment in policyFragments)
        {
            policy.DependsOn.Add(policyFragment);
        }
        infrastructure.Add(policy);
    }

    private static IReadOnlyList<ApiManagementPolicyFragmentProvisioningResource> AddPolicyFragments(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service)
    {
        var provisionedFragments = new List<ApiManagementPolicyFragmentProvisioningResource>();
        foreach (var fragmentResource in azureResource.PolicyFragments)
        {
            var fragment = new ApiManagementPolicyFragmentProvisioningResource(
                Infrastructure.NormalizeBicepIdentifier(fragmentResource.Name))
            {
                Parent = service,
                Name = fragmentResource.FragmentName,
                Format = "rawxml",
                Value = CreatePolicyFragmentDocument(fragmentResource.Value),
            };
            if (fragmentResource.Description is not null)
            {
                fragment.Description = fragmentResource.Description;
            }

            infrastructure.Add(fragment);
            provisionedFragments.Add(fragment);
        }

        return provisionedFragments;
    }

    private static void AddNamedValues(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service,
        UserAssignedIdentity? keyVaultIdentity,
        Dictionary<string, RoleAssignment> keyVaultRoleAssignments)
    {
        foreach (var namedValueResource in azureResource.NamedValues)
        {
            var namedValue = new ApiManagementNamedValueProvisioningResource(
                Infrastructure.NormalizeBicepIdentifier(namedValueResource.Name))
            {
                Parent = service,
                Name = namedValueResource.NamedValueName,
                DisplayName = namedValueResource.DisplayName,
                Secret = namedValueResource.Secret,
            };
            foreach (var tag in namedValueResource.Tags)
            {
                namedValue.Tags.Add(tag);
            }

            if (namedValueResource.Value is IAzureKeyVaultSecretReference secretReference)
            {
                Debug.Assert(keyVaultIdentity is not null);
                var secret = secretReference.AsKeyVaultSecret(infrastructure);
                namedValue.KeyVault = new ApiManagementKeyVaultNamedValueProvisioningModel
                {
                    SecretIdentifier = CreateVersionlessSecretUri(secret),
                    IdentityClientId = keyVaultIdentity.ClientId,
                };
                var roleAssignment = AddKeyVaultRoleAssignment(
                    infrastructure,
                    secretReference,
                    keyVaultIdentity.PrincipalId,
                    KeyVaultBuiltInRole.KeyVaultSecretsUser,
                    keyVaultRoleAssignments);
                namedValue.DependsOn.Add(roleAssignment);
            }
            else if (namedValueResource.Value is ParameterResource parameter)
            {
                namedValue.Value = parameter.AsProvisioningParameter(infrastructure, isSecure: true);
            }
            else
            {
                namedValue.Value = (string)namedValueResource.Value;
            }

            infrastructure.Add(namedValue);
        }
    }

    private static void AddProducts(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service,
        IReadOnlyDictionary<AzureApiManagementApiResource, ApiManagementApiProvisioningResource> provisionedApis)
    {
        foreach (var productResource in azureResource.Products)
        {
            var productIdentifier = Infrastructure.NormalizeBicepIdentifier(productResource.Name);
            var product = new ApiManagementProductProvisioningResource(productIdentifier)
            {
                Parent = service,
                Name = productResource.ProductName,
                DisplayName = productResource.DisplayName,
                SubscriptionRequired = productResource.Options.SubscriptionRequired,
                ApprovalRequired = productResource.Options.ApprovalRequired,
                State = productResource.Options.State switch
                {
                    AzureApiManagementProductState.NotPublished => "notPublished",
                    AzureApiManagementProductState.Published => "published",
                    _ => throw new UnreachableException(),
                },
            };
            if (productResource.Options.SubscriptionsLimit is { } subscriptionsLimit)
            {
                product.SubscriptionsLimit = subscriptionsLimit;
            }
            if (productResource.Options.Description is not null)
            {
                product.Description = productResource.Options.Description;
            }
            if (productResource.Options.Terms is not null)
            {
                product.Terms = productResource.Options.Terms;
            }
            infrastructure.Add(product);

            foreach (var apiResource in productResource.Apis)
            {
                var productApi = new ApiManagementProductApiProvisioningResource(
                    CreateGeneratedBicepIdentifier("productApi", productResource.Name, apiResource.Name))
                {
                    Parent = product,
                    Name = apiResource.ApiName,
                };
                productApi.DependsOn.Add(provisionedApis[apiResource]);
                infrastructure.Add(productApi);
            }

            foreach (var subscriptionResource in productResource.Subscriptions)
            {
                var subscription = new ApiManagementSubscriptionProvisioningResource(
                    Infrastructure.NormalizeBicepIdentifier(subscriptionResource.Name))
                {
                    Parent = service,
                    Name = subscriptionResource.SubscriptionName,
                    DisplayName = subscriptionResource.DisplayName,
                    Scope = product.Id,
                    State = subscriptionResource.Options.State switch
                    {
                        AzureApiManagementSubscriptionState.Active => "active",
                        AzureApiManagementSubscriptionState.Suspended => "suspended",
                        AzureApiManagementSubscriptionState.Submitted => "submitted",
                        AzureApiManagementSubscriptionState.Rejected => "rejected",
                        AzureApiManagementSubscriptionState.Expired => "expired",
                        AzureApiManagementSubscriptionState.Cancelled => "cancelled",
                        _ => throw new UnreachableException(),
                    },
                    AllowTracing = subscriptionResource.Options.AllowTracing,
                };
                infrastructure.Add(subscription);
            }
        }
    }

    private static void AddDiagnostics(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service,
        IReadOnlyDictionary<AzureApiManagementApiResource, ApiManagementApiProvisioningResource> provisionedApis)
    {
        var configuredDiagnostics = azureResource.Apis
            .Where(api => api.Diagnostic is not null)
            .Select(api => api.Diagnostic!)
            .Prepend(azureResource.Diagnostic)
            .OfType<AzureApiManagementDiagnostic>()
            .ToArray();
        if (configuredDiagnostics.Length == 0)
        {
            return;
        }

        var loggers = new Dictionary<AzureApplicationInsightsResource, ApiManagementLoggerProvisioningResource>();
        foreach (var diagnostic in configuredDiagnostics)
        {
            if (loggers.ContainsKey(diagnostic.ApplicationInsights))
            {
                continue;
            }

            var applicationInsights = (ApplicationInsightsComponent)diagnostic.ApplicationInsights.AddAsExistingResource(infrastructure);
            var logger = new ApiManagementLoggerProvisioningResource(
                CreateGeneratedBicepIdentifier("logger", diagnostic.ApplicationInsights.Name))
            {
                Parent = service,
                Name = CreateBoundedIdentifier($"{diagnostic.ApplicationInsights.Name}-application-insights", 256),
                LoggerType = "applicationInsights",
                ResourceId = applicationInsights.Id,
                IsBuffered = true,
                Credentials =
                {
                    { "instrumentationKey", applicationInsights.InstrumentationKey },
                },
            };
            infrastructure.Add(logger);
            loggers.Add(diagnostic.ApplicationInsights, logger);
        }

        if (azureResource.Diagnostic is { } serviceDiagnostic)
        {
            var diagnostic = CreateServiceDiagnostic(
                service,
                loggers[serviceDiagnostic.ApplicationInsights],
                serviceDiagnostic.Options);
            infrastructure.Add(diagnostic);
        }

        foreach (var apiResource in azureResource.Apis)
        {
            if (apiResource.Diagnostic is not { } apiDiagnostic)
            {
                continue;
            }

            var diagnostic = CreateApiDiagnostic(
                provisionedApis[apiResource],
                loggers[apiDiagnostic.ApplicationInsights],
                apiDiagnostic.Options);
            infrastructure.Add(diagnostic);
        }
    }

    private static ApiManagementServiceDiagnosticProvisioningResource CreateServiceDiagnostic(
        ApiManagementServiceProvisioningResource service,
        ApiManagementLoggerProvisioningResource logger,
        AzureApiManagementDiagnosticOptions options)
    {
        var diagnostic = new ApiManagementServiceDiagnosticProvisioningResource(
            CreateGeneratedBicepIdentifier("serviceDiagnostic", service.BicepIdentifier))
        {
            Parent = service,
            Name = "applicationinsights",
            LoggerId = logger.Id,
            AlwaysLog = "allErrors",
            Sampling = new ApiManagementSamplingProvisioningModel
            {
                SamplingType = "fixed",
                Percentage = options.SamplingPercentage,
            },
            HttpCorrelationProtocol = "W3C",
            LogClientIp = options.LogClientIp,
            Verbosity = GetProvisioningVerbosity(options.Verbosity),
            OperationNameFormat = "Name",
            Metrics = true,
        };
        diagnostic.DependsOn.Add(logger);
        return diagnostic;
    }

    private static ApiManagementApiDiagnosticProvisioningResource CreateApiDiagnostic(
        ApiManagementApiProvisioningResource api,
        ApiManagementLoggerProvisioningResource logger,
        AzureApiManagementDiagnosticOptions options)
    {
        var diagnostic = new ApiManagementApiDiagnosticProvisioningResource(
            CreateGeneratedBicepIdentifier("apiDiagnostic", api.BicepIdentifier))
        {
            Parent = api,
            Name = "applicationinsights",
            LoggerId = logger.Id,
            AlwaysLog = "allErrors",
            Sampling = new ApiManagementSamplingProvisioningModel
            {
                SamplingType = "fixed",
                Percentage = options.SamplingPercentage,
            },
            HttpCorrelationProtocol = "W3C",
            LogClientIp = options.LogClientIp,
            Verbosity = GetProvisioningVerbosity(options.Verbosity),
            OperationNameFormat = "Name",
            Metrics = true,
        };
        diagnostic.DependsOn.Add(logger);
        return diagnostic;
    }

    private static RoleAssignment AddKeyVaultRoleAssignment(
        AzureResourceInfrastructure infrastructure,
        IAzureKeyVaultSecretReference secretReference,
        BicepValue<Guid> principalId,
        KeyVaultBuiltInRole role,
        Dictionary<string, RoleAssignment> roleAssignments)
    {
        var secret = secretReference.AsKeyVaultSecret(infrastructure);
        var vault = secret.Parent
            ?? throw new InvalidOperationException($"Key Vault secret '{secretReference.SecretName}' does not have a parent vault.");
        var key = $"{vault.BicepIdentifier}:{role}";
        if (roleAssignments.TryGetValue(key, out var existingRoleAssignment))
        {
            return existingRoleAssignment;
        }

        var roleAssignment = vault.CreateRoleAssignment(
            role,
            RoleManagementPrincipalType.ServicePrincipal,
            principalId);
        infrastructure.Add(roleAssignment);
        roleAssignments.Add(key, roleAssignment);
        return roleAssignment;
    }

    private static BicepValue<string> CreateVersionlessSecretUri(KeyVaultSecret secret)
    {
        var vault = secret.Parent
            ?? throw new InvalidOperationException($"Key Vault secret '{secret.BicepIdentifier}' does not have a parent vault.");

        // APIM refreshes Key Vault-backed values and certificates only when the URI does not pin a secret version.
        // The generated URI has the form https://{vault}.vault.azure.net/secrets/{secret-name}.
        return BicepFunction.Interpolate($"{vault.Properties.VaultUri}secrets/{secret.Name}");
    }

    private static ApiManagementApiProvisioningResource AddApi(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementApiResource apiResource,
        ApiManagementServiceProvisioningResource service,
        IReadOnlyDictionary<AzureApiManagementBackendResource, ApiManagementBackendProvisioningResource> provisionedBackends,
        IReadOnlyDictionary<AzureApiManagementBackendPoolResource, ApiManagementBackendProvisioningResource> provisionedBackendPools,
        IReadOnlyList<ApiManagementPolicyFragmentProvisioningResource> policyFragments)
    {
        var apiIdentifier = Infrastructure.NormalizeBicepIdentifier(apiResource.Name);
        var (backendIdentifier, backend, managedIdentityResource) = apiResource.Target is not null
            ? AddComputeBackend(infrastructure, apiResource, service, apiIdentifier)
            : ResolveApiManagementBackend(apiResource, provisionedBackends, provisionedBackendPools);

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
            CreateGeneratedBicepIdentifier("proxyOperation", apiIdentifier))
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
            AddOperation(infrastructure, operationResource, api, policyFragments);
        }

        var policyXml = apiResource.PolicyXml ??
            CreatePolicyDocument(
                apiResource.InboundPolicyStatements,
                backendIdentifier,
                managedIdentityResource: managedIdentityResource);

        var policy = new ApiManagementApiPolicyProvisioningResource(
            CreateGeneratedBicepIdentifier("apiPolicy", apiIdentifier))
        {
            Parent = api,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml!,
        };
        policy.DependsOn.Add(backend);
        foreach (var policyFragment in policyFragments)
        {
            policy.DependsOn.Add(policyFragment);
        }
        infrastructure.Add(policy);

        return api;
    }

    private static (string Identifier, ApiManagementBackendProvisioningResource Backend, string? ManagedIdentityResource)
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

        var backendIdentifier = CreateGeneratedBicepIdentifier("computeBackend", apiIdentifier);
        var backendName = CreateBoundedIdentifier($"{apiIdentifier}Backend", 80);
        var endpointExpression = computeEnvironment.GetEndpointPropertyExpression(
            endpoint.Property(EndpointProperty.Url));
        var backendUrl = endpointExpression.AsProvisioningParameter(
            infrastructure,
            CreateGeneratedBicepIdentifier("computeBackendUrl", apiIdentifier));

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

        return (backendName, backend, null);
    }

    private static Dictionary<AzureApiManagementBackendResource, ApiManagementBackendProvisioningResource> AddBackends(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service)
    {
        var provisionedBackends = new Dictionary<AzureApiManagementBackendResource, ApiManagementBackendProvisioningResource>();
        foreach (var backendResource in azureResource.Backends)
        {
            var backendIdentifier = Infrastructure.NormalizeBicepIdentifier(backendResource.Name);
            var backend = new ApiManagementBackendProvisioningResource(backendIdentifier)
            {
                Parent = service,
                Name = backendResource.BackendName,
                Protocol = GetProvisioningBackendProtocol(backendResource.Options.Protocol),
                Uri = backendResource.UriExpression.AsProvisioningParameter(
                    infrastructure,
                    CreateGeneratedBicepIdentifier("backendUrl", backendIdentifier)),
                Title = backendResource.Options.Title ?? backendResource.Name,
                Type = "Single",
                ValidateCertificateChain = backendResource.Options.ValidateCertificateChain,
                ValidateCertificateName = backendResource.Options.ValidateCertificateName,
            };
            if (backendResource.Options.CircuitBreaker is { } circuitBreaker)
            {
                backend.CircuitBreaker = CreateCircuitBreaker(circuitBreaker);
            }
            infrastructure.Add(backend);
            provisionedBackends.Add(backendResource, backend);
        }

        return provisionedBackends;
    }

    private static Dictionary<AzureApiManagementBackendPoolResource, ApiManagementBackendProvisioningResource> AddBackendPools(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service,
        IReadOnlyDictionary<AzureApiManagementBackendResource, ApiManagementBackendProvisioningResource> provisionedBackends)
    {
        var provisionedPools = new Dictionary<AzureApiManagementBackendPoolResource, ApiManagementBackendProvisioningResource>();
        foreach (var poolResource in azureResource.BackendPools)
        {
            if (poolResource.Backends.Count == 0)
            {
                throw new InvalidOperationException(
                    $"API Management backend pool '{poolResource.Name}' does not contain any backends.");
            }

            var poolIdentifier = Infrastructure.NormalizeBicepIdentifier(poolResource.Name);
            var pool = new ApiManagementBackendProvisioningResource(poolIdentifier)
            {
                Parent = service,
                Name = poolResource.BackendPoolName,
                Title = poolResource.DisplayName,
                Type = "Pool",
                Pool = new ApiManagementBackendPoolProvisioningModel(),
            };

            foreach (var member in poolResource.Backends)
            {
                var backend = provisionedBackends[member.Backend];
                pool.Pool.Services.Add(new ApiManagementBackendPoolMemberProvisioningModel
                {
                    Id = backend.Id,
                    Priority = member.Priority,
                    Weight = member.Weight,
                });
                pool.DependsOn.Add(backend);
            }

            infrastructure.Add(pool);
            provisionedPools.Add(poolResource, pool);
        }

        return provisionedPools;
    }

    private static void AddBackendRoleAssignments(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementResource azureResource,
        ApiManagementServiceProvisioningResource service)
    {
        var addedAssignments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in azureResource.Backends.SelectMany(backend => backend.RoleAssignments))
        {
            var key = $"{assignment.Target.Name}:{assignment.Role}";
            if (!addedAssignments.Add(key))
            {
                continue;
            }

            RoleAssignment roleAssignment;
            switch (assignment.Role)
            {
                case CognitiveServicesBuiltInRole cognitiveServicesRole:
                    var cognitiveServicesAccount =
                        (CognitiveServicesAccount)assignment.Target.AddAsExistingResource(infrastructure);
                    roleAssignment = cognitiveServicesAccount.CreateRoleAssignment(
                        cognitiveServicesRole,
                        RoleManagementPrincipalType.ServicePrincipal,
                        service.Identity.PrincipalId);
                    roleAssignment.Name = BicepFunction.CreateGuid(
                        cognitiveServicesAccount.Id,
                        service.Id,
                        roleAssignment.RoleDefinitionId);
                    break;
                case StorageBuiltInRole storageRole:
                    var storageAccount = (StorageAccount)assignment.Target.AddAsExistingResource(infrastructure);
                    roleAssignment = storageAccount.CreateRoleAssignment(
                        storageRole,
                        RoleManagementPrincipalType.ServicePrincipal,
                        service.Identity.PrincipalId);
                    roleAssignment.Name = BicepFunction.CreateGuid(
                        storageAccount.Id,
                        service.Id,
                        roleAssignment.RoleDefinitionId);
                    break;
                default:
                    throw new UnreachableException();
            }
            infrastructure.Add(roleAssignment);
        }
    }

    private static (string Identifier, ApiManagementBackendProvisioningResource Backend, string? ManagedIdentityResource)
        ResolveApiManagementBackend(
            AzureApiManagementApiResource apiResource,
            IReadOnlyDictionary<AzureApiManagementBackendResource, ApiManagementBackendProvisioningResource> provisionedBackends,
            IReadOnlyDictionary<AzureApiManagementBackendPoolResource, ApiManagementBackendProvisioningResource> provisionedBackendPools)
    {
        return apiResource.Backend switch
        {
            AzureApiManagementBackendResource backend =>
                (backend.BackendName, provisionedBackends[backend], backend.Options.ManagedIdentityResource),
            AzureApiManagementBackendPoolResource pool =>
                (pool.BackendPoolName, provisionedBackendPools[pool], pool.Backends[0].Backend.Options.ManagedIdentityResource),
            null => throw new InvalidOperationException(
                $"API '{apiResource.Name}' does not have a backend. Call {nameof(WithBackend)} before deployment."),
            _ => throw new UnreachableException(),
        };
    }

    private static ApiManagementCircuitBreakerProvisioningModel CreateCircuitBreaker(
        AzureApiManagementCircuitBreakerOptions options)
    {
        var rule = new ApiManagementCircuitBreakerRuleProvisioningModel
        {
            Name = options.Name,
            FailureCondition = new ApiManagementCircuitBreakerFailureConditionProvisioningModel
            {
                Count = options.FailureCount,
                Interval = XmlConvert.ToString(TimeSpan.FromSeconds(options.FailureIntervalSeconds)),
            },
            TripDuration = XmlConvert.ToString(TimeSpan.FromSeconds(options.TripDurationSeconds)),
            AcceptRetryAfter = options.AcceptRetryAfter,
        };
        var circuitBreaker = new ApiManagementCircuitBreakerProvisioningModel
        {
            Rules =
            {
                rule,
            },
        };

        foreach (var range in options.StatusCodeRanges)
        {
            rule.FailureCondition.StatusCodeRanges.Add(new ApiManagementStatusCodeRangeProvisioningModel
            {
                Minimum = range.Minimum,
                Maximum = range.Maximum,
            });
        }

        return circuitBreaker;
    }

    private static void AddOperation(
        AzureResourceInfrastructure infrastructure,
        AzureApiManagementOperationResource operationResource,
        ApiManagementApiProvisioningResource api,
        IReadOnlyList<ApiManagementPolicyFragmentProvisioningResource> policyFragments)
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
            CreateGeneratedBicepIdentifier("operationPolicy", operationIdentifier))
        {
            Parent = operation,
            Name = "policy",
            Format = "rawxml",
            Value = policyXml,
        };
        foreach (var policyFragment in policyFragments)
        {
            policy.DependsOn.Add(policyFragment);
        }
        infrastructure.Add(policy);
    }

    private static IResourceBuilder<AzureApiManagementNamedValueResource> AddNamedValueCore(
        IResourceBuilder<AzureApiManagementResource> builder,
        string name,
        object value,
        bool secret,
        string? displayName,
        string? namedValueName,
        string[]? tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resolvedNamedValueName = namedValueName ?? name;
        var resolvedDisplayName = displayName ?? name;
        ValidateGeneralIdentifier(resolvedNamedValueName, 256, nameof(namedValueName));
        if (resolvedDisplayName.Length > 256 ||
            resolvedDisplayName.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '.' or '_')))
        {
            throw new ArgumentException(
                "The named-value display name must be at most 256 characters and contain only letters, digits, hyphens, periods, and underscores.",
                nameof(displayName));
        }
        if (builder.Resource.NamedValues.Any(namedValue =>
            string.Equals(namedValue.NamedValueName, resolvedNamedValueName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An API Management named value with physical name '{resolvedNamedValueName}' has already been added.");
        }

        var resource = new AzureApiManagementNamedValueResource(
            name,
            resolvedNamedValueName,
            resolvedDisplayName,
            value,
            secret,
            tags ?? [],
            builder.Resource);
        builder.Resource.NamedValues.Add(resource);

        var resourceBuilder = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);

        return resourceBuilder
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithIconName(secret ? "LockClosed" : "BracesVariable");
    }

    private static void ValidatePolicyFragmentParent(
        AzureApiManagementResource parent,
        IResourceBuilder<AzureApiManagementPolicyFragmentResource> fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (!ReferenceEquals(parent, fragment.Resource.Parent))
        {
            throw new InvalidOperationException(
                "An API Management policy can only include policy fragments from the same API Management service.");
        }
    }

    private static string CreateIncludeFragmentPolicy(string fragmentName) =>
        $"<include-fragment fragment-id=\"{fragmentName}\" />";

    private static string CreatePolicyFragmentDocument(string policyXml)
    {
        var builder = new StringBuilder();
        builder.Append("<fragment>\n");
        foreach (var line in policyXml.Split('\n'))
        {
            builder.Append("  ").Append(line.TrimEnd('\r')).Append('\n');
        }
        builder.Append("</fragment>");
        return builder.ToString();
    }

    private static string GetProvisioningHostnameType(AzureApiManagementHostnameType type) =>
        type switch
        {
            AzureApiManagementHostnameType.ConfigurationApi => "ConfigurationApi",
            AzureApiManagementHostnameType.DeveloperPortal => "DeveloperPortal",
            AzureApiManagementHostnameType.Portal => "Portal",
            AzureApiManagementHostnameType.Proxy => "Proxy",
            AzureApiManagementHostnameType.Management => "Management",
            AzureApiManagementHostnameType.Scm => "Scm",
            _ => throw new UnreachableException(),
        };

    private static string GetProvisioningVerbosity(AzureApiManagementDiagnosticVerbosity verbosity) =>
        verbosity switch
        {
            AzureApiManagementDiagnosticVerbosity.Error => "error",
            AzureApiManagementDiagnosticVerbosity.Information => "information",
            AzureApiManagementDiagnosticVerbosity.Verbose => "verbose",
            _ => throw new UnreachableException(),
        };

    private static string GetProvisioningBackendProtocol(AzureApiManagementBackendProtocol protocol) =>
        protocol switch
        {
            AzureApiManagementBackendProtocol.Http => "http",
            AzureApiManagementBackendProtocol.Soap => "soap",
            _ => throw new UnreachableException(),
        };

    private static void ValidateBackendOptions(
        AzureApiManagementResource service,
        AzureApiManagementBackendOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ManagedIdentityResource) &&
            options.ManagedIdentityResource is not null)
        {
            throw new ArgumentException(
                "The managed-identity resource URI cannot be empty.",
                nameof(options));
        }

        var circuitBreaker = options.CircuitBreaker;
        if (circuitBreaker is null)
        {
            return;
        }

        if (service.Options.Sku == AzureApiManagementSku.Consumption)
        {
            throw new InvalidOperationException(
                "API Management backend circuit breakers are not supported by the Consumption SKU.");
        }
        ArgumentException.ThrowIfNullOrEmpty(circuitBreaker.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(circuitBreaker.FailureCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(circuitBreaker.FailureIntervalSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(circuitBreaker.TripDurationSeconds, 1);

        foreach (var range in circuitBreaker.StatusCodeRanges)
        {
            if (range.Minimum is < 200 or > 599)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    range.Minimum,
                    "The minimum circuit-breaker status code must be between 200 and 599.");
            }
            if (range.Maximum < range.Minimum || range.Maximum > 599)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    range.Maximum,
                    "The maximum circuit-breaker status code must be between the minimum status code and 599.");
            }
        }
        if (circuitBreaker.StatusCodeRanges.Length > 10)
        {
            throw new ArgumentException(
                "An API Management circuit-breaker rule cannot contain more than 10 status-code ranges.",
                nameof(options));
        }
    }

    private static void ValidateDisplayName(string value, string description, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
        {
            throw new ArgumentException(
                $"The {description} must be between 1 and 300 characters.",
                parameterName);
        }
    }

    private static void ValidateMaximumLength(string value, int maximumLength, string description, string parameterName)
    {
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"{description} cannot exceed {maximumLength} characters.", parameterName);
        }
    }

    private static void ValidateDiagnosticOptions(AzureApiManagementDiagnosticOptions options)
    {
        if (!double.IsFinite(options.SamplingPercentage) ||
            options.SamplingPercentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SamplingPercentage,
                "The Application Insights sampling percentage must be between 0 and 100.");
        }
    }

    private static void ValidateGeneralIdentifier(string identifier, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier, parameterName);
        if (identifier.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The API Management identifier cannot exceed {maximumLength} characters.",
                parameterName);
        }
        if (identifier.IndexOfAny(s_invalidApiIdentifierCharacters) >= 0)
        {
            throw new ArgumentException(
                "The API Management identifier cannot contain '*', '#', '&', '+', ':', '<', '>', or '?'.",
                parameterName);
        }
    }

    private static void ValidatePolicyFragmentIdentifier(string identifier, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier, parameterName);
        if (identifier.Length > 80)
        {
            throw new ArgumentException("The policy-fragment identifier cannot exceed 80 characters.", parameterName);
        }

        static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

        if (!IsWordCharacter(identifier[0]) ||
            !IsWordCharacter(identifier[^1]) ||
            identifier.Any(character => !IsWordCharacter(character) && character != '-'))
        {
            throw new ArgumentException(
                "The policy-fragment identifier may contain letters, digits, underscores, and non-leading or trailing hyphens.",
                parameterName);
        }
    }

    private static string? CreatePolicyDocument(
        IReadOnlyList<string> inboundStatements,
        string? backendIdentifier = null,
        bool inheritParentPolicy = true,
        string? managedIdentityResource = null)
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

        if (managedIdentityResource is not null)
        {
            builder.Append("    <authentication-managed-identity ")
                .Append(new XAttribute("resource", managedIdentityResource))
                .Append(" />\n");
        }

        if (backendIdentifier is not null)
        {
            builder.Append("    <set-backend-service ")
                .Append(new XAttribute("backend-id", backendIdentifier))
                .Append(" />\n");
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

    private static string CreateGeneratedBicepIdentifier(string kind, params string[] resourceNames)
    {
        // Aspire resource names must start with a letter, so this prefix reserves a namespace for
        // generated APIM symbols that cannot collide with symbols derived from user resource names.
        return Infrastructure.NormalizeBicepIdentifier(
            string.Join('_', resourceNames.Prepend(kind).Prepend("apim").Prepend(string.Empty)));
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
