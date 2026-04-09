@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('logAnalyticsWorkspace-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: {
    'aspire-resource-name': 'logAnalyticsWorkspace'
  }
}

output logAnalyticsWorkspaceId string = logAnalyticsWorkspace.id

output name string = logAnalyticsWorkspace.name