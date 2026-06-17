@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string

param principalType string

resource workergroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('workergroup${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  tags: {
    'aspire-resource-name': 'workergroup'
  }
}

resource workergroup_deploymentPrincipalDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workergroup.id, userPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
    principalType: principalType
  }
  scope: workergroup
}

output id string = workergroup.id

output name string = workergroup.name