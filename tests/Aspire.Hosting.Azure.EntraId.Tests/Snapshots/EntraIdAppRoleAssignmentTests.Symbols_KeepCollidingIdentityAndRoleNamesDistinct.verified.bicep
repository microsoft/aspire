extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param workerPrincipalId string

param workerPrincipalId_2 string

param workerReadPrincipalId string

resource application 'Microsoft.Graph/applications@v1.0' = {
  appRoles: [
    {
      id: 'b4994d33-35ac-88d7-9bb7-964685cd419a'
      displayName: 'Read'
      description: 'Read.'
      value: 'Read.All'
      isEnabled: true
      allowedMemberTypes: [
        'Application'
      ]
    }
    {
      id: 'b9523371-034f-8869-af76-6dd9ffbef75f'
      displayName: 'Read'
      description: 'Read.'
      value: 'Read-All'
      isEnabled: true
      allowedMemberTypes: [
        'Application'
      ]
    }
    {
      id: 'bedb11c7-28a2-8d30-a623-92471b49d1b1'
      displayName: 'All'
      description: 'All.'
      value: 'All'
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
    redirectUris: split(workerPrincipalId, ',')
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

resource workerReadAllRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: 'b4994d33-35ac-88d7-9bb7-964685cd419a'
  principalId: workerPrincipalId_2
  resourceId: servicePrincipal.id
}

resource workerReadAllRoleAssignment_2 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: 'b9523371-034f-8869-af76-6dd9ffbef75f'
  principalId: workerPrincipalId_2
  resourceId: servicePrincipal.id
}

resource workerReadAllRoleAssignment_3 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: 'bedb11c7-28a2-8d30-a623-92471b49d1b1'
  principalId: workerReadPrincipalId
  resourceId: servicePrincipal.id
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName