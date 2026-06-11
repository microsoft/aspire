@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource func_app_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('func_app_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = func_app_identity.id

output clientId string = func_app_identity.properties.clientId

output principalId string = func_app_identity.properties.principalId

output principalName string = func_app_identity.name

output name string = func_app_identity.name