targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param api_website_privateLinkResourceId_input string

param api_website_privateLinkLocation_input string

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

module frontdoor 'frontdoor/frontdoor.bicep' = {
  name: 'frontdoor'
  scope: rg
  params: {
    location: location
    api_host: 'api-${env.outputs.webSiteSuffix}.azurewebsites.net'
    api_website_environment_id: api_website_privateLinkResourceId_input
    api_website_location: api_website_privateLinkLocation_input
  }
}

output env_AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output env_planId string = env.outputs.planId

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID