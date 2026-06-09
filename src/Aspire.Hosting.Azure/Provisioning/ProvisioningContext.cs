// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure.Core;

namespace Aspire.Hosting.Azure.Provisioning;

// Represents the Azure identity that the provisioner is acting as.
// `Type` is forwarded directly to the `principalType` property on
// Microsoft.Authorization/roleAssignments. ARM accepts a fixed set of values for that property
// (see the docs link below); today this component emits "User" or "ServicePrincipal" based on
// the credential's access token, but the field is a plain string so consumers can override it.
// Defaults to "User" to preserve historical behavior for credentials whose access tokens don't
// include the `idtyp` claim.
// principalType values: https://learn.microsoft.com/azure/templates/microsoft.authorization/roleassignments
// idtyp claim:          https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference#payload-claims
internal sealed record AzurePrincipal(Guid Id, string Name, string Type = "User");

internal sealed class ProvisioningContext(
    TokenCredential credential,
    IArmClient armClient,
    ISubscriptionResource subscription,
    IResourceGroupResource resourceGroup,
    ITenantResource tenant,
    AzureLocation location,
    AzurePrincipal principal,
    DistributedApplicationExecutionContext executionContext)
{
    public TokenCredential Credential => credential;
    public IArmClient ArmClient => armClient;
    public ISubscriptionResource Subscription => subscription;
    public ITenantResource Tenant => tenant;
    public IResourceGroupResource ResourceGroup => resourceGroup;
    public AzureLocation Location => location;
    public AzurePrincipal Principal => principal;
    public DistributedApplicationExecutionContext ExecutionContext => executionContext;
}
