extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param web_identity_principalId string

param api_identity_principalId string

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
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
  ]
}

resource federatedCredential_web_identity 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  name: '${application.uniqueName}/aspire-web-identity'
  issuer: 'https://login.microsoftonline.com/${tenant().tenantId}/v2.0'
  subject: web_identity_principalId
  audiences: [
    'api://AzureADTokenExchange'
  ]
}

resource federatedCredential_api_identity 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  name: '${application.uniqueName}/aspire-api-identity'
  issuer: 'https://login.microsoftonline.com/${tenant().tenantId}/v2.0'
  subject: api_identity_principalId
  audiences: [
    'api://AzureADTokenExchange'
  ]
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName