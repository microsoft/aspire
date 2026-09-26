@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param hostmi_outputs_id string

param hostgroup_acr_outputs_name string

param userPrincipalId string

param principalType string

resource hostgroup_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('hostgroup_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource hostgroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('hostgroup-${uniqueString(resourceGroup().id)}', 63)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${hostgroup_mi.id}': { }
      '${hostmi_outputs_id}': { }
    }
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'hostgroup'
  }
}

resource hostgroup_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: hostgroup_acr_outputs_name
}

resource hostgroup_acr_hostgroup_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostgroup_acr.id, hostgroup_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: hostgroup_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: hostgroup_acr
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

output location string = hostgroup.location

output imagePullIdentityClientId string = hostgroup_mi.properties.clientId