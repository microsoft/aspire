// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Describes a federated identity credential that connects a Kubernetes service account to an Azure identity.
/// </summary>
internal sealed record AksWorkloadIdentityBinding(
    string ServiceAccountName,
    string FederatedCredentialName,
    IAppIdentityResource IdentityResource);
