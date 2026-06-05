@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param searchSku string

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: take('search-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    hostingMode: 'default'
    disableLocalAuth: true
    partitionCount: 1
    replicaCount: 1
  }
  sku: {
    name: searchSku
  }
  tags: {
    'aspire-resource-name': 'search'
  }
}

output connectionString string = 'Endpoint=https://${search.name}.search.windows.net'

output endpoint string = 'https://${search.name}.search.windows.net'

output name string = search.name