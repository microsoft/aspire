@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource weather_hosted_agent_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('weather_hosted_agent_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = weather_hosted_agent_identity.id

output clientId string = weather_hosted_agent_identity.properties.clientId

output principalId string = weather_hosted_agent_identity.properties.principalId

output principalName string = weather_hosted_agent_identity.name

output name string = weather_hosted_agent_identity.name