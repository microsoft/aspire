@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('aksacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'aks-acr'
  }
}

output name string = aks_acr.name

output loginServer string = aks_acr.properties.loginServer

output id string = aks_acr.id