@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

resource law_appInsights 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('lawappInsights-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: {
    'aspire-resource-name': 'law_appInsights'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: take('appInsights-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: law_appInsights.id
  }
  tags: {
    'aspire-resource-name': 'appInsights'
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString

output name string = appInsights.name

output id string = appInsights.id
