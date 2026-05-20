@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

param userPrincipalId string = ''

param aifmyfoundry_outputs_name string

param projmyproject_acr_outputs_name string

resource aifmyfoundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aifmyfoundry_outputs_name
}

resource projmyproject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  name: 'projmyproject'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'projmyproject'
  }
  tags: {
    'aspire-resource-name': 'projmyproject'
  }
  parent: aifmyfoundry
}

resource projmyproject_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: projmyproject_acr_outputs_name
}

resource projmyproject_acr_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(projmyproject_acr.id, projmyproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: projmyproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: projmyproject_acr
}

resource projmyproject_ai 'Microsoft.Insights/components@2020-02-02' = {
  name: 'projmyproject-ai'
  kind: 'web'
  location: location
  properties: {
    Application_Type: 'web'
  }
  tags: tags
}

resource projmyproject_ai_MonitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(projmyproject_ai.id, projmyproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb'))
  properties: {
    principalId: projmyproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalType: 'ServicePrincipal'
  }
  scope: projmyproject_ai
}

resource projmyproject_ai_conn 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'projmyproject-ai-conn'
  properties: {
    isSharedToAll: false
    metadata: {
      ApiType: 'Azure'
      ResourceId: projmyproject_ai.id
      location: projmyproject_ai.location
    }
    target: projmyproject_ai.id
    authType: 'ApiKey'
    credentials: {
      key: projmyproject_ai.properties.ConnectionString
    }
    category: 'AppInsights'
  }
  parent: projmyproject
}

resource aifmyfoundry_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aifmyfoundry.id, projmyproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: projmyproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: aifmyfoundry
}

output id string = projmyproject.id

output name string = '${aifmyfoundry_outputs_name}/projmyproject'

output endpoint string = projmyproject.properties.endpoints['AI Foundry API']

output principalId string = projmyproject.identity.principalId

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = projmyproject_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_NAME string = projmyproject_acr_outputs_name

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = projmyproject.identity.principalId

output APPLICATION_INSIGHTS_CONNECTION_STRING string = projmyproject_ai.properties.ConnectionString