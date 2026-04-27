@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource myregistry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('myregistry${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'myregistry'
  }
}

output name string = myregistry.name

output loginServer string = myregistry.properties.loginServer

output id string = myregistry.id