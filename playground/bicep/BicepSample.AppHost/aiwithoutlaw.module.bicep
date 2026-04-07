@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

resource law_aiwithoutlaw 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('lawaiwithoutlaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: {
    'aspire-resource-name': 'law_aiwithoutlaw'
  }
}

resource aiwithoutlaw 'Microsoft.Insights/components@2020-02-02' = {
  name: take('aiwithoutlaw-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: law_aiwithoutlaw.id
  }
  tags: {
    'aspire-resource-name': 'aiwithoutlaw'
  }
}

output appInsightsConnectionString string = aiwithoutlaw.properties.ConnectionString

output name string = aiwithoutlaw.name

output id string = aiwithoutlaw.id