extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param service_reference string

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    redirectUris: []
    implicitGrantSettings: {
      enableIdTokenIssuance: true
    }
  }
  tags: [
    'department:engineering'
    service_reference
  ]
  serviceManagementReference: service_reference
}

resource applicationIdentity 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  identifierUris: [
    'api://${application.appId}'
  ]
  api: {
    oauth2PermissionScopes: [
      {
        id: 'c6d2505b-8b71-88eb-bc42-2c3961a07f42'
        value: 'read'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'read'
        adminConsentDescription: 'Read data.'
        userConsentDisplayName: 'read'
        userConsentDescription: 'Read data.'
      }
    ]
    preAuthorizedApplications: []
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
    'department:engineering'
  ]
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName

output applicationIdUri string = 'api://${application.appId}'