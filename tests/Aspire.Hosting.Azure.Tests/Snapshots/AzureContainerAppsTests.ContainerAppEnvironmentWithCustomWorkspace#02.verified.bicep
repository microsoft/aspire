@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource customworkspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('customworkspace-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: {
    'aspire-resource-name': 'customworkspace'
  }
}

output logAnalyticsWorkspaceId string = customworkspace.id

output name string = customworkspace.name