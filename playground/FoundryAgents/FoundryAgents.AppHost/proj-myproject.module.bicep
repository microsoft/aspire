@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

param userPrincipalId string = ''

param aif_myfoundry_outputs_name string

param proj_myproject_acr_outputs_name string

resource aif_myfoundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aif_myfoundry_outputs_name
}

resource proj_myproject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  name: 'proj-myproject'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'proj-myproject'
  }
  tags: {
    'aspire-resource-name': 'proj-myproject'
  }
  parent: aif_myfoundry
}

resource proj_myproject_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: proj_myproject_acr_outputs_name
}

resource proj_myproject_acr_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(proj_myproject_acr.id, proj_myproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: proj_myproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: proj_myproject_acr
}

resource proj_myproject_ai 'Microsoft.Insights/components@2020-02-02' = {
  name: 'proj-myproject-ai'
  kind: 'web'
  location: location
  properties: {
    Application_Type: 'web'
  }
  tags: tags
}

resource proj_myproject_ai_MonitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(proj_myproject_ai.id, proj_myproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb'))
  properties: {
    principalId: proj_myproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalType: 'ServicePrincipal'
  }
  scope: proj_myproject_ai
}

resource proj_myproject_ai_conn 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'proj-myproject-ai-conn'
  properties: {
    isSharedToAll: false
    metadata: {
      ApiType: 'Azure'
      ResourceId: proj_myproject_ai.id
      location: proj_myproject_ai.location
    }
    target: proj_myproject_ai.id
    authType: 'ApiKey'
    credentials: {
      key: proj_myproject_ai.properties.ConnectionString
    }
    category: 'AppInsights'
  }
  parent: proj_myproject
}

resource aif_myfoundry_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aif_myfoundry.id, proj_myproject.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: proj_myproject.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: aif_myfoundry
}

output id string = proj_myproject.id

output name string = '${aif_myfoundry_outputs_name}/proj-myproject'

output endpoint string = proj_myproject.properties.endpoints['AI Foundry API']

output principalId string = proj_myproject.identity.principalId

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = proj_myproject_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_NAME string = proj_myproject_acr_outputs_name

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = proj_myproject.identity.principalId

output APPLICATION_INSIGHTS_CONNECTION_STRING string = proj_myproject_ai.properties.ConnectionString