targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module acr 'acr/acr.bicep' = {
  name: 'acr'
  scope: rg
  params: {
    location: location
  }
}

module env_mi 'env-mi/env-mi.bicep' = {
  name: 'env-mi'
  scope: rg
  params: {
    location: location
  }
}

module env 'env/env.bicep' = {
  name: 'env'
  scope: rg
  params: {
    location: location
    env_mi_outputs_id: env_mi.outputs.id
    acr_outputs_name: acr.outputs.name
    userPrincipalId: principalId
  }
}

module env_mi_roles_acr 'env-mi-roles-acr/env-mi-roles-acr.bicep' = {
  name: 'env-mi-roles-acr'
  scope: rg
  params: {
    location: location
    acr_outputs_name: acr.outputs.name
    principalId: env_mi.outputs.principalId
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID