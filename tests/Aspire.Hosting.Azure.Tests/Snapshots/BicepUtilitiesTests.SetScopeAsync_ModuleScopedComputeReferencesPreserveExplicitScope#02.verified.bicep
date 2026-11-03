@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sandbox 'Microsoft.App/sandboxGroups@2026-02-01-preview' existing = {
  name: 'existing-sandbox'
  scope: resourceGroup('module-subscription', 'module-rg')
}