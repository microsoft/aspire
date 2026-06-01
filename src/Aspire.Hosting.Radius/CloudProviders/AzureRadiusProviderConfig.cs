// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// Captures the full Azure cloud-provider configuration attached to a
/// <see cref="RadiusEnvironmentResource"/>: the subscription/resource-group
/// scope and the selected credential mode.
/// </summary>
/// <param name="SubscriptionId">Azure subscription GUID.</param>
/// <param name="ResourceGroup">Resource-group name within the subscription.</param>
/// <param name="Credential">Selected credential mode (Service Principal or Workload Identity).</param>
public sealed record AzureRadiusProviderConfig(
    string SubscriptionId,
    string ResourceGroup,
    AzureRadiusCredential Credential);

/// <summary>
/// Discriminated base for the Azure credential mode chosen via the
/// <c>WithAzureProvider</c> callback. Use one of the sealed subtypes.
/// </summary>
public abstract record AzureRadiusCredential
{
    private AzureRadiusCredential()
    {
    }

    /// <summary>
    /// Azure Service Principal credential. The client secret is bound by
    /// <see cref="IResourceBuilder{ParameterResource}"/> so secret material
    /// stays in the parameter system and never enters the publish artifact
    /// as a literal value.
    /// </summary>
    /// <param name="TenantId">Azure tenant GUID.</param>
    /// <param name="ClientId">Service principal application (client) GUID.</param>
    /// <param name="ClientSecret">Parameter resource carrying the client secret value.</param>
    public sealed record ServicePrincipal(
        string TenantId,
        string ClientId,
        IResourceBuilder<ParameterResource> ClientSecret) : AzureRadiusCredential;

    /// <summary>
    /// Azure Workload Identity (federated identity) credential. No long-lived
    /// secret is materialized; the identity is bound at deploy time by the
    /// hosting Kubernetes cluster's OIDC issuer.
    /// </summary>
    /// <param name="TenantId">Azure tenant GUID.</param>
    /// <param name="ClientId">Workload identity client (application) GUID.</param>
    public sealed record WorkloadIdentity(
        string TenantId,
        string ClientId) : AzureRadiusCredential;
}
