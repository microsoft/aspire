@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource appconfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
  name: take('appconfig-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: 'standard'
  }
  tags: {
    'aspire-resource-name': 'appconfig'
  }
}

output appConfigEndpoint string = appconfig.properties.endpoint

output name string = appconfig.name