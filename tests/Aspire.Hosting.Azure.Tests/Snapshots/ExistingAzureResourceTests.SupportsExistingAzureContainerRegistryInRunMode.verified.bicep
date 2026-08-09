@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: existingResourceName
}

output name string = acr.name

output loginServer string = acr.properties.loginServer

output id string = acr.id