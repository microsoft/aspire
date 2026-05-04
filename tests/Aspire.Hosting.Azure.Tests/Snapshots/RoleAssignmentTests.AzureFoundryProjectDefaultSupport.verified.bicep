@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param project_outputs_name string

param principalId string

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' existing = {
  name: project_outputs_name
}

resource project_Azure_AI_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalType: 'ServicePrincipal'
  }
  scope: project
}