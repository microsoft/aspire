@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource env 'Microsoft.App/managedEnvironments@2026-07-01' existing = {
  name: 'existing-env'
}