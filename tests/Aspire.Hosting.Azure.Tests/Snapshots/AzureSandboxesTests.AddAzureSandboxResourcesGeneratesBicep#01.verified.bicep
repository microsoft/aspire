@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

param workergroup_acr_outputs_name string

param userPrincipalId string

resource workergroup_acr_pull_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('workergroup_acr_pull_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource workergroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('workergroup-${uniqueString(resourceGroup().id)}', 63)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workergroup_acr_pull_mi.id}': { }
    }
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'workergroup'
  }
}

resource workergroup_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: workergroup_acr_outputs_name
}

resource workergroup_acr_workergroup_acr_pull_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workergroup_acr.id, workergroup_acr_pull_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: workergroup_acr_pull_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: workergroup_acr
}

resource workergroup_deploymentPrincipalDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workergroup.id, userPrincipalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c'))
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')
  }
  scope: workergroup
}

output id string = workergroup.id

output name string = workergroup.name

output location string = workergroup.location

output acrPullIdentityClientId string = workergroup_acr_pull_mi.properties.clientId