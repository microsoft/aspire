extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

resource application 'Microsoft.Graph/applications@v1.0' = {
  keyCredentials: [
    {
      customKeyIdentifier: '<certificate thumbprint>'
      displayName: 'Aspire local developer certificate'
      endDateTime: '2026-06-03T00:00:00.0000000+00:00'
      key: '<public DER certificate>'
      startDateTime: '2026-06-01T00:00:00.0000000+00:00'
      type: 'AsymmetricX509Cert'
      usage: 'Verify'
    }
  ]
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

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName