@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param app_host_acr_outputs_name string

param log_env_shared_name string

param log_env_shared_rg string

resource app_host_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('app_host_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource app_host_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: app_host_acr_outputs_name
}

resource app_host_acr_app_host_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(app_host_acr.id, app_host_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: app_host_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: app_host_acr
}

resource log_env_shared 'Microsoft.OperationalInsights/workspaces@2025-02-01' existing = {
  name: log_env_shared_name
  scope: resourceGroup(log_env_shared_rg)
}

resource app_host 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: take('apphost${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: log_env_shared.properties.customerId
        sharedKey: log_env_shared.listKeys().primarySharedKey
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

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: app_host
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = log_env_shared.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = log_env_shared.id

output AZURE_CONTAINER_REGISTRY_NAME string = app_host_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = app_host_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = app_host_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = app_host.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = app_host.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = app_host.properties.defaultDomain