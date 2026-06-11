@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource functions_api_service_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('functions_api_service_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = functions_api_service_identity.id

output clientId string = functions_api_service_identity.properties.clientId

output principalId string = functions_api_service_identity.properties.principalId

output principalName string = functions_api_service_identity.name

output name string = functions_api_service_identity.name