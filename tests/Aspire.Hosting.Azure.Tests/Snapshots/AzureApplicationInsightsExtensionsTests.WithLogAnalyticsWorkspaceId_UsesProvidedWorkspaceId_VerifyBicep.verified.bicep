@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

param aca_outputs_azure_log_analytics_workspace_id string

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: take('appInsights-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: aca_outputs_azure_log_analytics_workspace_id
  }
  tags: {
    'aspire-resource-name': 'appInsights'
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString

output name string = appInsights.name

output id string = appInsights.id
