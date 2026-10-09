@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param express_acr_outputs_name string

resource express_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('express_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource express_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: express_acr_outputs_name
}

resource express_acr_express_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(express_acr.id, express_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: express_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: express_acr
}

resource express_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('expresslaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource express 'Microsoft.App/managedEnvironments@2026-03-02-preview' = {
  name: take('express${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: express_law.properties.customerId
        sharedKey: express_law.listKeys().primarySharedKey
      }
    }
    environmentMode: 'Express'
  }
  tags: tags
}

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = express_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = express_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = express_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = express_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = express_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = express.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = express.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = express.properties.defaultDomain