@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param signalr_outputs_name string

param principalType string

param principalId string

resource signalr 'Microsoft.SignalRService/signalR@2024-03-01' existing = {
  name: signalr_outputs_name
}

resource signalr_SignalRAppServer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(signalr.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '420fcaa2-552c-430f-98ca-3264be4806c7'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '420fcaa2-552c-430f-98ca-3264be4806c7')
    principalType: principalType
  }
  scope: signalr
}

resource signalr_SignalRRestApiOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(signalr.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'fd53cd77-2268-407a-8f46-7e7863d0f521'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'fd53cd77-2268-407a-8f46-7e7863d0f521')
    principalType: principalType
  }
  scope: signalr
}