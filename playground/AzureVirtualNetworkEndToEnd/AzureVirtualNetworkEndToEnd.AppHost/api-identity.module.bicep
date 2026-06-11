@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource api_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('api_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = api_identity.id

output clientId string = api_identity.properties.clientId

output principalId string = api_identity.properties.principalId

output principalName string = api_identity.name

output name string = api_identity.name