@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param registry_name string

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: registry_name
}

output name string = registry.name

output loginServer string = registry.properties.loginServer

output id string = registry.id