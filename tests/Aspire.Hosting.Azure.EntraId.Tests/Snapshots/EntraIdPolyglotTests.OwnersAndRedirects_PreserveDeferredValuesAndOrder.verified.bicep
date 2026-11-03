extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param redirects string

param redirectUri0 string

param redirectUri1 string

param applicationOwner0 string

param servicePrincipalOwner0 string

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra' : 'entra-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra' : 'entra-${tenantResourceSuffix}'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    redirectUris: concat([
      'https://localhost/signin'
    ], split(redirects, ','), [
      redirectUri0
      redirectUri1
    ])
    implicitGrantSettings: {
      enableIdTokenIssuance: false
    }
  }
  owners: {
    relationships: [
      '00001111-aaaa-2222-bbbb-3333cccc4444'
      applicationOwner0
      'aaaabbbb-0000-cccc-1111-dddd2222eeee'
    ]
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
  ]
  owners: {
    relationships: [
      '00001111-aaaa-2222-bbbb-3333cccc4444'
      servicePrincipalOwner0
      'aaaabbbb-0000-cccc-1111-dddd2222eeee'
    ]
  }
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName