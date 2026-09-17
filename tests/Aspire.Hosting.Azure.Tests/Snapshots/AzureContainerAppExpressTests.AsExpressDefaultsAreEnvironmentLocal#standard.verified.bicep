@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param standard_acr_outputs_name string

resource standard_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('standard_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource standard_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: standard_acr_outputs_name
}

resource standard_acr_standard_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(standard_acr.id, standard_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: standard_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: standard_acr
}

resource standard_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('standardlaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource standard 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: take('standard${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: standard_law.properties.customerId
        sharedKey: standard_law.listKeys().primarySharedKey
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
  parent: standard
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = standard_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = standard_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = standard_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = standard_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = standard_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = standard.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = standard.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = standard.properties.defaultDomain