@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param app_host_acr_pull_identity_outputs_id string

param app_host_acr_outputs_name string

param log_env_shared_name string

param log_env_shared_rg string

resource app_host_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: app_host_acr_outputs_name
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

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = app_host_acr_pull_identity_outputs_id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = app_host.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = app_host.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = app_host.properties.defaultDomain