targetScope = 'subscription'

param resourceGroup string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroup
  location: location
}

module env_acr 'env-acr/env-acr.bicep' = {
  name: 'env-acr'
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
    env_acr_outputs_name: env_acr.outputs.name
    userPrincipalId: principalId
  }
}

module env_mi_roles_env_acr 'env-mi-roles-env-acr/env-mi-roles-env-acr.bicep' = {
  name: 'env-mi-roles-env-acr'
  scope: rg
  params: {
    location: location
    env_acr_outputs_name: env_acr.outputs.name
    principalId: env_mi.outputs.principalId
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID