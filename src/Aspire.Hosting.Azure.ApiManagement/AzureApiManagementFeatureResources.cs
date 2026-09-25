// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure API Management product.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ProductName = {ProductName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementProductResource(
    string name,
    string productName,
    string displayName,
    AzureApiManagementProductOptions options,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical product identifier in API Management.
    /// </summary>
    public string ProductName { get; } = productName;

    /// <summary>
    /// Gets the product display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the product options.
    /// </summary>
    public AzureApiManagementProductOptions Options { get; } = options;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal List<AzureApiManagementApiResource> Apis { get; } = [];

    internal List<AzureApiManagementSubscriptionResource> Subscriptions { get; } = [];
}

/// <summary>
/// Represents an Azure API Management subscription scoped to a product.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, SubscriptionName = {SubscriptionName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementSubscriptionResource(
    string name,
    string subscriptionName,
    string displayName,
    AzureApiManagementSubscriptionOptions options,
    AzureApiManagementProductResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementProductResource>
{
    /// <summary>
    /// Gets the physical subscription identifier in API Management.
    /// </summary>
    public string SubscriptionName { get; } = subscriptionName;

    /// <summary>
    /// Gets the subscription display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the subscription options.
    /// </summary>
    public AzureApiManagementSubscriptionOptions Options { get; } = options;

    /// <summary>
    /// Gets the parent API Management product.
    /// </summary>
    public AzureApiManagementProductResource Parent { get; } = parent;
}

/// <summary>
/// Represents an Azure API Management named value.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, NamedValueName = {NamedValueName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementNamedValueResource(
    string name,
    string namedValueName,
    string displayName,
    object value,
    bool secret,
    IReadOnlyList<string> tags,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical named-value identifier in API Management.
    /// </summary>
    public string NamedValueName { get; } = namedValueName;

    /// <summary>
    /// Gets the named-value display name used in policy expressions.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets whether the named value contains secret material.
    /// </summary>
    public bool Secret { get; } = secret;

    /// <summary>
    /// Gets the tags associated with the named value.
    /// </summary>
    public IReadOnlyList<string> Tags { get; } = tags;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal object Value { get; } = value;
}

/// <summary>
/// Represents a reusable Azure API Management policy fragment.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, FragmentName = {FragmentName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementPolicyFragmentResource(
    string name,
    string fragmentName,
    string value,
    string? description,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical policy-fragment identifier in API Management.
    /// </summary>
    public string FragmentName { get; } = fragmentName;

    /// <summary>
    /// Gets the XML policy statements contained by the fragment.
    /// </summary>
    public string Value { get; } = value;

    /// <summary>
    /// Gets the optional policy-fragment description.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;
}

/// <summary>
/// Configures an Azure API Management product.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureApiManagementProductOptions
{
    /// <summary>
    /// Gets the product description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the terms of use presented to subscribers.
    /// </summary>
    public string? Terms { get; init; }

    /// <summary>
    /// Gets whether a subscription is required to access APIs in the product.
    /// </summary>
    public bool SubscriptionRequired { get; init; } = true;

    /// <summary>
    /// Gets whether subscription requests require administrator approval.
    /// </summary>
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Gets the maximum number of subscriptions a user may create for the product. A null value allows unlimited subscriptions.
    /// </summary>
    public int? SubscriptionsLimit { get; init; }

    /// <summary>
    /// Gets the product publication state.
    /// </summary>
    public AzureApiManagementProductState State { get; init; } = AzureApiManagementProductState.Published;
}

/// <summary>
/// Configures an Azure API Management subscription.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureApiManagementSubscriptionOptions
{
    /// <summary>
    /// Gets or sets whether tracing is allowed for the subscription.
    /// </summary>
    public bool AllowTracing { get; set; }

    /// <summary>
    /// Gets or sets the subscription state.
    /// </summary>
    public AzureApiManagementSubscriptionState State { get; set; } = AzureApiManagementSubscriptionState.Active;
}

/// <summary>
/// Configures an Azure API Management Application Insights diagnostic.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureApiManagementDiagnosticOptions
{
    /// <summary>
    /// Gets the percentage of requests to sample, from 0 to 100.
    /// </summary>
    public double SamplingPercentage { get; init; } = 100;

    /// <summary>
    /// Gets the diagnostic verbosity.
    /// </summary>
    public AzureApiManagementDiagnosticVerbosity Verbosity { get; init; } = AzureApiManagementDiagnosticVerbosity.Information;

    /// <summary>
    /// Gets whether the client IP address is included in telemetry.
    /// </summary>
    public bool LogClientIp { get; init; }
}

/// <summary>
/// Specifies the publication state of an API Management product.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public enum AzureApiManagementProductState
{
    /// <summary>
    /// The product is not visible to subscribers.
    /// </summary>
    NotPublished,

    /// <summary>
    /// The product is visible to subscribers.
    /// </summary>
    Published,
}

/// <summary>
/// Specifies the state of an API Management subscription.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public enum AzureApiManagementSubscriptionState
{
    /// <summary>
    /// The subscription is active.
    /// </summary>
    Active,

    /// <summary>
    /// The subscription is suspended.
    /// </summary>
    Suspended,

    /// <summary>
    /// The subscription was submitted for approval.
    /// </summary>
    Submitted,

    /// <summary>
    /// The subscription was rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// The subscription expired.
    /// </summary>
    Expired,

    /// <summary>
    /// The subscription was cancelled.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Specifies an Azure API Management custom-hostname endpoint.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public enum AzureApiManagementHostnameType
{
    /// <summary>
    /// The configuration API endpoint.
    /// </summary>
    ConfigurationApi,

    /// <summary>
    /// The API gateway endpoint.
    /// </summary>
    Proxy,

    /// <summary>
    /// The developer portal endpoint.
    /// </summary>
    DeveloperPortal,

    /// <summary>
    /// The legacy developer portal endpoint.
    /// </summary>
    Portal,

    /// <summary>
    /// The management endpoint.
    /// </summary>
    Management,

    /// <summary>
    /// The source-control endpoint.
    /// </summary>
    Scm,
}

/// <summary>
/// Specifies the verbosity of an API Management diagnostic.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public enum AzureApiManagementDiagnosticVerbosity
{
    /// <summary>
    /// Records only errors.
    /// </summary>
    Error,

    /// <summary>
    /// Records errors and informational events.
    /// </summary>
    Information,

    /// <summary>
    /// Records detailed diagnostic events.
    /// </summary>
    Verbose,
}

internal sealed record AzureApiManagementCustomDomain(
    string Hostname,
    IAzureKeyVaultSecretReference Certificate,
    AzureApiManagementHostnameType Type,
    bool DefaultSslBinding,
    bool NegotiateClientCertificate);

internal sealed record AzureApiManagementDiagnostic(
    AzureApplicationInsightsResource ApplicationInsights,
    AzureApiManagementDiagnosticOptions Options);
