@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = env_mi.id

output clientId string = env_mi.properties.clientId

output principalId string = env_mi.properties.principalId

output principalName string = env_mi.name

output name string = env_mi.name