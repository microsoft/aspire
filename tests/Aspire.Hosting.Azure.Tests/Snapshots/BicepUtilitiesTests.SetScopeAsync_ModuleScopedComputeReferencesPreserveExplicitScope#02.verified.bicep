@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sandbox 'Microsoft.App/sandboxGroups@2026-07-01' existing = {
  name: 'existing-sandbox'
  scope: resourceGroup('module-subscription', 'module-rg')
}