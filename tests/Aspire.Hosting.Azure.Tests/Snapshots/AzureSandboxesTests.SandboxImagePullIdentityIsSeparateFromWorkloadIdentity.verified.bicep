@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param pull_identity_outputs_clientid string

param pull_identity_outputs_id string

param workload_identity_outputs_id string

param userPrincipalId string

resource sandboxes 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('sandboxes-${uniqueString(resourceGroup().id)}', 63)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${pull_identity_outputs_id}': { }
      '${workload_identity_outputs_id}': { }
    }
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'sandboxes'
  }
}

resource sandboxes_deploymentPrincipalDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxes.id, userPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
  }
  scope: sandboxes
}

output id string = sandboxes.id

output name string = sandboxes.name

output location string = sandboxes.location

output acrPullIdentityClientId string = pull_identity_outputs_clientid