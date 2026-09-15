@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource projmyproject_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('projmyprojectacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'projmyproject-acr'
  }
}

output name string = projmyproject_acr.name

output loginServer string = projmyproject_acr.properties.loginServer

output id string = projmyproject_acr.id