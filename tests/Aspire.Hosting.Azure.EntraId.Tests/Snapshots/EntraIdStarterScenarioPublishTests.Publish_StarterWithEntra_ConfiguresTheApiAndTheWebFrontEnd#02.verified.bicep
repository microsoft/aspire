extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param redirectUri0 string

param redirectUri1 string

param apiservice_entraClientId string

param webfrontend_identity_principalId string

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'webfrontend-entra' : 'webfrontend-entra-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'webfrontend-entra' : 'webfrontend-entra-${tenantResourceSuffix}'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    redirectUris: [
      redirectUri0
      redirectUri1
    ]
    implicitGrantSettings: {
      enableIdTokenIssuance: false
    }
  }
  requiredResourceAccess: [
    {
      resourceAppId: apiservice_entraClientId
      resourceAccess: [
        {
          id: '8af8aa72-1e32-8f5e-a4c3-e39e6e2093ab'
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

resource federatedCredential_webfrontend_identity 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  name: '${application.uniqueName}/aspire-webfrontend-identity'
  issuer: 'https://login.microsoftonline.com/${tenant().tenantId}/v2.0'
  subject: webfrontend_identity_principalId
  audiences: [
    'api://AzureADTokenExchange'
  ]
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName