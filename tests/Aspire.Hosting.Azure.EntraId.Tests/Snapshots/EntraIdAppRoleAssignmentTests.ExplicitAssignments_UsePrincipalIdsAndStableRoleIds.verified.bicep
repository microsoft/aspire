extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param worker_identityPrincipalId string

param other_identityPrincipalId string

resource application 'Microsoft.Graph/applications@v1.0' = {
  appRoles: [
    {
      id: '9f3e06e3-f71c-8586-863a-d40f705e0165'
      displayName: 'Read'
      description: 'Read orders.'
      value: 'Orders.Read'
      isEnabled: true
      allowedMemberTypes: [
        'Application'
      ]
    }
    {
      id: '11112222-aaaa-3333-bbbb-4444cccc5555'
      displayName: 'Write'
      description: 'Write orders.'
      value: 'Orders.Write'
      isEnabled: true
      allowedMemberTypes: [
        'User'
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

resource applicationIdentity 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'entra-api' : 'entra-api-${tenantResourceSuffix}'
  identifierUris: [
    'api://${application.appId}'
  ]
  api: {
    oauth2PermissionScopes: []
    preAuthorizedApplications: []
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  tags: [
    'WindowsAzureActiveDirectoryIntegratedApp'
  ]
}

resource worker_identityOrdersReadRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: '9f3e06e3-f71c-8586-863a-d40f705e0165'
  principalId: worker_identityPrincipalId
  resourceId: servicePrincipal.id
}

resource worker_identityOrdersWriteRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: '11112222-aaaa-3333-bbbb-4444cccc5555'
  principalId: worker_identityPrincipalId
  resourceId: servicePrincipal.id
}

resource other_identityOrdersReadRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: '9f3e06e3-f71c-8586-863a-d40f705e0165'
  principalId: other_identityPrincipalId
  resourceId: servicePrincipal.id
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName

output applicationIdUri string = 'api://${application.appId}'