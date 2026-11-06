extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param orders_apiClientId string

param ordersApiClientId string

param orders_apiServicePrincipalId string

param ordersApiServicePrincipalId string

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
      resourceAppId: orders_apiClientId
      resourceAccess: [
        {
          id: '078e68e9-2632-80ad-9d41-2f1281e2020f'
          type: 'Scope'
        }
      ]
    }
    {
      resourceAppId: ordersApiClientId
      resourceAccess: [
        {
          id: 'fff852d0-6877-87fc-88d6-69adb535cfb2'
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

resource orders_apiDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: orders_apiServicePrincipalId
  scope: 'read'
}

resource ordersApiDelegatedGrant 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: servicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: ordersApiServicePrincipalId
  scope: 'read'
}

output clientId string = application.appId

output tenantId string = tenant().tenantId

output objectId string = application.id

output servicePrincipalId string = servicePrincipal.id

output uniqueName string = application.uniqueName