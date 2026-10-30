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
    caller_identityPrincipalId: caller_identity.outputs.principalId
  }
}

module caller_identity 'caller-identity/caller-identity.bicep' = {
  name: 'caller-identity'
  scope: rg
  params: {
    location: location
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output caller_identity_id string = caller_identity.outputs.id

output caller_identity_clientId string = caller_identity.outputs.clientId