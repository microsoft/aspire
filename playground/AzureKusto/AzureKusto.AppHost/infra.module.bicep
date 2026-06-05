@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param infra_acr_outputs_name string

resource infra_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('infra_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource infra_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: infra_acr_outputs_name
}

resource infra_acr_infra_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(infra_acr.id, infra_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: infra_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: infra_acr
}

resource infra_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('infralaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource infra 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: take('infra${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: infra_law.properties.customerId
        sharedKey: infra_law.listKeys().primarySharedKey
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
  parent: infra
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = infra_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = infra_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = infra_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = infra_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = infra_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = infra.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = infra.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = infra.properties.defaultDomain