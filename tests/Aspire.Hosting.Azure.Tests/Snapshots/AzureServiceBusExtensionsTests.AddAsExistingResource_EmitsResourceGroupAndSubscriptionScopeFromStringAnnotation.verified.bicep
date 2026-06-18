@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource test_servicebus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: 'existing-sb'
  scope: resourceGroup('00000000-0000-0000-0000-000000000000', 'existing-rg')
}