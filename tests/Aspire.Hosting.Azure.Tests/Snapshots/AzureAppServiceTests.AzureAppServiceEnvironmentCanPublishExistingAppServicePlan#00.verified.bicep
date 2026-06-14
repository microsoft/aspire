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
    env_mi_outputs_clientid: env_mi.outputs.clientId
    env_acr_outputs_name: env_acr.outputs.name
    appServicePlanName: appServicePlanName
    appServicePlanResourceGroup: appServicePlanResourceGroup
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

output env_AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output env_planId string = env.outputs.planId

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID

output env_AZURE_APP_SERVICE_DASHBOARD_URI string = env.outputs.AZURE_APP_SERVICE_DASHBOARD_URI

output env_AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = env.outputs.AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID

output env_AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = env.outputs.AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID