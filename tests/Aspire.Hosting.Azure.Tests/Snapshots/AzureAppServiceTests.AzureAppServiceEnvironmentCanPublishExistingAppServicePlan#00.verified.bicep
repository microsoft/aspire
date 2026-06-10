targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param appServicePlanResourceGroup string

param appServicePlanName string

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
    env_acr_pull_identity_outputs_clientid: env_acr_pull_identity.outputs.clientId
    env_acr_outputs_name: env_acr.outputs.name
    appServicePlanName: appServicePlanName
    appServicePlanResourceGroup: appServicePlanResourceGroup
    userPrincipalId: principalId
  }
}

module env_roles_env_acr 'env-roles-env-acr/env-roles-env-acr.bicep' = {
  name: 'env-roles-env-acr'
  scope: rg
  params: {
    location: location
    env_acr_outputs_name: env_acr.outputs.name
    principalId: env_acr_pull_identity.outputs.principalId
  }
}

output env_AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output env_planId string = env.outputs.planId

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID

output env_AZURE_APP_SERVICE_DASHBOARD_URI string = env.outputs.AZURE_APP_SERVICE_DASHBOARD_URI

output env_AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = env.outputs.AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID

output env_AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = env.outputs.AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID