@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource weather_python_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('weather_python_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = weather_python_identity.id

output clientId string = weather_python_identity.properties.clientId

output principalId string = weather_python_identity.properties.principalId

output principalName string = weather_python_identity.name

output name string = weather_python_identity.name