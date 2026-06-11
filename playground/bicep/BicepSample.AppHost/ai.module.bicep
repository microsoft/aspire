@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

param lawkspc_outputs_loganalyticsworkspaceid string

resource ai 'Microsoft.Insights/components@2020-02-02' = {
  name: take('ai-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: lawkspc_outputs_loganalyticsworkspaceid
  }
  tags: {
    'aspire-resource-name': 'ai'
  }
}

output appInsightsConnectionString string = ai.properties.ConnectionString

output name string = ai.name

output id string = ai.id