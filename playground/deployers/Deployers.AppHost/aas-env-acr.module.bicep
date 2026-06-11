@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aas_env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('aasenvacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'aas-env-acr'
  }
}

output name string = aas_env_acr.name

output loginServer string = aas_env_acr.properties.loginServer