@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param ai_outputs_name string

param principalId string

resource ai 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: ai_outputs_name
}

resource ai_CognitiveServicesFaceRecognizer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ai.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '9894cab4-e18a-44aa-828b-cb588cd6f2d7'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '9894cab4-e18a-44aa-828b-cb588cd6f2d7')
    principalType: 'ServicePrincipal'
  }
  scope: ai
}