targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module identity 'identity/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: {
    location: location
  }
}

module mod 'mod/mod.bicep' = {
  name: 'mod'
  scope: rg
  params: {
    location: location
    clientId: identity.outputs.clientId
  }
}