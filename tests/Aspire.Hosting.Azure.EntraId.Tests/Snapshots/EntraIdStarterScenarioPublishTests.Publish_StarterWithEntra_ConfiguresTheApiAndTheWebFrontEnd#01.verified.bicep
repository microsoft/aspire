extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'apiservice-entra' : 'apiservice-entra-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'apiservice-entra' : 'apiservice-entra-${tenantResourceSuffix}'
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
}

resource applicationIdentity 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'apiservice-entra' : 'apiservice-entra-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'apiservice-entra' : 'apiservice-entra-${tenantResourceSuffix}'
  identifierUris: [
    'api://${application.appId}'
  ]
  api: {
    oauth2PermissionScopes: [
      {
        id: '8af8aa72-1e32-8f5e-a4c3-e39e6e2093ab'
        value: 'access_as_user'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'access_as_user'
        adminConsentDescription: 'Access the weather API on behalf of the signed-in user'
        userConsentDisplayName: 'access_as_user'
        userConsentDescription: 'Access the weather API on behalf of the signed-in user'
      }
    ]
    preAuthorizedApplications: []
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
  ]
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName

output applicationIdUri string = 'api://${application.appId}'