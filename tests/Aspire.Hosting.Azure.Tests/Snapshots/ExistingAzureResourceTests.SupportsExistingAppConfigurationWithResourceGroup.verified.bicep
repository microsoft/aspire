@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' existing = {
  name: existingResourceName
}

output appConfigEndpoint string = appConfig.properties.endpoint

output name string = appConfig.name

output id string = appConfig.id