// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.EntraId.Provisioning;
using Aspire.Hosting.Azure.Provisioning;

// Graph declarations use identifiers such as "application" and "servicePrincipal", not the Aspire resource name.
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphApplication), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphServicePrincipal), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphExistingServicePrincipal), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphFederatedIdentityCredential), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphOAuth2PermissionGrant), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(MicrosoftGraphAppRoleAssignedTo), IsInfrastructureRoot = false)]
