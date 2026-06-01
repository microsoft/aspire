// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// Builder surface exposed inside the <c>WithAzureProvider</c> callback for
/// selecting an Azure credential mode. Exactly one <c>With*</c> method must
/// be called; a repeat call replaces the previous selection (FR-008).
/// </summary>
public interface IAzureRadiusProviderBuilder
{
    /// <summary>
    /// Configures a Service Principal credential. The client secret value
    /// is provided via a <see cref="ParameterResource"/> so its plaintext
    /// never appears in the publish artifact (FR-006). Reference it with
    /// <c>builder.AddParameter("azureClientSecret", secret: true)</c>.
    /// </summary>
    /// <param name="tenantId">Azure tenant GUID.</param>
    /// <param name="clientId">Service principal client (application) GUID.</param>
    /// <param name="clientSecret">Parameter carrying the client secret.</param>
    /// <returns>This builder for chaining.</returns>
    IAzureRadiusProviderBuilder WithServicePrincipal(
        string tenantId,
        string clientId,
        IResourceBuilder<ParameterResource> clientSecret);

    /// <summary>
    /// Configures a Workload Identity (federated identity) credential. No
    /// long-lived secret is materialized; the identity is bound at deploy
    /// time by the hosting Kubernetes cluster's OIDC issuer.
    /// </summary>
    /// <param name="clientId">Workload identity client (application) GUID.</param>
    /// <param name="tenantId">Azure tenant GUID.</param>
    /// <returns>This builder for chaining.</returns>
    IAzureRadiusProviderBuilder WithWorkloadIdentity(string clientId, string tenantId);
}
