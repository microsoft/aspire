@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource proj_myproject_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('projmyprojectacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'proj-myproject-acr'
  }
}

output name string = proj_myproject_acr.name

output loginServer string = proj_myproject_acr.properties.loginServer

output id string = proj_myproject_acr.id