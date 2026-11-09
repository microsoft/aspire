@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sandboxes 'Microsoft.App/sandboxGroups@2026-07-01' = {
  name: take('sandboxes-${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'sandboxes'
  }
  location: location
}

output id string = sandboxes.id

output name string = sandboxes.name

output location string = sandboxes.location

output endpoint string = 'https://management.${sandboxes.location}.azuredevcompute.io'

output subscriptionId string = subscription().subscriptionId

output resourceGroup string = resourceGroup().name

output connectionString string = 'Endpoint=https://management.${sandboxes.location}.azuredevcompute.io;SubscriptionId=${subscription().subscriptionId};ResourceGroup=${resourceGroup().name};SandboxGroupName=${sandboxes.name}'