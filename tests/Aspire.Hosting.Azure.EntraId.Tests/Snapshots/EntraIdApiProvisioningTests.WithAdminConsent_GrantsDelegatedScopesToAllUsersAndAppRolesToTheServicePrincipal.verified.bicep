extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param entraApiClientId string

param entraApiServicePrincipalId string

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra-web' : 'entra-web-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-web' : 'entra-web-${tenantResourceSuffix}'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    redirectUris: []
    implicitGrantSettings: {
      enableIdTokenIssuance: false
    }
  }
  requiredResourceAccess: [
    {
      resourceAppId: entraApiClientId
      resourceAccess: [
        {
          id: 'e3062f1b-9e6d-8ffe-ad1a-47062669c708'
          type: 'Scope'
        }
        {
          id: '6adca8e9-0075-8da6-9179-7bd4383f7303'
          type: 'Scope'
        }
      ]
    }
    {
      resourceAppId: '00000003-0000-0000-c000-000000000000'
      resourceAccess: [
        {
          id: 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
          type: 'Scope'
        }
        {
          id: 'a154be20-db9c-4678-8ab7-66f6cc099a59'
          type: 'Scope'
        }
        {
          id: 'df021288-bdef-4463-88db-98f22de89214'
          type: 'Role'
        }
        {
          id: '7ab1d382-f21e-4acd-a863-ba3e13f7da61'
          type: 'Role'
        }
      ]
    }
  ]
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
  ]
}

resource entraApiDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: entraApiServicePrincipalId
  scope: 'access_as_user'
}

resource microsoftGraphServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '00000003-0000-0000-c000-000000000000'
}

resource microsoftGraphDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: microsoftGraphServicePrincipal.id
  scope: 'User.Read'
}

resource microsoftGraphUserReadAllRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: 'df021288-bdef-4463-88db-98f22de89214'
  principalId: servicePrincipal.id
  resourceId: microsoftGraphServicePrincipal.id
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName