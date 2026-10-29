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

module apiservice_entra 'apiservice-entra/apiservice-entra.bicep' = {
  name: 'apiservice-entra'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
  }
}

module webfrontend_entra 'webfrontend-entra/webfrontend-entra.bicep' = {
  name: 'webfrontend-entra'
  scope: rg
  params: {
    tenantResourceSuffix: tenantResourceSuffix
    redirectUri0: 'https://webfrontend.${env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN}/signin-oidc'
    redirectUri1: 'https://webfrontend.${env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN}/signout-callback-oidc'
    apiservice_entraClientId: apiservice_entra.outputs.clientId
    webfrontend_identity_principalId: webfrontend_identity.outputs.principalId
  }
}

module webfrontend_identity 'webfrontend-identity/webfrontend-identity.bicep' = {
  name: 'webfrontend-identity'
  scope: rg
  params: {
    location: location
  }
}

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output env_AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output apiservice_entra_tenantId string = apiservice_entra.outputs.tenantId

output apiservice_entra_clientId string = apiservice_entra.outputs.clientId

output webfrontend_identity_id string = webfrontend_identity.outputs.id

output webfrontend_entra_tenantId string = webfrontend_entra.outputs.tenantId

output webfrontend_entra_clientId string = webfrontend_entra.outputs.clientId

output webfrontend_identity_clientId string = webfrontend_identity.outputs.clientId

output apiservice_entra_applicationIdUri string = apiservice_entra.outputs.applicationIdUri