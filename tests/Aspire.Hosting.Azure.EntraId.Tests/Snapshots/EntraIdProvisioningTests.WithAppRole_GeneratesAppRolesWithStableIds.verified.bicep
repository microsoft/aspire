extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

resource application 'Microsoft.Graph/applications@v1.0' = {
  appRoles: [
    {
      id: 'ea58f5a0-9d67-83fd-9ae1-1fe82e9cd267'
      displayName: 'User'
      description: 'Can use the API.'
      value: 'User'
      isEnabled: true
      allowedMemberTypes: [
        'User'
      ]
    }
    {
      id: '55a60a6a-5bf8-8c2a-b354-5d493918c89e'
      displayName: 'Administrator'
      description: 'Can administer the API.'
      value: 'Admin'
      isEnabled: true
      allowedMemberTypes: [
        'User'
        'Application'
      ]
    }
    {
      id: '11112222-aaaa-3333-bbbb-4444cccc5555'
      displayName: 'Reader'
      description: 'Can read data.'
      value: 'Reader'
      isEnabled: true
      allowedMemberTypes: [
        'Application'
      ]
    }
    {
      id: 'd4430742-82f8-8f76-ae01-c7b4853b7848'
      displayName: 'Retired'
      description: 'Being removed.'
      value: 'Retired'
      isEnabled: false
      allowedMemberTypes: [
        'User'
      ]
    }
  ]
  displayName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
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