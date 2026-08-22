@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

param userPrincipalId string = ''

param account_outputs_name string

resource account 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: account_outputs_name
}

resource my_project 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  name: 'my-project'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'my-project'
  }
  tags: {
    'aspire-resource-name': 'my-project'
  }
  parent: account
}

resource my_project_Foundry_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, my_project.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d'))
  properties: {
    principalId: my_project.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalType: 'ServicePrincipal'
  }
  scope: account
}

resource my_project_ai 'Microsoft.Insights/components@2020-02-02' = {
  name: 'my-project-ai'
  kind: 'web'
  location: location
  properties: {
    Application_Type: 'web'
  }
  tags: tags
}

resource my_project_ai_MonitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(my_project_ai.id, my_project.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb'))
  properties: {
    principalId: my_project.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalType: 'ServicePrincipal'
  }
  scope: my_project_ai
}

resource my_project_ai_conn 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'my-project-ai-conn'
  properties: {
    isSharedToAll: false
    metadata: {
      ApiType: 'Azure'
      ResourceId: my_project_ai.id
      location: my_project_ai.location
    }
    target: my_project_ai.id
    authType: 'ApiKey'
    credentials: {
      key: my_project_ai.properties.ConnectionString
    }
    category: 'AppInsights'
  }
  parent: my_project
}

output id string = my_project.id

output name string = '${account_outputs_name}/my-project'

output endpoint string = my_project.properties.endpoints['AI Foundry API']

output principalId string = my_project.identity.principalId

output APPLICATION_INSIGHTS_CONNECTION_STRING string = my_project_ai.properties.ConnectionString