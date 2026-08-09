@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource customregistry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('customregistry${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'customregistry'
  }
}

output name string = customregistry.name

output loginServer string = customregistry.properties.loginServer

output id string = customregistry.id