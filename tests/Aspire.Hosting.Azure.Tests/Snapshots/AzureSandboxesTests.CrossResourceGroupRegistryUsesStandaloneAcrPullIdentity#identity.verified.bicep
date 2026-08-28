@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param tags object = { }

resource sandboxes_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('sandboxes_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

output id string = sandboxes_mi.id

output clientId string = sandboxes_mi.properties.clientId

output principalId string = sandboxes_mi.properties.principalId

output principalName string = sandboxes_mi.name

output name string = sandboxes_mi.name