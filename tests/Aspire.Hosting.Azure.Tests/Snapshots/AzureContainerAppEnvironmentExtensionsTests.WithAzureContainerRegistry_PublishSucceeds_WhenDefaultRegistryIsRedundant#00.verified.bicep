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

module env_acr_pull_identity 'env-acr-pull-identity/env-acr-pull-identity.bicep' = {
  name: 'env-acr-pull-identity'
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
    env_acr_pull_identity_outputs_id: env_acr_pull_identity.outputs.id
    acr_outputs_name: acr.outputs.name
    userPrincipalId: principalId
  }
}

module env_roles_acr 'env-roles-acr/env-roles-acr.bicep' = {
  name: 'env-roles-acr'
  scope: rg
  params: {
    location: location
    acr_outputs_name: acr.outputs.name
    principalId: env_acr_pull_identity.outputs.principalId
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID