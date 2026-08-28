@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource media_volume_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('media_volume_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = media_volume_identity.id

output clientId string = media_volume_identity.properties.clientId

output principalId string = media_volume_identity.properties.principalId

output principalName string = media_volume_identity.name

output name string = media_volume_identity.name