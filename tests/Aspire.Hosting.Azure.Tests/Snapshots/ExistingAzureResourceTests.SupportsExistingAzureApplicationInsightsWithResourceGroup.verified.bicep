@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: existingResourceName
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString

output name string = appInsights.name

output id string = appInsights.id