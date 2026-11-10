@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

param vault_outputs_name string

param apim_identity_outputs_clientid string

param apim_identity_outputs_principalname string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apim_identity_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
}

resource vault_upstream_secret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'upstream-secret'
  parent: vault
}

resource apim_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: apim_identity_outputs_principalname
}

resource vault_KeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apim_identity.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6'))
  properties: {
    principalId: apim_identity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalType: 'ServicePrincipal'
  }
  scope: vault
}

resource upstream_secret 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  name: 'upstream-secret'
  properties: {
    displayName: 'upstream-secret'
    secret: true
    keyVault: {
      secretIdentifier: '${vault.properties.vaultUri}secrets/upstream-secret'
      identityClientId: apim_identity_outputs_clientid
    }
  }
  parent: apim
  dependsOn: [
    vault_KeyVaultSecretsUser
  ]
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id