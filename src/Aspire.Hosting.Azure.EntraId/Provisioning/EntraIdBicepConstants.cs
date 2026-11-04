// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Shared identifiers for Entra provisioning outputs and Microsoft Graph resource types.
/// </summary>
internal static class EntraIdBicepConstants
{
    /// <summary>The application client ID output name.</summary>
    internal const string ClientIdOutputName = "clientId";

    /// <summary>The home tenant ID output name.</summary>
    internal const string TenantIdOutputName = "tenantId";

    /// <summary>The application object ID output name.</summary>
    internal const string ObjectIdOutputName = "objectId";

    /// <summary>The service principal object ID output name.</summary>
    internal const string ServicePrincipalIdOutputName = "servicePrincipalId";

    /// <summary>The application unique name output name.</summary>
    internal const string UniqueNameOutputName = "uniqueName";

    /// <summary>The application ID URI output name.</summary>
    internal const string ApplicationIdUriOutputName = "applicationIdUri";

    /// <summary>The Microsoft Graph delegated permission grant resource type.</summary>
    internal const string OAuth2PermissionGrantResourceType = "Microsoft.Graph/oauth2PermissionGrants";

    /// <summary>The Microsoft Graph app role assignment resource type. The resource is the API's service principal.</summary>
    internal const string AppRoleAssignedToResourceType = "Microsoft.Graph/appRoleAssignedTo";

    /// <summary>The Microsoft Graph application resource type.</summary>
    internal const string ApplicationResourceType = "Microsoft.Graph/applications";

    /// <summary>The Microsoft Graph service principal resource type.</summary>
    internal const string ServicePrincipalResourceType = "Microsoft.Graph/servicePrincipals";

    /// <summary>The Microsoft Graph federated identity credential resource type.</summary>
    internal const string FederatedIdentityCredentialResourceType = "Microsoft.Graph/applications/federatedIdentityCredentials";

    /// <summary>
    /// The service principal tag that marks it as an integrated enterprise application. The Entra portal sets it when it
    /// creates a service principal, but one created through Microsoft Graph has no tags. Without it the service principal
    /// is hidden from the Enterprise applications list and from the application picker for catalogs and access packages,
    /// even though the Graph entitlement management APIs accept it.
    /// See https://learn.microsoft.com/powershell/module/azuread/new-azureadserviceprincipal (the -Tags example).
    /// </summary>
    internal const string IntegratedAppTag = "WindowsAzureActiveDirectoryIntegratedApp";

    /// <summary>The Microsoft Graph resource API version.</summary>
    internal const string GraphApiVersion = "v1.0";
}
