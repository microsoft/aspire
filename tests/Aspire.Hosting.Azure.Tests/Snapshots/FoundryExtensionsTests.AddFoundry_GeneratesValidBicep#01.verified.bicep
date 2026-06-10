@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param foundry_outputs_name string

param principalType string

param principalId string

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: foundry_outputs_name
}

resource foundry_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: principalType
  }
  scope: foundry
}

resource foundry_CognitiveServicesOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalType: principalType
  }
  scope: foundry
}