extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: '238f6e2a-e70f-80cb-9b77-77bd8da7521d'
        value: 'orders.read'
        type: 'Admin'
        isEnabled: true
        adminConsentDisplayName: 'Read orders'
        adminConsentDescription: 'Read orders.'
        userConsentDisplayName: 'Read your orders'
        userConsentDescription: 'Allows the app to read your orders.'
      }
      {
        id: '6c884e29-1117-8d2d-b5f1-22cd9a21dac6'
        value: 'orders.write'
        type: 'User'
        isEnabled: false
        adminConsentDisplayName: 'orders.write'
        adminConsentDescription: 'Write orders.'
        userConsentDisplayName: 'orders.write'
        userConsentDescription: 'Write orders.'
      }
    ]
  }
  web: {
    redirectUris: []
    implicitGrantSettings: {
      enableIdTokenIssuance: false
    }
  }
  identifierUris: [
    'api://contoso-orders'
  ]
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

output applicationIdUri string = 'api://contoso-orders'