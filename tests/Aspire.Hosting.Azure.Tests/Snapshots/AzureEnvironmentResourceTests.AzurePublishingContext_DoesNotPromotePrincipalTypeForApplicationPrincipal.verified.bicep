targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module acaEnv_acr 'acaEnv-acr/acaEnv-acr.bicep' = {
  name: 'acaEnv-acr'
  scope: rg
  params: {
    location: location
  }
}

module acaEnv 'acaEnv/acaEnv.bicep' = {
  name: 'acaEnv'
  scope: rg
  params: {
    location: location
    acaenv_acr_outputs_name: acaEnv_acr.outputs.name
    userPrincipalId: principalId
  }
}

module roles 'roles/roles.bicep' = {
  name: 'roles'
  scope: rg
  params: {
    location: location
    principalId: principalId
    principalType: ''
  }
}