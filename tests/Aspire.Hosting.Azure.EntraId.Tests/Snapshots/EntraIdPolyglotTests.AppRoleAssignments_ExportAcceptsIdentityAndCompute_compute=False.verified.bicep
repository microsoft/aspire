extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param caller_identityPrincipalId string

resource application 'Microsoft.Graph/applications@v1.0' = {
  appRoles: [
    {
      id: '6b9e4a7e-31fe-8be8-ad5a-b30699497765'
      displayName: 'Read'
      description: 'Read.'
      value: 'Read'
      isEnabled: true
      allowedMemberTypes: [
        'Application'
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

resource caller_identityReadRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: '6b9e4a7e-31fe-8be8-ad5a-b30699497765'
  principalId: caller_identityPrincipalId
  resourceId: servicePrincipal.id
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName