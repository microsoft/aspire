@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aca_env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('acaenvacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'aca-env-acr'
  }
}

output name string = aca_env_acr.name

output loginServer string = aca_env_acr.properties.loginServer