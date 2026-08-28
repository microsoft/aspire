@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param pull_identity_outputs_clientid string

resource sandboxes 'Microsoft.App/sandboxGroups@2026-02-01-preview' existing = {
  name: 'existing-sandboxes'
}

output id string = sandboxes.id

output name string = sandboxes.name

output location string = sandboxes.location

output acrPullIdentityClientId string = pull_identity_outputs_clientid