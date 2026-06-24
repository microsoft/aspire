@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param hostgroup_outputs_name string

param principalId string

resource hostgroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' existing = {
  name: hostgroup_outputs_name
}

resource hostgroup_SandboxGroup_Data_Owner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostgroup.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
    principalType: 'ServicePrincipal'
  }
  scope: hostgroup
}