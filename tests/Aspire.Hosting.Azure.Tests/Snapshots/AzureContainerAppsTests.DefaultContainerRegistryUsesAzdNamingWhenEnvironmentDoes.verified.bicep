@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

var resourceToken = uniqueString(resourceGroup().id)

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: replace('acr-${resourceToken}', '-', '')
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'env-acr'
  }
}

output name string = env_acr.name

output loginServer string = env_acr.properties.loginServer

output id string = env_acr.id