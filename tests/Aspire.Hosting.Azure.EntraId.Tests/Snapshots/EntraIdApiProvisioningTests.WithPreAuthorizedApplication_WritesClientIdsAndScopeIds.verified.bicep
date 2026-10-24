extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0' as microsoftGraphV1

param tenantResourceSuffix string = ''

param entraWebClientId string

resource application 'Microsoft.Graph/applications@v1.0' = {
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
    oauth2PermissionScopes: [
      {
        id: 'e3062f1b-9e6d-8ffe-ad1a-47062669c708'
        value: 'access_as_user'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'access_as_user'
        adminConsentDescription: 'Allows the app to call the API on behalf of the signed-in user.'
        userConsentDisplayName: 'access_as_user'
        userConsentDescription: 'Allows the app to call the API on behalf of the signed-in user.'
      }
      {
        id: '6adca8e9-0075-8da6-9179-7bd4383f7303'
        value: 'other'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'other'
        adminConsentDescription: 'Another scope.'
        userConsentDisplayName: 'other'
        userConsentDescription: 'Another scope.'
      }
    ]
    preAuthorizedApplications: [
      {
        appId: entraWebClientId
        delegatedPermissionIds: [
          'e3062f1b-9e6d-8ffe-ad1a-47062669c708'
          '6adca8e9-0075-8da6-9179-7bd4383f7303'
        ]
      }
      {
        appId: '00001111-aaaa-2222-bbbb-3333cccc4444'
        delegatedPermissionIds: [
          'e3062f1b-9e6d-8ffe-ad1a-47062669c708'
        ]
      }
    ]
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

output applicationIdUri string = 'api://${application.appId}'