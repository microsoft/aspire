@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param identity_outputs_id string

param identity_outputs_clientid string

resource sandbox 'Microsoft.App/sandboxGroups@2026-02-01-preview' existing = {
  name: 'existing-sandbox'
}

output id string = sandbox.id

output name string = sandbox.name

output location string = sandbox.location

output tenantId string = tenant().tenantId

output imagePullIdentityClientId string = identity_outputs_clientid