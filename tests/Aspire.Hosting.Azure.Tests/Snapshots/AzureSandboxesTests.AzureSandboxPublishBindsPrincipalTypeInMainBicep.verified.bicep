targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param principalType string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module sandboxes_acr 'sandboxes-acr/sandboxes-acr.bicep' = {
  name: 'sandboxes-acr'
  scope: rg
  params: {
    location: location
  }
}

module sandboxes 'sandboxes/sandboxes.bicep' = {
  name: 'sandboxes'
  scope: rg
  params: {
    location: location
    userPrincipalId: principalId
    principalType: principalType
  }
}