@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existing_sb_name string

param existing_sb_subscription string

param existing_sb_rg string

resource test_servicebus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: existing_sb_name
  scope: resourceGroup(existing_sb_subscription, existing_sb_rg)
}