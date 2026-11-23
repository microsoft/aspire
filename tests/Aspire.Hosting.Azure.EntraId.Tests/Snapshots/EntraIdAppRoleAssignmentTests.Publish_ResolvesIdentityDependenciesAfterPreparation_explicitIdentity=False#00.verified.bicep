targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param tenantResourceSuffix string = ''

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module env_acr 'env-acr/env-acr.bicep' = {
  name: 'env-acr'
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
    env_acr_outputs_name: env_acr.outputs.name
    userPrincipalId: principalId
  }
}

module entra_api 'entra-api/entra-api.bicep' = {
  name: 'entra-api'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
    worker_identityPrincipalId: worker_identity.outputs.principalId
  }
}

module worker_identity 'worker-identity/worker-identity.bicep' = {
  name: 'worker-identity'
  scope: rg
  params: {
    location: location
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output worker_identity_id string = worker_identity.outputs.id

output worker_identity_clientId string = worker_identity.outputs.clientId