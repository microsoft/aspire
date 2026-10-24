extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param existingApiClientId string

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
      resourceAppId: existingApiClientId
      resourceAccess: [
        {
          id: '11112222-aaaa-3333-bbbb-4444cccc5555'
          type: 'Scope'
        }
      ]
    }
    {
      resourceAppId: '00001111-aaaa-2222-bbbb-3333cccc4444'
      resourceAccess: [
        {
          id: '22223333-aaaa-4444-bbbb-5555cccc6666'
          type: 'Scope'
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

resource existingApiServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: existingApiClientId
}

resource existingApiDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: existingApiServicePrincipal.id
  scope: 'orders.read'
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName