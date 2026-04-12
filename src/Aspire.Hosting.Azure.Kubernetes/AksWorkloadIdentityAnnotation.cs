// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Annotation that marks a compute resource for AKS workload identity.
/// When present, the AKS infrastructure will generate a Kubernetes ServiceAccount
/// with the appropriate annotations and a federated identity credential in Azure.
/// </summary>
internal sealed class AksWorkloadIdentityAnnotation(
    IAppIdentityResource identityResource,
    string? serviceAccountName = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the identity resource to federate with.
    /// </summary>
    public IAppIdentityResource IdentityResource { get; } = identityResource;

    /// <summary>
    /// Gets or sets the Kubernetes service account name.
    /// If null, defaults to the resource name.
    /// </summary>
    public string? ServiceAccountName { get; set; } = serviceAccountName;
}
