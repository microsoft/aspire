@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param compute_identity_outputs_id string

param sandboxes_acr_outputs_name string

param principalId string

resource sandboxes_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('sandboxes_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource sandboxes 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('sandboxes-${uniqueString(resourceGroup().id)}', 63)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sandboxes_mi.id}': { }
      '${compute_identity_outputs_id}': { }
    }
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'sandboxes'
  }
}

resource sandboxes_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: sandboxes_acr_outputs_name
}

resource sandboxes_acr_sandboxes_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxes_acr.id, sandboxes_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: sandboxes_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: sandboxes_acr
}

resource sandboxes_deploymentPrincipalDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxes.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
  }
  scope: sandboxes
}

output id string = sandboxes.id

output name string = sandboxes.name

output location string = sandboxes.location

output imagePullIdentityClientId string = sandboxes_mi.properties.clientId