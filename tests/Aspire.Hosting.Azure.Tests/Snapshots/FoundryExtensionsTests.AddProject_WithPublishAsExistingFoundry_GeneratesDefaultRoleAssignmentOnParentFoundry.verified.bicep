@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

param userPrincipalId string = ''

param project_acr_outputs_name string

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: 'existing-foundry'
  scope: resourceGroup('existing-rg')
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  name: 'project'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'project'
  }
  tags: {
    'aspire-resource-name': 'project'
  }
  parent: foundry
}

resource foundry_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, project.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: project.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: foundry
}

resource project_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: project_acr_outputs_name
}

resource project_acr_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project_acr.id, project.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: project.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: project_acr
}

resource project_ai 'Microsoft.Insights/components@2020-02-02' = {
  name: 'project-ai'
  kind: 'web'
  location: location
  properties: {
    Application_Type: 'web'
  }
  tags: tags
}

resource project_ai_MonitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project_ai.id, project.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb'))
  properties: {
    principalId: project.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalType: 'ServicePrincipal'
  }
  scope: project_ai
}

resource project_ai_conn 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'project-ai-conn'
  properties: {
    isSharedToAll: false
    metadata: {
      ApiType: 'Azure'
      ResourceId: project_ai.id
      location: project_ai.location
    }
    target: project_ai.id
    authType: 'ApiKey'
    credentials: {
      key: project_ai.properties.ConnectionString
    }
    category: 'AppInsights'
  }
  parent: project
}

output id string = project.id

output name string = '${foundry.name}/project'

output endpoint string = project.properties.endpoints['AI Foundry API']

output principalId string = project.identity.principalId

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = project_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_NAME string = project_acr_outputs_name

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = project.identity.principalId

output APPLICATION_INSIGHTS_CONNECTION_STRING string = project_ai.properties.ConnectionString