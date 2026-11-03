@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource env 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: 'existing-env'
}