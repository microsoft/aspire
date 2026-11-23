extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

resource application 'Microsoft.Graph/applications@v1.0' = {
  displayName: (tenantResourceSuffix == '') ? 'web' : 'web-${tenantResourceSuffix}'
  uniqueName: (tenantResourceSuffix == '') ? 'web' : 'web-${tenantResourceSuffix}'
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
  requiredResourceAccess: [
    {
      resourceAppId: '99998888-aaaa-7777-bbbb-6666cccc5555'
      resourceAccess: [
        {
          id: '33334444-aaaa-5555-bbbb-6666cccc7777'
          type: 'Scope'
        }
      ]
    }
    {
      resourceAppId: '11110000-aaaa-7777-bbbb-6666cccc5555'
      resourceAccess: [
        {
          id: '33334444-aaaa-5555-bbbb-6666cccc7777'
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

resource contosoAPIServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '99998888-aaaa-7777-bbbb-6666cccc5555'
}

resource contosoAPIDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: contosoAPIServicePrincipal.id
  scope: 'Read'
}

resource contosoAPIServicePrincipal_2 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '11110000-aaaa-7777-bbbb-6666cccc5555'
}

resource contosoAPIDelegatedGrant_2 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: contosoAPIServicePrincipal_2.id
  scope: 'Read'
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName