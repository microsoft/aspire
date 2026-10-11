@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sandboxes 'Microsoft.App/sandboxGroups@2026-02-01-preview' existing = {
  name: 'existing-sandboxes'
}

output id string = sandboxes.id

output name string = sandboxes.name

output location string = sandboxes.location

output endpoint string = 'https://management.${sandboxes.location}.azuredevcompute.io'

output subscriptionId string = subscription().subscriptionId

output resourceGroup string = resourceGroup().name

output connectionString string = 'Endpoint=https://management.${sandboxes.location}.azuredevcompute.io;SubscriptionId=${subscription().subscriptionId};ResourceGroup=${resourceGroup().name};SandboxGroupName=existing-sandboxes'