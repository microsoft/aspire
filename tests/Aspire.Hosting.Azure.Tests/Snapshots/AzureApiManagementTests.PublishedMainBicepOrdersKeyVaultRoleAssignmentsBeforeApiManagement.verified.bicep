targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module vault 'vault/vault.bicep' = {
  name: 'vault'
  scope: resourceGroup('security-resources')
  params: {
    location: location
  }
}

module apim_identity 'apim-identity/apim-identity.bicep' = {
  name: 'apim-identity'
  scope: rg
  params: {
    location: location
  }
}

module apim 'apim/apim.bicep' = {
  name: 'apim'
  scope: rg
  params: {
    location: location
    apim_identity_outputs_id: apim_identity.outputs.id
    apim_identity_outputs_clientid: apim_identity.outputs.clientId
  }
  dependsOn: [
    apim_identity_roles_vault
  ]
}

module apim_identity_roles_vault 'apim-identity-roles-vault/apim-identity-roles-vault.bicep' = {
  name: 'apim-identity-roles-vault'
  scope: resourceGroup('security-resources')
  params: {
    location: location
    principalId: apim_identity.outputs.principalId
  }
}