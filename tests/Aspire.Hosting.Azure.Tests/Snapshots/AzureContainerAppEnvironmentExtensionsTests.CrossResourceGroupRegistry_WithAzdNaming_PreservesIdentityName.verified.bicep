@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'mi-${resourceToken}'
  location: location
  tags: tags
}

var resourceToken = uniqueString(resourceGroup().id)

output id string = env_mi.id

output clientId string = env_mi.properties.clientId

output principalId string = env_mi.properties.principalId

output principalName string = env_mi.name

output name string = env_mi.name