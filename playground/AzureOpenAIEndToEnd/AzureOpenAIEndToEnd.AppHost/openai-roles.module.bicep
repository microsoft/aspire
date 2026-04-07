@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param openai_outputs_name string

param principalType string

param principalId string

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: openai_outputs_name
}

resource openai_CognitiveServicesOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalType: principalType
  }
  scope: openai
}