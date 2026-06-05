@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param aca_env_acr_outputs_name string

resource aca_env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('aca_env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource aca_env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: aca_env_acr_outputs_name
}

resource aca_env_acr_aca_env_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aca_env_acr.id, aca_env_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: aca_env_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: aca_env_acr
}

resource aca_env_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('acaenvlaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource aca_env 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: take('acaenv${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: aca_env_law.properties.customerId
        sharedKey: aca_env_law.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: tags
}

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2024-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: aca_env
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = aca_env_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = aca_env_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = aca_env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = aca_env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = aca_env_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = aca_env.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = aca_env.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = aca_env.properties.defaultDomain