@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource infra_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('infraacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'infra-acr'
  }
}

output name string = infra_acr.name

output loginServer string = infra_acr.properties.loginServer