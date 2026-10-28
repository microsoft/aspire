extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param entra_apiClientId string

param entra_apiServicePrincipalId string

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
      resourceAppId: entra_apiClientId
      resourceAccess: [
        {
          id: 'e3062f1b-9e6d-8ffe-ad1a-47062669c708'
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

resource entra_apiDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: entra_apiServicePrincipalId
  scope: 'access_as_user'
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName