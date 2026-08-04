@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param hostmi_outputs_id string

param userPrincipalId string

param principalType string

resource hostgroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('hostgroup${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${hostmi_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'hostgroup'
  }
}

resource hostgroup_deploymentPrincipalDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostgroup.id, userPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
    principalType: principalType
  }
  scope: hostgroup
}

output id string = hostgroup.id

output name string = hostgroup.name